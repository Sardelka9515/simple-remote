using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using SimpleRemote.Config;
using SimpleRemote.Input;
using SimpleRemote.Server.Protocol;

namespace SimpleRemote.Server;

/// <summary>
/// One connected phone.
///
/// The receive loop is the hot path and is written accordingly: a pooled buffer, no per-message
/// allocation, decode straight into a stack span, and a single batched SendInput for everything
/// that arrived in one frame. Nothing is queued to another thread - SendInput takes microseconds,
/// so a hand-off would cost more than the work itself.
/// </summary>
public sealed class WsConnection(WebSocket socket, RemoteServer server, IPAddress? remoteAddress)
{
    /// <summary>A device that has not authenticated within this window is dropped.</summary>
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(3);

    private const int ReceiveBufferSize = 4096;
    private const int MaxPingsPerMessage = 8;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _closing = new();

    public string? DeviceId { get; private set; }
    public string? DeviceName { get; private set; }
    public bool Authenticated => DeviceId is not null;

    public async Task RunAsync(CancellationToken hostToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostToken, _closing.Token);
        var token = linked.Token;

        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        var authDeadline = DateTimeOffset.UtcNow + AuthTimeout;

        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var (type, length, ok) = await ReceiveMessageAsync(buffer, token).ConfigureAwait(false);
                if (!ok) break;

                if (!Authenticated)
                {
                    // Only an auth message is accepted before authentication, and only briefly.
                    if (DateTimeOffset.UtcNow > authDeadline) break;
                    if (type != WebSocketMessageType.Text) break;
                    if (!await TryAuthenticateAsync(buffer.AsMemory(0, length), token).ConfigureAwait(false)) break;
                    continue;
                }

