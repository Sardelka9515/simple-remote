using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SimpleRemote.Input;
using SimpleRemote.Server.Protocol;
using Windows.Media.Control;
using Windows.Storage.Streams;

using SmtcManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using SmtcSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace SimpleRemote.Media;

/// <summary>
/// Reads and drives whatever is currently playing, via the Windows System Media Transport Controls.
///
/// SMTC is used in preference to blindly firing media keys because it gives real now-playing
/// metadata - title, artist, artwork, position - which is what makes this a media remote rather
/// than three unlabelled buttons. Media keys remain as a fallback for apps SMTC does not see.
/// </summary>
public sealed class MediaController(InputInjector injector) : IDisposable
{
    private readonly Lock _gate = new();

    private SmtcManager? _manager;
    private SmtcSession? _session;
    private CancellationTokenSource? _cts;

    /// <summary>Album art keyed by content hash. A handful of entries survives track changes.</summary>
    private readonly Dictionary<string, (byte[] Bytes, string ContentType)> _art = new(StringComparer.Ordinal);

    public event Action<MediaStateMessage>? StateChanged;

    public MediaStateMessage Current { get; private set; } = new() { Active = false, Status = "unknown" };

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await SmtcManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            AttachSession(_manager.GetCurrentSession());
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // No SMTC available (rare, but possible on stripped installs). Media keys still work.
            _manager = null;
        }

        _cts = new CancellationTokenSource();
        _ = PollPositionAsync(_cts.Token);
    }

    /// <summary>
    /// Position is not reliably event-driven, so it is polled - but only while something is
    /// actually playing, and only once a second. The client interpolates between these ticks with
    /// requestAnimationFrame, so the progress bar looks smooth on near-zero traffic.
    /// </summary>
    private async Task PollPositionAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (_session is null || Current.Status != "playing") continue;
                await RefreshAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnCurrentSessionChanged(SmtcManager sender, CurrentSessionChangedEventArgs args) =>
        AttachSession(sender.GetCurrentSession());

    private void AttachSession(SmtcSession? session)
    {
        lock (_gate)
        {
            Detach();
            _session = session;

            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnSessionChanged;
                _session.PlaybackInfoChanged += OnPlaybackChanged;
                _session.TimelinePropertiesChanged += OnTimelineChanged;
            }
        }

        _ = RefreshAsync();
    }

    private void Detach()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= OnSessionChanged;
        _session.PlaybackInfoChanged -= OnPlaybackChanged;
        _session.TimelinePropertiesChanged -= OnTimelineChanged;
        _session = null;
    }

    private void OnSessionChanged(SmtcSession s, MediaPropertiesChangedEventArgs a) => _ = RefreshAsync();
    private void OnPlaybackChanged(SmtcSession s, PlaybackInfoChangedEventArgs a) => _ = RefreshAsync();
    private void OnTimelineChanged(SmtcSession s, TimelinePropertiesChangedEventArgs a) => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        var state = await BuildStateAsync().ConfigureAwait(false);
        Current = state;
        StateChanged?.Invoke(state);
    }

    private async Task<MediaStateMessage> BuildStateAsync()
    {
        var session = _session;
        if (session is null) return new MediaStateMessage { Active = false, Status = "unknown" };

        try
        {
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var props = await session.TryGetMediaPropertiesAsync();

            var status = playback.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "playing",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "paused",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => "stopped",
                _ => "unknown",
            };

            var state = new MediaStateMessage
            {
                Active = true,
                Title = props?.Title,
                Artist = string.IsNullOrWhiteSpace(props?.Artist) ? props?.AlbumArtist : props.Artist,
                Album = props?.AlbumTitle,
                App = FriendlyAppName(session.SourceAppUserModelId),
                Status = status,
                PositionMs = EstimatePositionMs(
                    timeline.Position,
                    timeline.EndTime - timeline.StartTime,
                    timeline.LastUpdatedTime,
                    DateTimeOffset.Now,
                    status == "playing",
                    playback.PlaybackRate ?? 1.0),
                DurationMs = (long)(timeline.EndTime - timeline.StartTime).TotalMilliseconds,
                CanPlayPause = playback.Controls.IsPlayPauseToggleEnabled || playback.Controls.IsPlayEnabled,
                CanNext = playback.Controls.IsNextEnabled,
                CanPrevious = playback.Controls.IsPreviousEnabled,
                CanSeek = playback.Controls.IsPlaybackPositionEnabled,
            };

            if (props?.Thumbnail is { } thumb)
                state.ArtUrl = await CacheArtAsync(thumb).ConfigureAwait(false);

            return state;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or TaskCanceledException)
        {
            // Sessions vanish mid-query when an app closes; treat that as simply nothing playing.
            return new MediaStateMessage { Active = false, Status = "unknown" };
        }
    }

    /// <summary>
    /// Where playback actually is right now.
    ///
    /// SMTC's Position is a snapshot, not a live value: it is correct as of LastUpdatedTime, and
    /// many players - browsers in particular, so every web video - only publish a new snapshot on
    /// play, pause or seek. Reporting Position as-is while playing made the phone's progress bar
    /// snap back to that frozen value on every poll, so it looked stuck until the next pause.
    /// Advancing it by the time since the snapshot is what the timeline API expects of a reader.
    /// </summary>
    public static long EstimatePositionMs(
        TimeSpan position,
        TimeSpan duration,
        DateTimeOffset lastUpdated,
        DateTimeOffset now,
        bool playing,
        double rate)
    {
        var estimate = position;

        // A player that never sets LastUpdatedTime leaves it at the default; extrapolating from
        // year 1 would jump straight to the end.
        if (playing && lastUpdated.Year >= 2000)
        {
            var elapsed = now - lastUpdated;
            if (elapsed > TimeSpan.Zero)
            {
                if (!double.IsFinite(rate) || rate <= 0) rate = 1.0;
                estimate += TimeSpan.FromTicks((long)(elapsed.Ticks * rate));
            }
        }

        if (duration > TimeSpan.Zero && estimate > duration) estimate = duration;
        if (estimate < TimeSpan.Zero) estimate = TimeSpan.Zero;

        return (long)estimate.TotalMilliseconds;
    }

    /// <summary>
    /// Pulls artwork out of WinRT and caches it by content hash, so it is served over plain HTTP
    /// with an immutable cache header rather than re-pushed down the WebSocket on every state
    /// change. The hash means an unchanged cover is fetched exactly once per device.
    /// </summary>
    private async Task<string?> CacheArtAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream.Size is 0 or > 8 * 1024 * 1024) return null;

            var bytes = new byte[stream.Size];
            var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);

            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
            var contentType = string.IsNullOrEmpty(stream.ContentType) ? "image/jpeg" : stream.ContentType;

            lock (_gate)
            {
                if (!_art.ContainsKey(hash))
                {
                    if (_art.Count >= 4) _art.Remove(_art.Keys.First());
                    _art[hash] = (bytes, contentType);
                }
            }

            return "/api/albumart/" + hash;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or OutOfMemoryException)
        {
            return null;
        }
    }

    public bool TryGetArt(string hash, out byte[] bytes, out string contentType)
    {
        lock (_gate)
        {
            if (_art.TryGetValue(hash, out var entry))
            {
                bytes = entry.Bytes;
                contentType = entry.ContentType;
                return true;
            }
        }

        bytes = [];
        contentType = "";
        return false;
    }

    /// <summary>Executes a transport command, falling back to media keys when SMTC cannot serve it.</summary>
    public async Task<bool> CommandAsync(string? command, long? positionMs)
    {
        var session = _session;

        try
        {
            if (session is not null)
            {
                var handled = command switch
                {
                    "playpause" => await session.TryTogglePlayPauseAsync(),
                    "play" => await session.TryPlayAsync(),
                    "pause" => await session.TryPauseAsync(),
                    "next" => await session.TrySkipNextAsync(),
                    "prev" => await session.TrySkipPreviousAsync(),
                    "stop" => await session.TryStopAsync(),
                    "seek" when positionMs is { } ms =>
                        await session.TryChangePlaybackPositionAsync(ms * TimeSpan.TicksPerMillisecond),
                    _ => false,
                };

                if (handled)
                {
                    await RefreshAsync().ConfigureAwait(false);
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
        }

        return FallbackToMediaKey(command);
    }

    /// <summary>
    /// Some players - a browser tab playing video, older desktop apps - never register with SMTC
    /// but do honour the hardware media keys, so this is a genuine second chance rather than a
    /// token error path.
    /// </summary>
    private bool FallbackToMediaKey(string? command)
    {
        ushort vk = command switch
        {
            "playpause" or "play" or "pause" => 0xB3,
            "next" => 0xB0,
            "prev" => 0xB1,
            "stop" => 0xB2,
            _ => 0,
        };

        if (vk == 0) return false;
        injector.Tap(vk);
        return true;
    }

    /// <summary>Turns an AUMID such as Spotify.exe or a packaged app id into something displayable.</summary>
    private static string? FriendlyAppName(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return null;

        var name = aumid;
        var bang = name.LastIndexOf('!');
        if (bang >= 0 && bang < name.Length - 1) name = name[(bang + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        return name.Length == 0 ? null : name;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();

        lock (_gate)
        {
            Detach();

            if (_manager is not null)
            {
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
                _manager = null;
            }
        }
    }
}
