using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleRemote.Shortcuts;

namespace SimpleRemote.Config;

/// <summary>User-editable settings, persisted to %APPDATA%\SimpleRemote\config.json.</summary>
public sealed class AppConfig
{
    /// <summary>
    /// Bumped whenever the *meaning* of a setting changes, not merely its default.
    ///
    /// Version 2 redefined pointer sensitivity from an absolute gain into a multiplier on a
    /// screen-relative base. A file written by version 1 carries a value calibrated for the old
    /// meaning, and silently reinterpreting it made the cursor roughly twice as fast as intended -
    /// the kind of bug that looks like a feel problem and wastes a lot of time.
    ///
    /// Version 3 reworked the bundled Netflix layout (S to skip intro, the shared media panel, no
    /// next-episode button). Because the config file is rewritten on every load, the earlier
    /// default was already persisted and would otherwise shadow the new one forever.
    ///
    /// Version 4 replaced that layout again: its tab icon had been persisted as the literal text
    /// "U0001F3AC" instead of an emoji, and its icons moved from Unicode glyphs to named SVG icons.
    ///
    /// Rule of thumb: any change to a bundled layout needs a version bump, or existing installs
    /// never see it.
    /// </summary>
    public const int CurrentVersion = 4;

    /// <summary>
    /// Defaults to 0, NOT CurrentVersion: a property initializer would be kept when the field is
    /// absent from the JSON, making every pre-versioning file look current and silently skipping
    /// its migration. Fresh configs get the current version set explicitly on creation.
    /// </summary>
    public int Version { get; set; }

    public int Port { get; set; } = 8787;

    /// <summary>HTTPS seam: flip this (plus a cert) and the whole stack follows.</summary>
    public bool UseHttps { get; set; }
    public string? CertPath { get; set; }
    public string? CertPassword { get; set; }

    /// <summary>Pinned LAN address for the QR code; null means auto-rank.</summary>
    public string? PreferredAddress { get; set; }

    public bool StartMinimized { get; set; }

    public PointerConfig Pointer { get; set; } = new();

    public List<ShortcutDefinition> Shortcuts { get; set; } = ShortcutDefinition.Defaults();

    /// <summary>Custom control pages. Add one here and it appears as a tab on the phone.</summary>
    public List<LayoutDefinition> Layouts { get; set; } = LayoutDefinition.Defaults();
}

/// <summary>
/// Pointer feel. Acceleration is applied on the client (before the delta is even sent) so the
/// curve is evaluated against the true event timestamps rather than post-network jitter.
///
/// Sensitivity is a multiplier on a screen-relative base, not an absolute gain: the client scales
/// it by how large the desktop is compared to the trackpad, so the same value feels the same on a
/// laptop panel and a 4K desktop. A value of 1.0 means one corner-to-corner drag across the pad
/// travels roughly one corner-to-corner distance on screen.
///
/// Note that Windows applies its own acceleration to relative mouse input when "Enhance pointer
/// precision" is on, which it is by default - measured at roughly 2.2x on top of whatever we send.
/// Turning that off gives this curve sole control and makes the feel more predictable, at which
/// point sensitivity wants raising.
///
/// The phone also has its own Speed slider, stored per device, which multiplies these values -
/// pointer feel is subjective and differs between a small phone and a tablet.
/// </summary>
public sealed class PointerConfig
{
    /// <summary>Multiplier on the screen-relative base gain.</summary>
    public double Sensitivity { get; set; } = 0.55;

    /// <summary>Acceleration coefficient k in gain = base * sensitivity * (1 + k * min(speed, maxSpeed)).</summary>
    public double Acceleration { get; set; } = 0.4;

    /// <summary>Upper bound on the speed term, so a fast flick cannot fling the cursor unboundedly.</summary>
    public double MaxSpeed { get; set; } = 3.0;

    /// <summary>
    /// How long a tap holds the left button down before releasing, in milliseconds.
    ///
    /// This is the tap-and-a-half window: a second press arriving while the button is still held
    /// becomes a drag, and because no second button-down is ever sent, the host cannot mistake the
    /// gesture for a double click. The trade-off is that a plain tap activates this much later,
    /// so it wants to be just long enough to cover the pause between the tap and the press.
    /// </summary>
    public int TapHoldMs { get; set; } = 200;

    public double ScrollSpeed { get; set; } = 1.0;

