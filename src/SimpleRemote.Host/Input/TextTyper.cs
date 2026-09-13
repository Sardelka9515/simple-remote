namespace SimpleRemote.Input;

/// <summary>
/// Types arbitrary text by injecting UTF-16 code units directly.
///
/// KEYEVENTF_UNICODE bypasses the keyboard layout entirely, which is the only sane approach here:
/// the on-screen keyboard on the phone has no relationship to the active layout on the PC, so
/// mapping characters to virtual keys would break for anyone on a non-US layout, and would never
/// handle emoji or CJK at all.
/// </summary>
public static class TextTyper
{
    /// <summary>Cap per message, so pasting a huge document cannot stall the input queue.</summary>
    public const int MaxLength = 4096;

    private const ushort VkReturn = 0x0D;

    public static unsafe void Type(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length > MaxLength) text = text[..MaxLength];

        // Two INPUTs per code unit (down + up). Chunked so the stack buffer stays small.
        const int chunkChars = 64;
        var buffer = stackalloc INPUT[chunkChars * 2];

        for (var offset = 0; offset < text.Length; offset += chunkChars)
        {
            var slice = text.AsSpan(offset, Math.Min(chunkChars, text.Length - offset));
            var n = 0;

            foreach (var ch in slice)
            {
                // Newlines arrive as \n or \r\n, but edit controls expect a real Return keypress -
                // injecting U+000A as a character does nothing useful in most of them.
                if (ch == 13) continue;
                if (ch == 10)
                {
                    buffer[n++] = InputInjector.Keyboard(VkReturn, true);
                    buffer[n++] = InputInjector.Keyboard(VkReturn, false);
                    continue;
                }

                // Surrogate pairs work because each UTF-16 code unit is emitted separately and
                // Windows recombines them, which is exactly why this iterates chars, not runes.
                buffer[n++] = Unicode(ch, down: true);
                buffer[n++] = Unicode(ch, down: false);
            }

            if (n > 0) NativeMethods.SendInput((uint)n, buffer, sizeof(INPUT));
        }
    }

    private static INPUT Unicode(char ch, bool down) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = ch,
                dwFlags = NativeMethods.KEYEVENTF_UNICODE | (down ? 0 : NativeMethods.KEYEVENTF_KEYUP),
            },
        },
    };
}
