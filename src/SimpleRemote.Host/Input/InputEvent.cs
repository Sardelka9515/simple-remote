namespace SimpleRemote.Input;

public enum InputKind : byte
{
    MouseMove,
    MouseButton,
    Scroll,
    Key,
}

public enum MouseButton : byte
{
    Left = 0,
    Right = 1,
    Middle = 2,
}

/// <summary>
/// Transport-neutral input event. The binary codec decodes into these and the injector translates
/// them into Win32 INPUT records - keeping the wire format testable without P/Invoking anything.
/// </summary>
public readonly struct InputEvent(InputKind kind, int a, int b)
{
    public readonly InputKind Kind = kind;

    /// <summary>dx | button | scrollX | virtual-key.</summary>
    public readonly int A = a;

    /// <summary>dy | pressed | scrollY | pressed.</summary>
    public readonly int B = b;

    public static InputEvent Move(int dx, int dy) => new(InputKind.MouseMove, dx, dy);
    public static InputEvent Button(MouseButton button, bool down) => new(InputKind.MouseButton, (int)button, down ? 1 : 0);
    public static InputEvent Scroll(int dx, int dy) => new(InputKind.Scroll, dx, dy);
    public static InputEvent Key(int vk, bool down) => new(InputKind.Key, vk, down ? 1 : 0);

    public override string ToString() => $"{Kind}({A},{B})";
}
