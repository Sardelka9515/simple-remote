using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using SimpleRemote.Clipboard;
using SimpleRemote.Config;
using SimpleRemote.Input;
using SimpleRemote.Media;
using SimpleRemote.Pairing;
using SimpleRemote.Server.Protocol;
using SimpleRemote.Shortcuts;

namespace SimpleRemote.Server;

/// <summary>
/// Owns the connected devices and fans state out to them.
///
/// Everything below the transport - injector, media, volume, clipboard, shortcuts - is a singleton
/// shared by every connection, because they all manipulate one shared machine. The hub is the only
/// place that knows how many phones are attached.
/// </summary>
public sealed class RemoteServer : IDisposable
{
    private readonly ConcurrentDictionary<WsConnection, byte> _connections = new();
    private readonly ConcurrentDictionary<string, AuthAttempts> _authFailures = new();

    public RemoteServer(
        ConfigStore config,
        DeviceStore devices,
        PairingTokenSource pairing,
        InputInjector injector,
        MediaController media,
        VolumeController volume,
        ClipboardService clipboard,
        ShortcutService shortcuts)
    {
        Config = config;
        Devices = devices;
        Pairing = pairing;
        Injector = injector;
        Media = media;
        Volume = volume;
        Clipboard = clipboard;
        Shortcuts = shortcuts;

        Media.StateChanged += state => Broadcast(state, AppJson.Default.MediaStateMessage);
        Volume.StateChanged += state => Broadcast(state, AppJson.Default.VolumeStateMessage);
        Clipboard.ClipboardChanged += text =>
            Broadcast(new ClipboardMessage { S = text }, AppJson.Default.ClipboardMessage);
    }

    public ConfigStore Config { get; }
    public DeviceStore Devices { get; }
    public PairingTokenSource Pairing { get; }
    public InputInjector Injector { get; }
    public MediaController Media { get; }
    public VolumeController Volume { get; }
    public ClipboardService Clipboard { get; }
    public ShortcutService Shortcuts { get; }

    public int ConnectedCount => _connections.Count;

    /// <summary>Raised when a device connects or disconnects, so the tray can reflect it.</summary>
    public event Action? ConnectionsChanged;

    public void Add(WsConnection connection)
    {
        _connections[connection] = 0;
        ConnectionsChanged?.Invoke();
    }

    public void Remove(WsConnection connection)
    {
        _connections.TryRemove(connection, out _);
        ConnectionsChanged?.Invoke();
    }

    /// <summary>Serialises once and sends the same bytes to every authenticated device.</summary>
    public void Broadcast<T>(T message, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        if (_connections.IsEmpty) return;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);
        foreach (var connection in _connections.Keys)
            connection.QueueText(bytes);
    }

    public void DisconnectDevice(string deviceId)
    {
        foreach (var connection in _connections.Keys)
            if (connection.DeviceId == deviceId)
                connection.RequestClose();
    }

    /// <summary>
    /// Throttles credential guessing. The window is per source address rather than per device id,
    /// since an attacker chooses the id and would otherwise get a fresh budget for every guess.
    /// </summary>
    public bool IsAuthThrottled(IPAddress? address)
    {
        if (address is null) return false;
        if (!_authFailures.TryGetValue(address.ToString(), out var attempts)) return false;

        if (DateTimeOffset.UtcNow - attempts.WindowStart > TimeSpan.FromMinutes(1))
        {
            _authFailures.TryRemove(address.ToString(), out _);
            return false;
        }

        return attempts.Count >= 10;
    }

    public void RecordAuthFailure(IPAddress? address)
    {
        if (address is null) return;

        _authFailures.AddOrUpdate(
            address.ToString(),
            _ => new AuthAttempts { Count = 1, WindowStart = DateTimeOffset.UtcNow },
            (_, existing) =>
            {
                if (DateTimeOffset.UtcNow - existing.WindowStart > TimeSpan.FromMinutes(1))
                    return new AuthAttempts { Count = 1, WindowStart = DateTimeOffset.UtcNow };

                existing.Count++;
                return existing;
            });
    }

    public void RecordAuthSuccess(IPAddress? address)
    {
        if (address is not null) _authFailures.TryRemove(address.ToString(), out _);
    }

    /// <summary>Everything the UI needs to draw itself, sent once immediately after auth.</summary>
    public ConfigMessage DescribeConfig()
    {
        // Whole virtual desktop, not just the primary monitor: the cursor can travel across all of
        // them, so that is the distance the trackpad has to be able to cover.
        var screen = System.Windows.Forms.SystemInformation.VirtualScreen;

        return new ConfigMessage
        {
            Shortcuts = Shortcuts.Describe(),
            Pointer = new PointerInfo
            {
                Sensitivity = Config.Current.Pointer.Sensitivity,
                Acceleration = Config.Current.Pointer.Acceleration,
                MaxSpeed = Config.Current.Pointer.MaxSpeed,
                ScrollSpeed = Config.Current.Pointer.ScrollSpeed,
                NaturalScroll = Config.Current.Pointer.NaturalScroll,
                ScreenWidth = screen.Width,
                ScreenHeight = screen.Height,
            },
        };
    }

    public static byte[] Encode<T>(T message, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);

    public void Dispose()
    {
        foreach (var connection in _connections.Keys) connection.RequestClose();
    }

    private sealed class AuthAttempts
    {
        public int Count;
        public DateTimeOffset WindowStart;
    }
}
