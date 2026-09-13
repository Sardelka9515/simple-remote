using System.Windows.Forms;
using SimpleRemote.Clipboard;
using SimpleRemote.Config;
using SimpleRemote.Input;
using SimpleRemote.Media;
using SimpleRemote.Pairing;
using SimpleRemote.Server;
using SimpleRemote.Shortcuts;
using SimpleRemote.Tray;

namespace SimpleRemote;

internal static class Program
{
    /// <summary>Keeps the second launch from silently fighting the first over the port.</summary>
    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main(string[] args)
    {
        _singleInstance = new Mutex(initiallyOwned: true, "SimpleRemote.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show(
                "Simple Remote is already running. Look for the icon in the system tray.",
                "Simple Remote", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        var config = new ConfigStore();
        config.Load();

        var devices = new DeviceStore();
        var pairing = new PairingTokenSource();
        var injector = new InputInjector();
        var media = new MediaController(injector);
        var volume = new VolumeController();
        var clipboard = new ClipboardService();
        var shortcuts = new ShortcutService(config, injector);

        shortcuts.Reload();
        volume.Initialize();
        clipboard.Start();

        var server = new RemoteServer(config, devices, pairing, injector, media, volume, clipboard, shortcuts);
        var host = new WebHost(server);

        try
        {
            // Kestrel is started synchronously so a port clash is reported as a dialog rather than
            // leaving a tray icon that quietly does nothing.
            host.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not start the server on port {config.Current.Port}.\n\n{ex.Message}\n\n" +
                "Another program may be using that port. Change it in config.json and restart.",
                "Simple Remote", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Media init is async and touches WinRT; it must not block the UI thread coming up.
        _ = media.InitializeAsync();

        using var tray = new TrayApplicationContext(server, host);

        if (!args.Contains("--minimized", StringComparer.OrdinalIgnoreCase) && devices.Devices.Count == 0)
        {
            // Nothing paired yet, so the only useful first action is showing the QR.
            ShowPairingOnStartup(server, host);
        }

        Application.Run(tray);

        // Ordered teardown: stop accepting first, then release the machine-wide hooks.
        host.StopAsync().GetAwaiter().GetResult();
        server.Dispose();
        clipboard.Dispose();
        volume.Dispose();
        media.Dispose();
        devices.Flush();
        _singleInstance.Dispose();
    }

    private static void ShowPairingOnStartup(RemoteServer server, WebHost host)
    {
        var form = new PairingForm(server, host);
        form.Show();
    }
}