    /// <summary>
    /// Send wheel motion at 1-unit granularity instead of whole 120-unit notches.
    ///
    /// On by default: notch quantisation is what makes scrolling feel abrupt, since nothing moves
    /// until the finger has covered a whole notch and then the view jumps three lines. Browsers,
    /// Explorer, Office and anything built on a modern toolkit consume sub-notch deltas, which is
    /// exactly what a Windows precision touchpad sends.
    ///
    /// Turn it off if some older application integer-divides the wheel delta by 120 and therefore
    /// never scrolls at all.
    /// </summary>
    public bool SmoothScroll { get; set; } = true;

    /// <summary>Content-follows-finger, matching phone conventions.</summary>
    public bool NaturalScroll { get; set; } = true;

    /// <summary>
    /// Replay pointer and wheel motion at the pace the phone produced it, instead of as it arrives.
    ///
    /// Wi-Fi delivers packets in bursts, and injecting a burst at once makes the cursor jump. The
    /// cost is a small, adaptive playout delay (see <see cref="MaxNetworkBufferMs"/>). Turn it off
    /// on a wired or otherwise very clean network to shave that latency.
    /// </summary>
    public bool NetworkSmoothing { get; set; } = true;

    /// <summary>The most latency network smoothing may add, in milliseconds.</summary>
    public int MaxNetworkBufferMs { get; set; } = 60;
}

/// <summary>Loads and saves <see cref="AppConfig"/>, tolerating a missing or corrupt file.</summary>
public sealed class ConfigStore
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimpleRemote");

    private readonly string _directory;

    /// <param name="directory">Override for the settings location; defaults to %APPDATA%\SimpleRemote.</param>
    public ConfigStore(string? directory = null)
    {
        _directory = directory ?? DataDirectory;
        FilePath = Path.Combine(_directory, "config.json");
    }

    public string FilePath { get; }

    public AppConfig Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                Current = JsonSerializer.Deserialize(json, AppJson.Default.AppConfig) ?? new AppConfig();
                // An empty shortcut list is a legitimate user choice; a missing one is not.
                Current.Pointer ??= new PointerConfig();
                Current.Shortcuts ??= ShortcutDefinition.Defaults();
                Current.Layouts ??= LayoutDefinition.Defaults();
                Migrate();

                // Rewrite so the file always lists every setting, including ones added by a newer
                // build. A knob that does not appear in the file is a knob nobody finds.
                Save();
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt config must never block startup - fall back to defaults and rewrite.
        }

        Current = new AppConfig { Version = AppConfig.CurrentVersion };
        Save();
    }

    /// <summary>
    /// Brings a file written by an older version up to date.
    ///
    /// Only the pointer block is reset, and only when the stored version predates the change in
    /// its units - everything a user is likely to have deliberately customised (port, shortcuts,
    /// chosen network) is preserved. The per-device Speed slider on the phone is the real tuning
    /// control now, so re-deriving these is cheap.
    /// </summary>
    private void Migrate()
    {
        if (Current.Version >= AppConfig.CurrentVersion) return;

        // Steps are cumulative and individually gated, so moving from 2 to 3 does not also re-run
        // the version 1 pointer reset and throw away settings chosen under the current meaning.
        if (Current.Version < 2)
            Current.Pointer = new PointerConfig();

        if (Current.Version < 4)
        {
            // Replace only the bundled layout, in place. Layouts the user added are untouched, and a
            // Netflix layout they deleted stays deleted.
            var index = Current.Layouts.FindIndex(l => l.Id == "netflix");
            if (index >= 0) Current.Layouts[index] = LayoutDefinition.Netflix();
        }

        Current.Version = AppConfig.CurrentVersion;
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, AppJson.Default.AppConfig));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(SimpleRemote.Pairing.DeviceRecord[]))]
[JsonSerializable(typeof(SimpleRemote.Server.Protocol.ClientMessage))]
[JsonSerializable(typeof(SimpleRemote.Server.Protocol.AuthResultMessage))]
[JsonSerializable(typeof(SimpleRemote.Server.Protocol.MediaStateMessage))]
[JsonSerializable(typeof(SimpleRemote.Server.Protocol.VolumeStateMessage))]
[JsonSerializable(typeof(SimpleRemote.Server.Protocol.ClipboardMessage))]
[JsonSerializable(typeof(SimpleRemote.Server.Protocol.ToastMessage))]
[JsonSerializable(typeof(SimpleRemote.Server.Protocol.ConfigMessage))]
[JsonSerializable(typeof(SimpleRemote.Server.ApiPairRequest))]
[JsonSerializable(typeof(SimpleRemote.Server.ApiPairResponse))]
[JsonSerializable(typeof(SimpleRemote.Server.ApiHealthResponse))]
public partial class AppJson : JsonSerializerContext;
