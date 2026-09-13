using System.Diagnostics;
using System.Windows.Forms;
using SimpleRemote.Config;
using SimpleRemote.Platform;
using SimpleRemote.Server;

namespace SimpleRemote.Tray;

/// <summary>
/// Owns the tray icon and the application lifetime.
///
/// There is no main window on purpose: the app is a background service with a pairing dialog, so
/// the tray icon is the whole UI most of the time.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly RemoteServer _server;
    private readonly WebHost _host;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _statusItem;

    private PairingForm? _pairingForm;
    private bool _announcedFirstConnection;

    public TrayApplicationContext(RemoteServer server, WebHost host)
    {
        _server = server;
        _host = host;

        _statusItem = new ToolStripMenuItem("Starting...") { Enabled = false };
        _autoStartItem = new ToolStripMenuItem("Start with Windows", null, OnToggleAutoStart)
        {
            Checked = AutoStart.IsEnabled,
            CheckOnClick = false,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Pair a device...", null, (_, _) => ShowPairing()) { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem("Open remote in browser", null, (_, _) => OpenInBrowser()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripMenuItem("Edit settings file", null, (_, _) => OpenConfig()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Create(connected: false, SystemInformation.SmallIconSize.Width),
            Text = "Simple Remote",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowPairing();

        _server.ConnectionsChanged += OnConnectionsChanged;
        UpdateStatus();
    }

    private void OnConnectionsChanged()
    {
        // Fired from socket threads; the tray icon is UI state.
        if (_tray.ContextMenuStrip is { IsHandleCreated: true } menu)
            menu.BeginInvoke(UpdateStatus);
        else
            UpdateStatus();
    }

    private void UpdateStatus()
    {
        var count = _server.ConnectedCount;
        var connected = count > 0;

        _statusItem.Text = connected
            ? $"{count} device{(count == 1 ? "" : "s")} connected"
            : $"Waiting on port {_host.Port}";

        var previous = _tray.Icon;
        _tray.Icon = AppIcon.Create(connected, SystemInformation.SmallIconSize.Width);
        previous?.Dispose();

        _tray.Text = connected ? $"Simple Remote - {count} connected" : "Simple Remote - waiting";

        if (connected && !_announcedFirstConnection)
        {
            _announcedFirstConnection = true;
            _tray.ShowBalloonTip(3000, "Simple Remote", "A device is now connected.", ToolTipIcon.Info);
        }
    }

    private void ShowPairing()
    {
        if (_pairingForm is { IsDisposed: false })
        {
            _pairingForm.WindowState = FormWindowState.Normal;
            _pairingForm.Activate();
            return;
        }

        _pairingForm = new PairingForm(_server, _host);
        _pairingForm.FormClosed += (_, _) => _pairingForm = null;
        _pairingForm.Show();
    }

    private void OpenInBrowser()
    {
        try
        {
            var url = $"{_host.Scheme}://localhost:{_host.Port}/";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private void OnToggleAutoStart(object? sender, EventArgs e)
    {
        var target = !_autoStartItem.Checked;
        if (AutoStart.TrySet(target)) _autoStartItem.Checked = target;
    }

    private void OpenConfig()
    {
        try
        {
            _server.Config.Save(); // make sure the file exists before opening it
            Process.Start(new ProcessStartInfo(_server.Config.FilePath) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private void ExitApp()
    {
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _server.ConnectionsChanged -= OnConnectionsChanged;
            _tray.Visible = false;
            _tray.Icon?.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
