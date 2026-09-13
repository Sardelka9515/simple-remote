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
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(120, 120, 130) };

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

        Text = "Simple Remote - Pair a device";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 640);
        Size = new Size(560, 720);
        Icon = AppIcon.Create(connected: false);
        Font = new Font("Segoe UI", 9f);

        BuildLayout();

        _server.Devices.Changed += OnDevicesChanged;
        _refresh.Tick += (_, _) => RegenerateQr();

        PopulateAddresses();
        RefreshDevices();
        UpdateStatus();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(16),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // instructions
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // qr
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));   // url
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));   // address picker
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // status
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));  // devices
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));   // buttons

        root.Controls.Add(new Label
        {
            Text = "Scan with your phone camera. No app to install.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 11f, FontStyle.Regular),
        }, 0, 0);

        root.Controls.Add(_qr, 0, 1);
        root.Controls.Add(_url, 0, 2);

        var addressRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addressRow.Controls.Add(new Label
        {
            Text = "Network:",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);
        addressRow.Controls.Add(_addresses, 1, 0);
        root.Controls.Add(addressRow, 0, 3);

        root.Controls.Add(_status, 0, 4);

        _devices.Columns.Add("Paired device", 240);
        _devices.Columns.Add("Last seen", 150);
        _devices.Columns.Add("Status", 90);
        root.Controls.Add(_devices, 0, 5);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };

        var close = new Button { Text = "Close", Width = 90, Height = 30 };
        close.Click += (_, _) => Close();

        var revoke = new Button { Text = "Revoke selected", Width = 130, Height = 30 };
        revoke.Click += (_, _) => RevokeSelected();

        var copy = new Button { Text = "Copy link", Width = 90, Height = 30 };
        copy.Click += (_, _) => CopyLink();

        var newCode = new Button { Text = "New code", Width = 90, Height = 30 };
        newCode.Click += (_, _) => RegenerateQr();

        var firewall = new Button { Text = "Fix firewall", Width = 100, Height = 30 };
        firewall.Click += (_, _) => FixFirewall();

        buttons.Controls.AddRange([close, revoke, copy, newCode, firewall]);
        root.Controls.Add(buttons, 0, 6);

        Controls.Add(root);

        _addresses.SelectedIndexChanged += (_, _) => { if (!_suppressAddressEvents) RegenerateQr(); };
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
