using System.Diagnostics;

namespace SimpleRemote.Input;

/// <summary>
/// Feeds one connection's input to the injector, pacing motion through a <see cref="PlayoutBuffer"/>.
///
/// Motion stamped with a phone frame time is queued and injected from a dedicated thread every
/// couple of milliseconds, at the pace it was made. Everything else is injected immediately, but
/// never ahead of motion that preceded it: a click first drains the queue, so it lands exactly
/// where the finger put the cursor.
///
/// The thread sleeps on an event while nothing is queued, so an idle connection costs nothing.
/// </summary>
public sealed class MotionPlayer : IDisposable
{
    private const int TickMs = 2;

    private readonly InputInjector _injector;
    private readonly bool _enabled;
    private readonly PlayoutBuffer _buffer;
    private readonly Lock _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread? _thread;
    private volatile bool _stopping;

    // Unwraps the phone's 32-bit, 0.1ms clock into a continuous millisecond timeline.
    private uint _lastRawTime;
    private double _clientMs = double.NaN;

    public MotionPlayer(InputInjector injector, bool enabled, double maxBufferMs)
    {
        _injector = injector;
        _enabled = enabled;
        _buffer = new PlayoutBuffer { MaxBufferMs = maxBufferMs };

        if (!_enabled) return;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "SimpleRemote motion playout",
            // Pacing is only as even as this thread's wakeups.
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    private static double NowMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// <summary>Takes one decoded message's events, in order.</summary>
    public void Submit(ReadOnlySpan<InputEvent> events)
    {
        Span<InputEvent> immediate = stackalloc InputEvent[InputInjector.MaxBatch];
        var n = 0;
        var queued = false;
        double? frameTime = null;
        var now = NowMs;

        lock (_gate)
        {
            foreach (var e in events)
            {
                switch (e.Kind)
                {
                    case InputKind.FrameTime:
                        frameTime = Unwrap(unchecked((uint)e.A));
                        break;

                    case InputKind.MouseMove or InputKind.Scroll when _enabled && frameTime is { } t:
                        _buffer.Add(t, now, e.Kind == InputKind.Scroll, e.A, e.B);
                        queued = true;
                        break;

                    case InputKind.MouseMove or InputKind.Scroll:
                        if (n < immediate.Length) immediate[n++] = e;
                        break;

                    default:
                        // A button or key: whatever motion came before it goes first.
                        if (_buffer.HasPending) n = Append(immediate, n, _buffer.Drain());
                        if (n < immediate.Length) immediate[n++] = e;
                        break;
                }
            }

            if (n > 0) _injector.Inject(immediate[..n]);
        }

        if (queued) _wake.Set();
    }

    private double Unwrap(uint raw)
    {
        if (double.IsNaN(_clientMs))
        {
            _clientMs = raw / 10.0;
        }
        else
        {
            // Signed difference, so both the wrap and a slightly out-of-order stamp come out right.
            _clientMs += unchecked((int)(raw - _lastRawTime)) / 10.0;
        }

        _lastRawTime = raw;
        return _clientMs;
    }

    private static int Append(Span<InputEvent> destination, int n, PlayoutBuffer.Output output)
    {
        if ((output.MoveX != 0 || output.MoveY != 0) && n < destination.Length)
            destination[n++] = InputEvent.Move(output.MoveX, output.MoveY);
        if ((output.ScrollX != 0 || output.ScrollY != 0) && n < destination.Length)
            destination[n++] = InputEvent.Scroll(output.ScrollX, output.ScrollY);
        return n;
    }

    private void Run()
    {
        var timer = NativeMethods.CreateWaitableTimerEx(
            0, 0, NativeMethods.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, NativeMethods.TIMER_ALL_ACCESS);

        try
        {
            Span<InputEvent> batch = stackalloc InputEvent[2];

            while (!_stopping)
            {
                bool pending;
                lock (_gate)
                {
                    var output = _buffer.Advance(NowMs);
                    var n = Append(batch, 0, output);
                    if (n > 0) _injector.Inject(batch[..n]);
                    pending = _buffer.HasPending;
                }

                if (!pending)
                {
                    _wake.WaitOne();
                    continue;
                }

                Sleep(timer);
            }
        }
        catch (ObjectDisposedException)
        {
            // Dispose gave up waiting for this thread and released the event; it was stopping anyway.
        }
        finally
        {
            if (timer != 0) NativeMethods.CloseHandle(timer);
        }
    }

    private static void Sleep(nint timer)
    {
        if (timer != 0)
        {
            // Negative means relative, in 100ns units.
            long due = -TickMs * 10_000L;
            if (NativeMethods.SetWaitableTimer(timer, in due, 0, 0, 0, false))
            {
                NativeMethods.WaitForSingleObject(timer, NativeMethods.INFINITE);
                return;
            }
        }

        // Older Windows without high-resolution timers: coarser, but still correct.
        Thread.Sleep(1);
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread?.Join(TimeSpan.FromMilliseconds(200));
        _wake.Dispose();
    }
}
