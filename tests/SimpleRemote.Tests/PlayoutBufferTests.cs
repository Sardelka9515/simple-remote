using SimpleRemote.Input;
using SimpleRemote.Server.Protocol;
using Xunit;

namespace SimpleRemote.Tests;

public class PlayoutBufferTests
{
    private const double Frame = 16.7;
    private const double Tick = 2;

    /// <summary>
    /// Plays a phone's frames through the buffer: frame i is made at i*Frame on the phone and
    /// arrives at the host after latency(i). Returns motion injected per 2ms host tick.
    /// </summary>
    private static List<(double T, int Dx)> Play(int frames, int dxPerFrame, Func<int, double> latency, double runMs)
    {
        var buffer = new PlayoutBuffer();

        // A WebSocket is TCP: a held-back frame holds back everything behind it too, so arrival
        // order is always send order.
        var arrivals = new List<(double Client, double Host)>();
        var previous = double.NegativeInfinity;
        for (var i = 0; i < frames; i++)
        {
            previous = Math.Max(previous, 1000 + i * Frame + latency(i));
            arrivals.Add((i * Frame, previous));
        }

        var output = new List<(double, int)>();
        var next = 0;
        for (var t = 1000.0; t < 1000 + runMs; t += Tick)
        {
            while (next < arrivals.Count && arrivals[next].Host <= t)
            {
                buffer.Add(arrivals[next].Client, t, scroll: false, dxPerFrame, 0);
                next++;
            }

            var o = buffer.Advance(t);
            if (o.MoveX != 0) output.Add((t, o.MoveX));
        }

        return output;
    }

    /// <summary>
    /// The reported bug. Wi-Fi holds frames back and releases them together - here, every sixth
    /// frame arrives on time and the five before it are held until then. Injected on arrival, that
    /// is 60px in one go. Played out, no single 2ms tick moves more than a few pixels.
    /// </summary>
    [Fact]
    public void BurstyArrivalIsReplayedEvenly()
    {
        double Bursty(int i) => (5 - i % 6) * Frame; // frame 0 waits 5 frames, frame 5 waits none

        var output = Play(60, 12, Bursty, 1400);

        Assert.Equal(60 * 12, output.Sum(o => o.Dx));
        Assert.True(output.Max(o => o.Dx) <= 4, $"largest single injection was {output.Max(o => o.Dx)}px: " +
            string.Join(" ", output.Select(o => $"{o.T - 1000:0}:{o.Dx}")));
    }

    [Fact]
    public void SteadyArrivalLosesNothingAndAddsLittleDelay()
    {
        var output = Play(30, 10, _ => 5, 800);

        Assert.Equal(300, output.Sum(o => o.Dx));

        // First motion appears within about one frame plus margin of the first arrival.
        Assert.InRange(output[0].T - 1005, 0, 30);
    }

    /// <summary>A long stall (a TCP retransmit) glides out over time rather than landing at once.</summary>
    [Fact]
    public void StallIsGlidedThroughNotDumped()
    {
        // Frame 10 is lost and retransmitted 200ms later; frames behind it queue up and arrive with it.
        double Stalled(int i) => i == 10 ? 200 : 3;

        var output = Play(40, 10, Stalled, 1500);

        Assert.Equal(400, output.Sum(o => o.Dx));

        // Glide at 2.5x real time: 10 frames of 10px over >= ~66ms, so never a big jump.
        Assert.True(output.Max(o => o.Dx) <= 4, $"largest single injection was {output.Max(o => o.Dx)}px: " +
            string.Join(" ", output.Select(o => $"{o.T - 1000:0}:{o.Dx}")));
    }

