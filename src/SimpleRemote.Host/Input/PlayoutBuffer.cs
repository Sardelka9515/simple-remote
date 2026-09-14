namespace SimpleRemote.Input;

/// <summary>
/// A jitter buffer for pointer and wheel motion: replays it at the pace the phone produced it,
/// rather than the pace the network delivered it.
///
/// Wi-Fi does not deliver a steady stream. Power-save wakeups, frame aggregation and the odd TCP
/// retransmit hold packets back and then release several at once, so the host receives a quarter
/// second of finger movement in one burst. Injected as it arrives, that is exactly the "cursor
/// suddenly jumps" symptom - however well the phone smoothed it before sending.
///
/// So every motion frame carries the phone's frame timestamp, and this buffer:
///
///  1. Maps phone time onto host time. The offset is the smallest (arrival - sent) seen recently:
///     the packet that waited least shows the true clock relationship, and everything slower is
///     network delay. It drifts upward slowly so clock drift and route changes are followed.
///  2. Plays out a little behind that - one send interval plus the recent worst jitter - so there is
///     normally data on hand when the playhead needs it, and a delayed packet has usually arrived
///     before its turn.
///  3. Spreads each frame's delta evenly across the time it covers, so the cursor moves in small
///     steps at the injection rate instead of one step per packet.
///  4. After a stall longer than the buffer, glides through the backlog at a bounded catch-up speed
///     rather than dumping it in one jump.
///
/// Pure logic with explicit timestamps: MotionPlayer supplies the clock and the thread.
/// </summary>
public sealed class PlayoutBuffer
{
    /// <summary>Upper bound on the playout delay, i.e. the most latency this may add.</summary>
    public double MaxBufferMs { get; set; } = 60;

    private const double MinBufferMs = 4;

    /// <summary>Playhead speed while working through a backlog, relative to real time.</summary>
    private const double CatchUpRate = 2.5;

    /// <summary>A backlog beyond this is played out immediately: gliding through it would lag badly.</summary>
    private const double MaxBacklogMs = 300;

    /// <summary>A gap in a lane's timestamps longer than this starts a new gesture.</summary>
    private const double IdleGapMs = 250;

    /// <summary>No traffic for this long and the clock relationship is measured afresh.</summary>
    private const double ClockResetMs = 1000;

    private const double JitterRelaxPerMs = 0.01;
    private const double OffsetDriftPerMs = 0.02;

    private readonly Lane _move = new();
    private readonly Lane _scroll = new();

    private double? _offset;
    private double _jitterPeak;
    private double _interval = 16.7;
    private double _lastArrival = double.NegativeInfinity;
    private double _lastAdvance = double.NaN;

    public bool HasPending => _move.Segments.Count > 0 || _scroll.Segments.Count > 0;

    /// <summary>Current playout delay behind the phone's clock.</summary>
    public double BufferMs => Math.Clamp(_interval + _jitterPeak + 2, MinBufferMs, Math.Max(MaxBufferMs, MinBufferMs));

    /// <summary>Queues one frame of motion made at <paramref name="clientMs"/> that arrived at <paramref name="hostMs"/>.</summary>
    public void Add(double clientMs, double hostMs, bool scroll, int dx, int dy)
    {
        UpdateClock(clientMs, hostMs);

        var lane = scroll ? _scroll : _move;

        if (lane.Segments.Count > 0 && clientMs <= lane.LastEnd)
        {
            // Stamped no later than the previous frame. It happens legitimately: a click forces a
            // flush stamped "now", and the animation frame that runs next carries its frame-start
            // time, which is earlier. Folding it into the previous segment could cram two frames of
            // motion into the sliver of that segment not yet played, so give it a short slot of its
            // own just after it instead.
            var slotStart = lane.LastEnd;
            var slotEnd = slotStart + _interval / 2;
            lane.Segments.Add(new Segment { Start = slotStart, End = slotEnd, ConsumedTo = slotStart, Dx = dx, Dy = dy });
            lane.LastEnd = slotEnd;
            return;
        }

        double start;
        if (lane.Segments.Count == 0 && (lane.Playhead is null || clientMs - lane.LastEnd > IdleGapMs || clientMs <= lane.LastEnd))
        {
            // A new gesture: pretend the frame took one normal interval, and start the playhead there.
            start = clientMs - _interval;
            lane.Playhead = start;
        }
        else
        {
            var gap = clientMs - lane.LastEnd;
            if (gap is > 0 and < 100) _interval += 0.1 * (gap - _interval);
            start = lane.LastEnd;
        }

        lane.Segments.Add(new Segment { Start = start, End = clientMs, ConsumedTo = start, Dx = dx, Dy = dy });
        lane.LastEnd = clientMs;
    }

