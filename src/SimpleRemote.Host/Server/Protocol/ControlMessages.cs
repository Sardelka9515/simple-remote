namespace SimpleRemote.Server.Protocol;

/// <summary>
/// Every client control message, flattened into one shape.
///
/// A polymorphic hierarchy would be tidier, but this path carries a handful of messages per second
/// at most, and a single deserialize with nullable fields beats parsing the discriminator and then
/// re-deserializing into a concrete type.
/// </summary>
public sealed class ClientMessage
{
    /// <summary>Discriminator: auth, hello, text, media, volume, clip, shortcut, key.</summary>
    public string? T { get; set; }

    // auth
    public string? DeviceId { get; set; }
    public string? Token { get; set; }
    public string? Name { get; set; }

    // text / clip
    public string? S { get; set; }

    // media
    public string? Cmd { get; set; }
    public long? Pos { get; set; }

    // volume
    public double? Level { get; set; }
    public bool? Mute { get; set; }

    // shortcut
    public string? Id { get; set; }
}

public sealed class AuthResultMessage
{
    public string T => "authResult";
    public bool Ok { get; set; }
    public string? Reason { get; set; }
}

public sealed class MediaStateMessage
{
    public string T => "mediaState";

    /// <summary>False when nothing is currently playable, so the UI can show an idle state.</summary>
    public bool Active { get; set; }

    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }

    /// <summary>Owning application, e.g. Spotify.exe, for display.</summary>
    public string? App { get; set; }

    /// <summary>playing | paused | stopped | unknown.</summary>
    public string? Status { get; set; }

    public long PositionMs { get; set; }
    public long DurationMs { get; set; }

    /// <summary>Relative URL of the cached album art, or null when the session has none.</summary>
    public string? ArtUrl { get; set; }

    public bool CanPlayPause { get; set; }
    public bool CanNext { get; set; }
    public bool CanPrevious { get; set; }
    public bool CanSeek { get; set; }
}

public sealed class VolumeStateMessage
{
    public string T => "volumeState";
    public double Level { get; set; }
    public bool Muted { get; set; }
    public string? Device { get; set; }
}

public sealed class ClipboardMessage
{
    public string T => "clipboard";
    public string? S { get; set; }
}

public sealed class ToastMessage
{
    public string T => "toast";
    public string? S { get; set; }

    /// <summary>info | error.</summary>
    public string Kind { get; set; } = "info";
}

/// <summary>Sent once after auth so the UI can build itself without a second round trip.</summary>
public sealed class ConfigMessage
{
    public string T => "config";
    public List<ShortcutInfo> Shortcuts { get; set; } = [];
    public PointerInfo Pointer { get; set; } = new();
    public string HostName { get; set; } = Environment.MachineName;
}

public sealed class ShortcutInfo
{
    public required string Id { get; set; }
    public required string Label { get; set; }
    public string? Icon { get; set; }
}

public sealed class PointerInfo
{
    public double Sensitivity { get; set; }
    public double Acceleration { get; set; }
    public double MaxSpeed { get; set; }
    public double ScrollSpeed { get; set; }
    public bool NaturalScroll { get; set; }
    public int TapHoldMs { get; set; }

    /// <summary>
    /// Size of the whole virtual desktop, in pixels.
    ///
    /// The client needs this to scale motion: a 390pt trackpad driving a 4K desktop has to cover
    /// far more ground per millimetre of finger travel than the same trackpad driving a laptop
    /// panel. Without it the base gain is a guess that is wrong on most hardware.
    /// </summary>
    public int ScreenWidth { get; set; }

    public int ScreenHeight { get; set; }
}