                if (type == WebSocketMessageType.Binary)
                    HandleBinary(buffer.AsSpan(0, length));
                else
                    await HandleTextAsync(buffer.AsMemory(0, length), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
            // Phone went out of range, browser tab closed, Wi-Fi dropped: all routine.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            server.Remove(this);
            await CloseQuietlyAsync().ConfigureAwait(false);
            _sendLock.Dispose();
            _closing.Dispose();
        }
    }

    /// <summary>
    /// Reads one whole WebSocket message into <paramref name="buffer"/>.
    ///
    /// Our messages are tiny, so fragmentation should never happen - but a client is free to
    /// fragment anyway, and silently treating a fragment as a complete message would corrupt the
    /// input stream. Oversized messages are drained and discarded rather than desynchronising it.
    /// </summary>
    private async Task<(WebSocketMessageType Type, int Length, bool Ok)> ReceiveMessageAsync(
        byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        var overflowed = false;

        while (true)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(offset, buffer.Length - offset), token)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return (default, 0, false);
            }

            if (result.MessageType == WebSocketMessageType.Close) return (default, 0, false);

            offset += result.Count;

            if (result.EndOfMessage)
                return overflowed ? (result.MessageType, 0, true) : (result.MessageType, offset, true);

            if (offset >= buffer.Length)
            {
                // Keep draining so the stream stays framed, but throw the message away.
                overflowed = true;
                offset = 0;
            }
        }
    }

    private async Task<bool> TryAuthenticateAsync(ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        if (server.IsAuthThrottled(remoteAddress))
        {
            await SendJsonAsync(new AuthResultMessage { Ok = false, Reason = "Too many attempts" },
                AppJson.Default.AuthResultMessage, token).ConfigureAwait(false);
            return false;
        }

        ClientMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(payload.Span, AppJson.Default.ClientMessage);
        }
        catch (JsonException)
        {
            return false;
        }

        if (message?.T != "auth")
        {
            server.RecordAuthFailure(remoteAddress);
            return false;
        }

        var device = server.Devices.Verify(message.DeviceId, message.Token);
        if (device is null)
        {
            server.RecordAuthFailure(remoteAddress);
            await SendJsonAsync(new AuthResultMessage { Ok = false, Reason = "Not paired" },
                AppJson.Default.AuthResultMessage, token).ConfigureAwait(false);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(message.Name)) server.Devices.Rename(device.Id, message.Name);

        DeviceId = device.Id;
        DeviceName = device.Name;
        server.RecordAuthSuccess(remoteAddress);
        server.Add(this);

        await SendJsonAsync(new AuthResultMessage { Ok = true }, AppJson.Default.AuthResultMessage, token)
            .ConfigureAwait(false);

        // Push the full initial state so the UI paints complete on first frame.
        await SendJsonAsync(server.DescribeConfig(), AppJson.Default.ConfigMessage, token).ConfigureAwait(false);
        await SendJsonAsync(server.Media.Current, AppJson.Default.MediaStateMessage, token).ConfigureAwait(false);
        await SendJsonAsync(server.Volume.Current(), AppJson.Default.VolumeStateMessage, token).ConfigureAwait(false);

        return true;
    }

    private void HandleBinary(ReadOnlySpan<byte> data)
    {
        Span<InputEvent> events = stackalloc InputEvent[InputInjector.MaxBatch];
        Span<uint> pings = stackalloc uint[MaxPingsPerMessage];

        var status = InputCodec.Decode(data, events, pings, out var eventCount, out var pingCount);

        // Answer pings before injecting, so the round-trip figure the phone displays measures the
        // network rather than however long this batch of input happened to take.
        for (var i = 0; i < pingCount; i++) QueuePong(pings[i]);

        if (eventCount > 0) server.Injector.Inject(events[..eventCount]);

        // A malformed frame means this message is garbage, not that the connection is. Events
        // decoded before the fault were well-formed and have already been applied.
        _ = status;
    }

    private async Task HandleTextAsync(ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        ClientMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(payload.Span, AppJson.Default.ClientMessage);
        }
        catch (JsonException)
        {
            return;
        }

        if (message is null) return;

        switch (message.T)
        {
            case "text":
                if (!string.IsNullOrEmpty(message.S)) TextTyper.Type(message.S);
                break;

            case "media":
                await server.Media.CommandAsync(message.Cmd, message.Pos).ConfigureAwait(false);
                break;

            case "volume":
                if (message.Level is { } level) server.Volume.SetLevel(level);
                if (message.Mute is { } mute) server.Volume.SetMute(mute);
                break;

            case "clip":
                if (!string.IsNullOrEmpty(message.S)) server.Clipboard.Push(message.S);
                break;

            case "clipPull":
                var current = await server.Clipboard.ReadAsync().ConfigureAwait(false);
                await SendJsonAsync(new ClipboardMessage { S = current }, AppJson.Default.ClipboardMessage, token)
                    .ConfigureAwait(false);
                break;

            case "shortcut":
                if (!server.Shortcuts.Invoke(message.Id))
                    await SendJsonAsync(
                        new ToastMessage { S = "Shortcut failed", Kind = "error" },
                        AppJson.Default.ToastMessage, token).ConfigureAwait(false);
                break;

            case "refresh":
                await server.Media.RefreshAsync().ConfigureAwait(false);
                break;
        }
    }

    private void QueuePong(uint seq)
    {
        var frame = new byte[5];
        InputCodec.WritePong(frame, seq);
        _ = SendAsync(frame, WebSocketMessageType.Binary, CancellationToken.None);
    }

    /// <summary>Fire-and-forget text send, used by hub broadcasts.</summary>
    public void QueueText(byte[] utf8Json) =>
        _ = SendAsync(utf8Json, WebSocketMessageType.Text, CancellationToken.None);

    private Task SendJsonAsync<T>(
        T message,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken token) =>
        SendAsync(RemoteServer.Encode(message, typeInfo), WebSocketMessageType.Text, token);

    private async Task SendAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType type, CancellationToken token)
    {
        if (socket.State != WebSocketState.Open) return;

        // Sends are serialised because pongs, broadcasts and command replies all race each other,
        // and interleaving two payloads on one socket would corrupt both.
        try
        {
            await _sendLock.WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(payload, type, endOfMessage: true, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            try { _sendLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    public void RequestClose()
    {
        try { _closing.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task CloseQuietlyAsync()
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }
}