    /// <summary>Motion due by <paramref name="hostMs"/>, in whole pixels / wheel units.</summary>
    public Output Advance(double hostMs)
    {
        var elapsed = double.IsNaN(_lastAdvance) ? 0 : Math.Clamp(hostMs - _lastAdvance, 0, 50);
        _lastAdvance = hostMs;

        if (_offset is not { } offset || !HasPending) return default;

        var desired = hostMs - offset - BufferMs;

        var (mx, my) = AdvanceLane(_move, desired, elapsed);
        var (sx, sy) = AdvanceLane(_scroll, desired, elapsed);
        return new Output(mx, my, sx, sy);
    }

    /// <summary>
    /// Everything queued, immediately. A click or key must land with the cursor where the finger
    /// actually put it, so pending motion goes out ahead of it.
    /// </summary>
    public Output Drain()
    {
        var (mx, my) = ConsumeLane(_move, double.PositiveInfinity);
        var (sx, sy) = ConsumeLane(_scroll, double.PositiveInfinity);
        return new Output(mx, my, sx, sy);
    }

    private void UpdateClock(double clientMs, double hostMs)
    {
        var sample = hostMs - clientMs;
        var sinceLast = hostMs - _lastArrival;
        _lastArrival = hostMs;

        if (_offset is not { } offset || sinceLast > ClockResetMs)
        {
            // Fresh start. Needed after idle too: a phone's clock can pause while it sleeps, which
            // moves the offset by however long it slept.
            _offset = sample;
            _jitterPeak = 0;
            return;
        }

        offset = Math.Min(sample, offset + OffsetDriftPerMs * sinceLast);
        _offset = offset;

        var jitter = sample - offset;
        _jitterPeak = jitter > _jitterPeak
            ? jitter
            : Math.Max(0, _jitterPeak - JitterRelaxPerMs * sinceLast);
    }

    private (int, int) AdvanceLane(Lane lane, double desired, double elapsed)
    {
        if (lane.Playhead is not { } playhead || lane.Segments.Count == 0) return (0, 0);

        // Never past the newest data: with nothing to play, the playhead waits for more.
        var target = Math.Min(desired, lane.LastEnd);
        if (target <= playhead) return (0, 0);

        // Normally the playhead keeps pace with real time exactly. When it has fallen behind - a
        // burst arrived late - it glides through the backlog at a bounded multiple of real time.
        var next = Math.Min(target, playhead + elapsed * CatchUpRate);

        // Far behind: play the oldest part out at once rather than lagging for seconds.
        var floor = desired - MaxBacklogMs;
        if (next < floor) next = Math.Min(floor, target);

        return ConsumeLane(lane, next);
    }

    private static (int, int) ConsumeLane(Lane lane, double upTo)
    {
        double x = 0, y = 0;

        while (lane.Segments.Count > 0)
        {
            var s = lane.Segments[0];
            var length = s.End - s.Start;
            var reach = Math.Min(upTo, s.End);

            if (reach >= s.End)
            {
                // Finish the segment exactly: whatever was not sent yet, so no rounding ever leaks.
                x += s.Dx - s.SentX;
                y += s.Dy - s.SentY;
                lane.Segments.RemoveAt(0);
                continue;
            }

            if (reach > s.ConsumedTo && length > 0)
            {
                var fraction = (reach - s.ConsumedTo) / length;
                var px = s.Dx * fraction;
                var py = s.Dy * fraction;
                s.SentX += px;
                s.SentY += py;
                s.ConsumedTo = reach;
                lane.Segments[0] = s;
                x += px;
                y += py;
            }

            break;
        }

        if (!double.IsPositiveInfinity(upTo)) lane.Playhead = Math.Max(lane.Playhead ?? upTo, upTo);
        else lane.Playhead = lane.LastEnd;

        lane.RemainderX += x;
        lane.RemainderY += y;

        // A finished lane has delivered whole input deltas, so its remainder is float residue:
        // round it out rather than carrying a phantom fraction into the next gesture.
        var ix = lane.Segments.Count == 0 ? (int)Math.Round(lane.RemainderX) : (int)Math.Truncate(lane.RemainderX);
        var iy = lane.Segments.Count == 0 ? (int)Math.Round(lane.RemainderY) : (int)Math.Truncate(lane.RemainderY);
        lane.RemainderX = lane.Segments.Count == 0 ? 0 : lane.RemainderX - ix;
        lane.RemainderY = lane.Segments.Count == 0 ? 0 : lane.RemainderY - iy;

        return (ix, iy);
    }

    public readonly record struct Output(int MoveX, int MoveY, int ScrollX, int ScrollY)
    {
        public bool IsEmpty => MoveX == 0 && MoveY == 0 && ScrollX == 0 && ScrollY == 0;
    }

    private struct Segment
    {
        public double Start;
        public double End;
        public double ConsumedTo;
        public int Dx;
        public int Dy;
        public double SentX;
        public double SentY;
    }

    private sealed class Lane
    {
        public readonly List<Segment> Segments = [];
        public double? Playhead;
        public double LastEnd = double.NegativeInfinity;
        public double RemainderX;
        public double RemainderY;
    }
}
