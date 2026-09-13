using SimpleRemote.Input;
using SimpleRemote.Server.Protocol;
using Xunit;

namespace SimpleRemote.Tests;

public class InputCodecTests
{
    private static DecodeStatus Decode(byte[] data, out InputEvent[] events, out uint[] pings)
    {
        Span<InputEvent> eventBuffer = stackalloc InputEvent[InputInjector.MaxBatch];
        Span<uint> pingBuffer = stackalloc uint[8];

        var status = InputCodec.Decode(data, eventBuffer, pingBuffer, out var eventCount, out var pingCount);

        events = eventBuffer[..eventCount].ToArray();
        pings = pingBuffer[..pingCount].ToArray();
        return status;
    }

    private static byte[] Encode(params InputEvent[] events)
    {
        var buffer = new byte[events.Length * 8];
        var offset = 0;
        foreach (var e in events) offset += InputCodec.Write(buffer.AsSpan(offset), e);
        return buffer[..offset];
    }

    [Fact]
    public void MouseMoveRoundTrips()
    {
        var status = Decode(Encode(InputEvent.Move(120, -45)), out var events, out _);

        Assert.Equal(DecodeStatus.Ok, status);
        var move = Assert.Single(events);
        Assert.Equal(InputKind.MouseMove, move.Kind);
        Assert.Equal(120, move.A);
        Assert.Equal(-45, move.B);
    }

    [Theory]
    [InlineData(MouseButton.Left, true)]
    [InlineData(MouseButton.Right, false)]
    [InlineData(MouseButton.Middle, true)]
    public void MouseButtonRoundTrips(MouseButton button, bool down)
    {
        Decode(Encode(InputEvent.Button(button, down)), out var events, out _);

        var e = Assert.Single(events);
        Assert.Equal(InputKind.MouseButton, e.Kind);
        Assert.Equal((int)button, e.A);
        Assert.Equal(down ? 1 : 0, e.B);
    }

    [Fact]
    public void ScrollAndKeyRoundTrip()
    {
        Decode(Encode(InputEvent.Scroll(-240, 120), InputEvent.Key(0x1B, true)), out var events, out _);

        Assert.Equal(2, events.Length);
        Assert.Equal(InputKind.Scroll, events[0].Kind);
        Assert.Equal(-240, events[0].A);
        Assert.Equal(120, events[0].B);
        Assert.Equal(InputKind.Key, events[1].Kind);
        Assert.Equal(0x1B, events[1].A);
    }

    /// <summary>
    /// The whole point of the binary format: a frame of coalesced motion arrives as one message
    /// and turns into one batched SendInput call.
    /// </summary>
    [Fact]
    public void ConcatenatedMessagesInOneBufferAllDecode()
    {
        var data = Encode(
            InputEvent.Move(1, 2),
            InputEvent.Move(3, 4),
            InputEvent.Button(MouseButton.Left, true),
            InputEvent.Scroll(0, 120),
            InputEvent.Button(MouseButton.Left, false));

        var status = Decode(data, out var events, out _);

        Assert.Equal(DecodeStatus.Ok, status);
        Assert.Equal(5, events.Length);
        Assert.Equal(InputKind.MouseMove, events[0].Kind);
        Assert.Equal(InputKind.Scroll, events[3].Kind);
    }

    [Fact]
    public void PingsAreSeparatedFromInputEvents()
    {
        var data = new byte[] { 0x01, 5, 0, 5, 0, 0x05, 0x2A, 0, 0, 0 }; // move then ping seq 42

        var status = Decode(data, out var events, out var pings);

        Assert.Equal(DecodeStatus.Ok, status);
        Assert.Single(events);
        Assert.Equal(42u, Assert.Single(pings));
    }

    [Fact]
    public void PongEncodesFiveBytesLittleEndian()
    {
        Span<byte> buffer = stackalloc byte[5];
        var written = InputCodec.WritePong(buffer, 0x01020304);

        Assert.Equal(5, written);
        Assert.Equal(0x81, buffer[0]);
        Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01 }, buffer[1..].ToArray());
    }

    // A malformed frame must never throw: that would kill an otherwise healthy connection.

    [Fact]
    public void UnknownOpcodeIsRejectedWithoutThrowing()
    {
        var status = Decode([0x7F, 0, 0, 0], out var events, out _);

        Assert.Equal(DecodeStatus.Malformed, status);
        Assert.Empty(events);
    }

    [Fact]
    public void TruncatedTrailingMessageKeepsWhatCameBefore()
    {
        // A complete move, then a move missing its last byte.
        var data = new byte[] { 0x01, 5, 0, 5, 0, 0x01, 9, 0, 9 };

        var status = Decode(data, out var events, out _);

        Assert.Equal(DecodeStatus.Malformed, status);
        Assert.Single(events);
        Assert.Equal(5, events[0].A);
    }

    [Fact]
    public void OutOfRangeMouseButtonIsRejected()
    {
        var status = Decode([0x02, 9, 1], out var events, out _);

        Assert.Equal(DecodeStatus.Malformed, status);
        Assert.Empty(events);
    }

    [Fact]
    public void EmptyBufferIsOk()
    {
        var status = Decode([], out var events, out var pings);

        Assert.Equal(DecodeStatus.Ok, status);
        Assert.Empty(events);
        Assert.Empty(pings);
    }

    [Fact]
    public void OverflowStopsCleanlyAtBatchLimit()
    {
        var many = Enumerable.Range(0, InputInjector.MaxBatch + 10)
            .Select(i => InputEvent.Move(i, i)).ToArray();

        var status = Decode(Encode(many), out var events, out _);

        Assert.Equal(DecodeStatus.Overflow, status);
        Assert.Equal(InputInjector.MaxBatch, events.Length);
    }

    [Fact]
    public void WriteClampsDeltasToInt16Range()
    {
        Decode(Encode(InputEvent.Move(999_999, -999_999)), out var events, out _);

        Assert.Equal(short.MaxValue, events[0].A);
        Assert.Equal(short.MinValue, events[0].B);
    }
}