    /// <summary>A click's early flush followed by a frame stamped earlier must not bunch up motion.</summary>
    [Fact]
    public void OutOfOrderStampDoesNotDoubleUp()
    {
        var buffer = new PlayoutBuffer();
        var max = 0;
        var total = 0;
        var host = 1000.0;

        for (var i = 0; i < 20; i++)
        {
            buffer.Add(i * Frame, host, scroll: false, 12, 0);
            if (i == 10) buffer.Add(i * Frame - 5, host, scroll: false, 12, 0); // stamped earlier

            for (var k = 0; k < 8; k++, host += Tick)
            {
                var o = buffer.Advance(host);
                max = Math.Max(max, o.MoveX);
                total += o.MoveX;
            }
            host += Frame - 8 * Tick;
        }

        for (var k = 0; k < 100; k++, host += Tick) total += buffer.Advance(host).MoveX;

        Assert.Equal(21 * 12, total);
        Assert.True(max <= 4, $"largest single injection was {max}px");
    }

    [Fact]
    public void DrainReturnsEverythingPendingForAClick()
    {
        var buffer = new PlayoutBuffer();
        buffer.Add(0, 1000, scroll: false, 7, -3);
        buffer.Add(16.7, 1016.7, scroll: false, 5, 1);
        buffer.Add(16.7, 1016.7, scroll: true, 0, 240);

        var drained = buffer.Drain();

        Assert.Equal(new PlayoutBuffer.Output(12, -2, 0, 240), drained);
        Assert.False(buffer.HasPending);
        Assert.True(buffer.Advance(2000).IsEmpty);
    }

    [Fact]
    public void MoveAndScrollArePlayedOnSeparateLanes()
    {
        var buffer = new PlayoutBuffer();
        for (var i = 0; i < 10; i++)
        {
            buffer.Add(i * Frame, 1000 + i * Frame, scroll: false, 3, 0);
            buffer.Add(i * Frame, 1000 + i * Frame, scroll: true, 0, 60);
        }

        int mx = 0, sy = 0;
        for (var t = 1000.0; t < 1600; t += Tick)
        {
            var o = buffer.Advance(t);
            mx += o.MoveX;
            sy += o.ScrollY;
        }

        Assert.Equal(30, mx);
        Assert.Equal(600, sy);
    }

    /// <summary>
    /// A phone that slept has a clock that jumped relative to the PC. The first gesture after it must
    /// play normally, not wait out (or dump) the difference.
    /// </summary>
    [Fact]
    public void ClockJumpAfterIdleIsRelearned()
    {
        var buffer = new PlayoutBuffer();
        buffer.Add(0, 1000, scroll: false, 5, 0);
        Assert.Equal(5, buffer.Drain().MoveX);

        // Ten seconds later on the PC, but the phone's clock only advanced by one.
        var injected = 0;
        var firstAt = double.NaN;
        for (var i = 0; i < 10; i++) buffer.Add(1000 + i * Frame, 11_000 + i * Frame, scroll: false, 4, 0);
        for (var t = 11_000.0; t < 11_400; t += Tick)
        {
            var o = buffer.Advance(t);
            if (o.MoveX != 0 && double.IsNaN(firstAt)) firstAt = t;
            injected += o.MoveX;
        }

        Assert.Equal(40, injected);
        Assert.InRange(firstAt - 11_000, 0, 60);
    }

    [Fact]
    public void FrameTimeRoundTripsThroughTheCodec()
    {
        Span<byte> buffer = stackalloc byte[16];
        var written = InputCodec.Write(buffer, InputEvent.FrameTime(0xFEDC_BA98));
        written += InputCodec.Write(buffer[written..], InputEvent.Move(3, -4));

        Span<InputEvent> events = stackalloc InputEvent[4];
        Span<uint> pings = stackalloc uint[1];
        var status = InputCodec.Decode(buffer[..written], events, pings, out var count, out _);

        Assert.Equal(DecodeStatus.Ok, status);
        Assert.Equal(2, count);
        Assert.Equal(InputKind.FrameTime, events[0].Kind);
        Assert.Equal(0xFEDC_BA98u, unchecked((uint)events[0].A));
        Assert.Equal(InputEvent.Move(3, -4).ToString(), events[1].ToString());
    }
}
