using System.Text.Json;
using SimpleRemote.Clipboard;
using SimpleRemote.Config;
using SimpleRemote.Input;
using SimpleRemote.Media;
using SimpleRemote.Pairing;
using SimpleRemote.Server;
using SimpleRemote.Shortcuts;
using Xunit;

namespace SimpleRemote.Tests;

public class ConfigMessageTests
{
    /// <summary>
    /// Builds a server with the real collaborators but nothing started, which is enough to
    /// exercise the config message the client depends on.
    /// </summary>
    private static RemoteServer BuildServer(out ShortcutService shortcuts)
    {
        var config = new ConfigStore();
        var injector = new InputInjector();
        shortcuts = new ShortcutService(config, injector);
        shortcuts.Reload();

        return new RemoteServer(
            config,
            new DeviceStore(Path.Combine(Path.GetTempPath(), $"sr-cfg-{Guid.NewGuid():N}.json")),
            new PairingTokenSource(),
            injector,
            new MediaController(injector),
            new VolumeController(),
            new ClipboardService(),
            shortcuts);
    }

    /// <summary>
    /// The client scales pointer gain by desktop size over pad size. If these arrive as zero it
    /// silently falls back to a guess, which is the sluggish-cursor bug in another costume.
    /// </summary>
    [Fact]
    public void ConfigCarriesRealScreenDimensions()
    {
        using var server = BuildServer(out _);

        var message = server.DescribeConfig();

        Assert.True(message.Pointer.ScreenWidth > 0, "screen width must be discoverable");
        Assert.True(message.Pointer.ScreenHeight > 0, "screen height must be discoverable");
    }

    [Fact]
    public void ConfigCarriesPointerSettingsAndShortcuts()
    {
        using var server = BuildServer(out var shortcuts);

        var message = server.DescribeConfig();

        Assert.True(message.Pointer.Sensitivity > 0);
        Assert.True(message.Pointer.MaxSpeed > 0);
        Assert.Equal(shortcuts.Describe().Count, message.Shortcuts.Count);
    }

    [Fact]
    public void ConfigSerializesScreenSizeToCamelCase()
    {
        using var server = BuildServer(out _);

        var json = JsonSerializer.Serialize(server.DescribeConfig(), AppJson.Default.ConfigMessage);

        Assert.Contains("\"screenWidth\"", json);
        Assert.Contains("\"screenHeight\"", json);
    }

    /// <summary>
    /// Defaults must stay inside the range the on-screen slider can correct from, so a bad default
    /// is always recoverable without editing a file on the PC.
    /// </summary>
    [Fact]
    public void DefaultPointerSettingsAreSane()
    {
        var pointer = new PointerConfig();

        Assert.InRange(pointer.Sensitivity, 0.1, 2.0);
        Assert.InRange(pointer.Acceleration, 0.0, 2.0);
        Assert.InRange(pointer.MaxSpeed, 1.0, 10.0);
        Assert.InRange(pointer.ScrollSpeed, 0.1, 5.0);
    }

    /// <summary>
    /// Every shortcut that ships by default must actually parse, or it is dropped at startup and
    /// the button never appears.
    /// </summary>
    [Fact]
    public void AllDefaultShortcutsCompile()
    {
        var config = new ConfigStore();
        var shortcuts = new ShortcutService(config, new InputInjector());
        shortcuts.Reload();

        Assert.Empty(shortcuts.Errors);
        Assert.Equal(ShortcutDefinition.Defaults().Count, shortcuts.Describe().Count);
    }
}
