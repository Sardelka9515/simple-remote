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

    /// <summary>
    /// Every JSON payload the HTTP surface returns must be source-generated. Under Native AOT
    /// there is no reflection-based serializer to fall back on, so an anonymous type makes the
    /// endpoint return 500 - which is exactly how /healthz broke, and it compiled cleanly.
    /// </summary>
    [Fact]
    public void HealthResponseIsSourceGenerated()
    {
        // Asserts content, not formatting: the context is WriteIndented for readable config.json.
        var json = JsonSerializer.Serialize(new ApiHealthResponse(), AppJson.Default.ApiHealthResponse);

        using var parsed = JsonDocument.Parse(json);
        Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean());
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
    /// A version-1 file holds a sensitivity calibrated as an absolute gain. Applying it to the
    /// screen-relative base makes the cursor about twice as fast as intended, so it must be reset
    /// rather than carried forward - while genuine customisation survives.
    /// </summary>
    [Fact]
    public void StaleConfigHasItsPointerBlockResetButKeepsEverythingElse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sr-migrate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        var file = Path.Combine(path, "config.json");

        File.WriteAllText(file, """
            {
              "port": 9999,
              "preferredAddress": "10.1.2.3",
              "pointer": { "sensitivity": 1.0, "acceleration": 0.5, "maxSpeed": 3.0 },
              "shortcuts": [
                { "id": "mine", "label": "Mine", "action": { "type": "keys", "target": "F5" } }
              ]
            }
            """);

        try
        {
            var store = new ConfigStore(path);
            store.Load();

            // Units changed, so the stale pointer values go.
            Assert.Equal(new PointerConfig().Sensitivity, store.Current.Pointer.Sensitivity);
            Assert.Equal(new PointerConfig().Acceleration, store.Current.Pointer.Acceleration);
            Assert.Equal(AppConfig.CurrentVersion, store.Current.Version);

            // Deliberate customisation is preserved.
            Assert.Equal(9999, store.Current.Port);
            Assert.Equal("10.1.2.3", store.Current.PreferredAddress);
            Assert.Equal("mine", Assert.Single(store.Current.Shortcuts).Id);

            // And the upgrade is persisted, so it happens once.
            Assert.Contains("\"version\": 2", File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void CurrentVersionConfigIsLeftAlone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sr-migrate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);

        File.WriteAllText(Path.Combine(path, "config.json"), """
            { "version": 2, "pointer": { "sensitivity": 1.8, "acceleration": 0.9 } }
            """);

        try
        {
            var store = new ConfigStore(path);
            store.Load();

            // A value the user chose under the current meaning must survive.
            Assert.Equal(1.8, store.Current.Pointer.Sensitivity);
            Assert.Equal(0.9, store.Current.Pointer.Acceleration);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
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
