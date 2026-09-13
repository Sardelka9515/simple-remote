using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleRemote.Shortcuts;

namespace SimpleRemote.Config;

/// <summary>User-editable settings, persisted to %APPDATA%\SimpleRemote\config.json.</summary>
public sealed class AppConfig
{
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

    public double ScrollSpeed { get; set; } = 1.0;

    /// <summary>Content-follows-finger, matching phone conventions.</summary>
    public bool NaturalScroll { get; set; } = true;
}

/// <summary>Loads and saves <see cref="AppConfig"/>, tolerating a missing or corrupt file.</summary>
public sealed class ConfigStore
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimpleRemote");

    public string FilePath { get; } = Path.Combine(DataDirectory, "config.json");

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
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt config must never block startup - fall back to defaults and rewrite.
        }

        Current = new AppConfig();
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
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
public partial class AppJson : JsonSerializerContext;
