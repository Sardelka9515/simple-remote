using System.Runtime.CompilerServices;

namespace SimpleRemote.Input;

/// <summary>
/// Translates <see cref="InputEvent"/>s into Win32 SendInput calls.
///
/// The whole class is allocation-free and batches an entire WebSocket message into a single
/// SendInput call. Batching is not just a micro-optimisation: SendInput guarantees that the events
/// in one call are injected contiguously, so a click cannot be interleaved with another thread
/// synthetic input mid-sequence.
/// </summary>
public sealed class InputInjector
{
    /// <summary>Upper bound on events translated from one message. Sized to fit on the stack.</summary>
    public const int MaxBatch = 64;

    /// <summary>
    /// When false, wheel motion is quantised to whole 120-unit notches for the benefit of
    /// applications that integer-divide the delta. See PointerConfig.SmoothScroll.
    /// </summary>
    public bool SmoothScroll { get; set; } = true;

    // Only used in quantised mode, to carry the sub-notch remainder so slow scrolling still works.
    private int _scrollRemainderX;
    private int _scrollRemainderY;

    public unsafe void Inject(ReadOnlySpan<InputEvent> events)
    {
        if (events.Length == 0) return;

        var buffer = stackalloc INPUT[MaxBatch];
        var n = 0;

        foreach (var e in events)
        {
            if (n >= MaxBatch) break;

            switch (e.Kind)
            {
                case InputKind.MouseMove:
                    if (e.A == 0 && e.B == 0) continue;
                    buffer[n++] = Mouse(e.A, e.B, 0, NativeMethods.MOUSEEVENTF_MOVE);
                    break;

                case InputKind.MouseButton:
                    var flag = ButtonFlag((MouseButton)e.A, e.B != 0);
                    if (flag == 0) continue;
                    buffer[n++] = Mouse(0, 0, 0, flag);
                    break;

                case InputKind.Scroll:
                    n += Scroll(buffer + n, MaxBatch - n, e.A, e.B);
                    break;

                case InputKind.Key:
                    if (e.A is <= 0 or > 0xFF) continue;
                    buffer[n++] = Keyboard((ushort)e.A, e.B != 0);
                    break;
            }
        }

        if (n > 0) NativeMethods.SendInput((uint)n, buffer, sizeof(INPUT));
    }

    public void Inject(InputEvent single) => Inject(new ReadOnlySpan<InputEvent>(in single));

    /// <summary>Presses and releases a virtual key.</summary>
    public void Tap(ushort vk)
    {
        Span<InputEvent> pair = [InputEvent.Key(vk, true), InputEvent.Key(vk, false)];
        Inject(pair);
    }

    /// <summary>Runs a parsed key combo: modifiers down, key tap, modifiers up in reverse order.</summary>
    public void SendCombo(ReadOnlySpan<ushort> modifiers, ushort key)
    {
        Span<InputEvent> events = stackalloc InputEvent[MaxBatch];
        var n = 0;

        foreach (var m in modifiers)
            if (n < MaxBatch) events[n++] = InputEvent.Key(m, true);

        if (key != 0 && n + 1 < MaxBatch)
        {
            events[n++] = InputEvent.Key(key, true);
            events[n++] = InputEvent.Key(key, false);
        }

        for (var i = modifiers.Length - 1; i >= 0; i--)
            if (n < MaxBatch) events[n++] = InputEvent.Key(modifiers[i], false);

        Inject(events[..n]);
    }

    /// <summary>
    /// Emits high-resolution wheel motion.
    ///
    /// Deltas are passed through at 1-unit granularity rather than being quantised to whole
    /// 120-unit notches. Quantising is what makes scrolling feel abrupt: nothing moves until the
    /// finger has travelled a whole notch, then the view jumps three lines at once.
    ///
    /// Sub-notch deltas are exactly what a Windows precision touchpad sends, and what smooth
    /// scrolling in browsers and modern apps consumes. The client carries the sub-unit fraction,
    /// so nothing is lost to rounding on the way here.
    /// </summary>
    private unsafe int Scroll(INPUT* dest, int capacity, int dx, int dy)
    {
        if (!SmoothScroll) return ScrollQuantised(dest, capacity, dx, dy);

        var n = 0;

        if (dy != 0 && n < capacity)
            dest[n++] = Mouse(0, 0, unchecked((uint)dy), NativeMethods.MOUSEEVENTF_WHEEL);

        if (dx != 0 && n < capacity)
            dest[n++] = Mouse(0, 0, unchecked((uint)dx), NativeMethods.MOUSEEVENTF_HWHEEL);

        return n;
    }

    /// <summary>Legacy path: whole notches only, carrying the remainder so slow drags still move.</summary>
    private unsafe int ScrollQuantised(INPUT* dest, int capacity, int dx, int dy)
    {
        var n = 0;

        _scrollRemainderY += dy;
        var notchesY = _scrollRemainderY / NativeMethods.WHEEL_DELTA;
        if (notchesY != 0 && n < capacity)
        {
            _scrollRemainderY -= notchesY * NativeMethods.WHEEL_DELTA;
            dest[n++] = Mouse(0, 0, unchecked((uint)(notchesY * NativeMethods.WHEEL_DELTA)),
                NativeMethods.MOUSEEVENTF_WHEEL);
        }

        _scrollRemainderX += dx;
        var notchesX = _scrollRemainderX / NativeMethods.WHEEL_DELTA;
        if (notchesX != 0 && n < capacity)
        {
            _scrollRemainderX -= notchesX * NativeMethods.WHEEL_DELTA;
            dest[n++] = Mouse(0, 0, unchecked((uint)(notchesX * NativeMethods.WHEEL_DELTA)),
                NativeMethods.MOUSEEVENTF_HWHEEL);
        }

        return n;
    }

    private static uint ButtonFlag(MouseButton button, bool down) => button switch
    {
        MouseButton.Left => down ? NativeMethods.MOUSEEVENTF_LEFTDOWN : NativeMethods.MOUSEEVENTF_LEFTUP,
        MouseButton.Right => down ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_RIGHTUP,
        MouseButton.Middle => down ? NativeMethods.MOUSEEVENTF_MIDDLEDOWN : NativeMethods.MOUSEEVENTF_MIDDLEUP,
        _ => 0,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static INPUT Mouse(int dx, int dy, uint data, uint flags) => new()
    {
        type = NativeMethods.INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } },
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static INPUT Keyboard(ushort vk, bool down) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = (down ? 0 : NativeMethods.KEYEVENTF_KEYUP)
                          | (IsExtended(vk) ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0),
            },
        },
    };

    // Keys on the extended part of the keyboard need the extended flag, or applications mistake
    // them for their numpad twins - arrows would still move, but Home/End would type digits.
    private static bool IsExtended(ushort vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or
        0x25 or 0x26 or 0x27 or 0x28 or
        0x2D or 0x2E or
        0x5B or 0x5C or 0x5D or
        0xA3 or 0xA5 or
        0x90;
}
