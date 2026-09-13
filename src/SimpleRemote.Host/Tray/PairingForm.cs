using System.Drawing;
using System.Windows.Forms;
using QRCoder;
using SimpleRemote.Pairing;
using SimpleRemote.Net;
using SimpleRemote.Platform;
using SimpleRemote.Server;

namespace SimpleRemote.Tray;

/// <summary>
/// The pairing window: a QR code, the address it encodes, and the list of devices already paired.
///
/// This is the entire onboarding flow, so it is built to be understood without instructions -
/// the QR is large, the URL is shown as readable text underneath for anyone who would rather type
/// it, and the reasons pairing usually fails (wrong network adapter, blocked firewall) are
/// surfaced here rather than left to fail silently on the phone.
/// </summary>
public sealed class PairingForm : Form
{
    private readonly RemoteServer _server;
    private readonly WebHost _host;

    private readonly PictureBox _qr = new() { SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill };
    private readonly ComboBox _addresses = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _url = new() { ReadOnly = true, Dock = DockStyle.Fill, TextAlign = HorizontalAlignment.Center, BorderStyle = BorderStyle.None };
    private readonly ListView _devices = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, MultiSelect = false };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(120, 120, 130), AutoEllipsis = true };

    private TableLayoutPanel? _root;
    private Label? _networkLabel;
    private readonly Button _close = new() { Text = "Close", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly };
    private readonly Button _revoke = new() { Text = "Revoke selected", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly };
    private readonly Button _copy = new() { Text = "Copy link", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly };
    private readonly Button _newCode = new() { Text = "New code", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly };
    private readonly Button _firewall = new() { Text = "Fix firewall", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly };

    /// <summary>
    /// Refreshes the code before its 5 minute lifetime runs out. Without this, a window left open
    /// while the user wanders off shows a QR that scans perfectly and then fails - the exact
    /// silent dead end this screen exists to prevent.
    /// </summary>
    private readonly System.Windows.Forms.Timer _refresh = new()
    {
        Interval = (int)(PairingTokenSource.Lifetime.TotalMilliseconds * 0.8),
    };

    private string _pairingUrl = "";
    private bool _suppressAddressEvents;

    public PairingForm(RemoteServer server, WebHost host)
    {
        _server = server;
        _host = host;

        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);

        Text = "Simple Remote - Pair a device";
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.Create(connected: false);

        var scale = DeviceDpi / 96f;
        ClientSize = new Size((int)(600 * scale), (int)(720 * scale));
        MinimumSize = new Size((int)(540 * scale), (int)(620 * scale));

        BuildLayout();

        _server.Devices.Changed += OnDevicesChanged;
        _refresh.Tick += (_, _) => RegenerateQr();

        PopulateAddresses();
        RefreshDevices();
        UpdateStatus();
    }

    private void BuildLayout()
    {
        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(LogicalToDeviceUnits(16)),
        };
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));                      // instructions
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));                  // qr
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));                      // url
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));                      // address picker
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, LogicalToDeviceUnits(28))); // status
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, LogicalToDeviceUnits(140)));// devices
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));                      // buttons

        var instructions = new Label
        {
            Text = "Scan with your phone camera. No app to install.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, 11f, FontStyle.Regular),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(6)),
        };
        _root.Controls.Add(instructions, 0, 0);

        _root.Controls.Add(_qr, 0, 1);

        _url.Margin = new Padding(0, LogicalToDeviceUnits(4), 0, LogicalToDeviceUnits(4));
        _root.Controls.Add(_url, 0, 2);

        var addressRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, LogicalToDeviceUnits(4), 0, LogicalToDeviceUnits(4)),
        };
        addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _networkLabel = new Label
        {
            Text = "Network:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, LogicalToDeviceUnits(8), 0),
        };
        addressRow.Controls.Add(_networkLabel, 0, 0);
        addressRow.Controls.Add(_addresses, 1, 0);
        _root.Controls.Add(addressRow, 0, 3);

        _status.Margin = new Padding(0, LogicalToDeviceUnits(4), 0, LogicalToDeviceUnits(4));
        _root.Controls.Add(_status, 0, 4);

        _devices.Columns.Add("Paired device", LogicalToDeviceUnits(220));
        _devices.Columns.Add("Last seen", LogicalToDeviceUnits(140));
        _devices.Columns.Add("Status", LogicalToDeviceUnits(90));
        _devices.Resize += (_, _) => AdjustDeviceColumnWidths();
        _root.Controls.Add(_devices, 0, 5);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Margin = new Padding(0, LogicalToDeviceUnits(6), 0, 0),
        };

        _close.Click += (_, _) => Close();
        _revoke.Click += (_, _) => RevokeSelected();
        _copy.Click += (_, _) => CopyLink();
        _newCode.Click += (_, _) => RegenerateQr();
        _firewall.Click += (_, _) => FixFirewall();

        ConfigureButton(_close, 85);
        ConfigureButton(_revoke, 125);
        ConfigureButton(_copy, 90);
        ConfigureButton(_newCode, 90);
        ConfigureButton(_firewall, 100);

        buttons.Controls.AddRange([_close, _revoke, _copy, _newCode, _firewall]);
        _root.Controls.Add(buttons, 0, 6);

        Controls.Add(_root);

        _addresses.SelectedIndexChanged += (_, _) => { if (!_suppressAddressEvents) RegenerateQr(); };

        AdjustDeviceColumnWidths();
    }

    private void ConfigureButton(Button button, int baseWidth)
    {
        button.MinimumSize = new Size(LogicalToDeviceUnits(baseWidth), LogicalToDeviceUnits(32));
        button.Margin = new Padding(LogicalToDeviceUnits(3), LogicalToDeviceUnits(3), LogicalToDeviceUnits(3), LogicalToDeviceUnits(3));
        button.Padding = new Padding(LogicalToDeviceUnits(8), 0, LogicalToDeviceUnits(8), 0);
    }

    private void AdjustDeviceColumnWidths()
    {
        if (_devices.Columns.Count < 3) return;
        var lastSeenWidth = LogicalToDeviceUnits(140);
        var statusWidth = LogicalToDeviceUnits(90);
        var remaining = _devices.ClientSize.Width - lastSeenWidth - statusWidth - 4;
        _devices.Columns[0].Width = Math.Max(LogicalToDeviceUnits(160), remaining);
        _devices.Columns[1].Width = lastSeenWidth;
        _devices.Columns[2].Width = statusWidth;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        RescaleLayout();
    }

    private void RescaleLayout()
    {
        var scale = DeviceDpi / 96f;
        MinimumSize = new Size((int)(540 * scale), (int)(620 * scale));

        if (_root != null)
        {
            _root.Padding = new Padding(LogicalToDeviceUnits(16));
            if (_root.RowStyles.Count >= 7)
            {
                _root.RowStyles[4].Height = LogicalToDeviceUnits(28);
                _root.RowStyles[5].Height = LogicalToDeviceUnits(140);
            }
        }

        if (_networkLabel != null)
            _networkLabel.Margin = new Padding(0, 0, LogicalToDeviceUnits(8), 0);

        ConfigureButton(_close, 85);
        ConfigureButton(_revoke, 125);
        ConfigureButton(_copy, 90);
        ConfigureButton(_newCode, 90);
        ConfigureButton(_firewall, 100);

        AdjustDeviceColumnWidths();
    }

    private void PopulateAddresses()
    {
        // Clearing and refilling the combo raises SelectedIndexChanged twice. Left unsuppressed
        // that mints (and immediately invalidates) two extra pairing tokens every time the window
        // opens, so the code on screen is only valid by luck of ordering.
        _suppressAddressEvents = true;
        try
        {
            _addresses.Items.Clear();

            foreach (var candidate in LocalAddresses.Enumerate())
                _addresses.Items.Add(new AddressItem(candidate.Address, $"{candidate.Address}  -  {candidate.InterfaceName}"));

            if (_addresses.Items.Count == 0)
                _addresses.Items.Add(new AddressItem("127.0.0.1", "127.0.0.1  -  no network detected"));

            // Honour an explicit pin, otherwise take the best-ranked adapter.
            var preferred = _server.Config.Current.PreferredAddress;
            var index = 0;
            for (var i = 0; i < _addresses.Items.Count; i++)
            {
                if (_addresses.Items[i] is AddressItem item && item.Address == preferred) { index = i; break; }
            }

            _addresses.SelectedIndex = index;
        }
        finally
        {
            _suppressAddressEvents = false;
        }
    }

    /// <summary>
    /// Mints a fresh single-use token and redraws the QR. Called on open, on adapter change, and
    /// from the New code button - so an expired code is never a dead end.
    /// </summary>
    private void RegenerateQr()
    {
        if (_addresses.SelectedItem is not AddressItem selected) return;

        // Remember the pick so the next QR, and the next launch, use the same adapter.
        if (_server.Config.Current.PreferredAddress != selected.Address)
        {
            _server.Config.Current.PreferredAddress = selected.Address;
            _server.Config.Save();
        }

        var token = _server.Pairing.Issue();

        // The token lives in the fragment, which browsers never put on the wire, so it stays out
        // of request logs and proxy history even though the transport itself is plain HTTP.
        _pairingUrl = $"{_host.Scheme}://{selected.Address}:{_host.Port}/#p={token}";
        _url.Text = _pairingUrl;

        var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(_pairingUrl, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(12);

        var previous = _qr.Image;
        using var stream = new MemoryStream(png);
        _qr.Image = new Bitmap(stream);
        previous?.Dispose();

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var parts = new List<string> { $"Listening on port {_host.Port}" };

        if (!FirewallRule.Exists())
            parts.Add("firewall rule not found - tap Fix firewall if the phone cannot connect");

        if (_server.ConnectedCount > 0)
            parts.Add($"{_server.ConnectedCount} device(s) connected");

        _status.Text = string.Join("  -  ", parts);
    }

    private void CopyLink()
    {
        if (string.IsNullOrEmpty(_pairingUrl)) return;
        try { System.Windows.Forms.Clipboard.SetText(_pairingUrl); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    private void FixFirewall()
    {
        var added = FirewallRule.TryAdd(_host.Port);
        MessageBox.Show(this,
            added
                ? "Firewall rule added for private and domain networks."
                : "Could not add the firewall rule. It needs administrator approval.",
            "Simple Remote",
            MessageBoxButtons.OK,
            added ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

        UpdateStatus();
    }

    private void RevokeSelected()
    {
        if (_devices.SelectedItems.Count == 0) return;
        if (_devices.SelectedItems[0].Tag is not string deviceId) return;

        _server.Devices.Revoke(deviceId);

        // Revoking has to drop any live socket too, or the phone keeps working until it reconnects.
        _server.DisconnectDevice(deviceId);
        RefreshDevices();
    }

    private void OnDevicesChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(RefreshDevices);
    }

    private void RefreshDevices()
    {
        _devices.BeginUpdate();
        _devices.Items.Clear();

        foreach (var device in _server.Devices.Devices.OrderByDescending(d => d.LastSeenUtc))
        {
            var item = new ListViewItem(device.Name) { Tag = device.Id };
            item.SubItems.Add(Describe(device.LastSeenUtc));
            item.SubItems.Add("paired");
            _devices.Items.Add(item);
        }

        if (_devices.Items.Count == 0)
            _devices.Items.Add(new ListViewItem("No devices paired yet") { ForeColor = Color.Gray });

        _devices.EndUpdate();
        UpdateStatus();
    }

    private static string Describe(DateTimeOffset when)
    {
        var age = DateTimeOffset.UtcNow - when;
        return age switch
        {
            { TotalSeconds: < 60 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)age.TotalMinutes} min ago",
            { TotalHours: < 24 } => $"{(int)age.TotalHours} h ago",
            _ => when.ToLocalTime().ToString("d MMM yyyy"),
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // A code minted when the window was last closed may well have expired, so always issue a
        // fresh one on show rather than leaving a stale QR on screen.
        PopulateAddresses();
        RegenerateQr();
        _refresh.Start();
        RescaleLayout();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _refresh.Stop();
        // The token is only meaningful while the code is on screen.
        _server.Pairing.Revoke();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _server.Devices.Changed -= OnDevicesChanged;
            _refresh.Dispose();
            _qr.Image?.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record AddressItem(string Address, string Display)
    {
        public override string ToString() => Display;
    }
}
