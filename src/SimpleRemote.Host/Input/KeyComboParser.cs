using System.Diagnostics.CodeAnalysis;

namespace SimpleRemote.Input;

/// <summary>A parsed key combination: zero or more modifiers plus one terminal key.</summary>
public readonly record struct KeyCombo(ushort[] Modifiers, ushort Key)
{
    public override string ToString() => $"[{string.Join("+", Modifiers)}]+{Key}";
}

/// <summary>
/// Parses human-written combos such as "Ctrl+Shift+Esc" into virtual-key sequences.
///
/// This exists so shortcuts live in config.json rather than in code. It is deliberately pure and
/// total: every failure path returns false rather than throwing, because the input is a
/// user-edited file and a typo there must not crash the host.
/// </summary>
public static class KeyComboParser
{
    private static readonly Dictionary<string, ushort> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11,
        ["alt"] = 0x12, ["menu"] = 0x12,
        ["shift"] = 0x10,
        ["win"] = 0x5B, ["super"] = 0x5B, ["meta"] = 0x5B, ["cmd"] = 0x5B,
    };

    private static readonly Dictionary<string, ushort> Keys = BuildKeyTable();

    /// <summary>Names accepted for the terminal key, for docs and error messages.</summary>
    public static IEnumerable<string> KnownKeyNames => Keys.Keys.Order(StringComparer.OrdinalIgnoreCase);

    public static bool TryParse(string? text, [NotNullWhen(true)] out KeyCombo? combo)
    {
        combo = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        var modifiers = new List<ushort>();
        ushort key = 0;

        foreach (var part in parts)
        {
            if (Modifiers.TryGetValue(part, out var mod))
            {
                // Duplicate modifiers are harmless but pointless; collapse them so the
                // down/up sequence stays balanced.
                if (!modifiers.Contains(mod)) modifiers.Add(mod);
                continue;
            }

            // Anything that is not a modifier must be the terminal key, and there can be only one.
            if (key != 0) return false;
            if (!TryResolveKey(part, out key)) return false;
        }

        if (key == 0) return false; // modifiers alone are not a shortcut

        combo = new KeyCombo([.. modifiers], key);
        return true;
    }

    private static bool TryResolveKey(string name, out ushort vk)
    {
        if (Keys.TryGetValue(name, out vk)) return true;

        // Single printable characters map straight onto their VK for A-Z and 0-9.
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = c;
                return true;
            }
        }

        vk = 0;
        return false;
    }

    private static Dictionary<string, ushort> BuildKeyTable()
    {
        var table = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["backspace"] = 0x08, ["tab"] = 0x09, ["enter"] = 0x0D, ["return"] = 0x0D,
            ["pause"] = 0x13, ["capslock"] = 0x14, ["esc"] = 0x1B, ["escape"] = 0x1B,
            ["space"] = 0x20, ["pageup"] = 0x21, ["pagedown"] = 0x22,
            ["end"] = 0x23, ["home"] = 0x24,
            ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
            ["printscreen"] = 0x2C, ["insert"] = 0x2D, ["delete"] = 0x2E, ["del"] = 0x2E,
            ["apps"] = 0x5D, ["menukey"] = 0x5D,
            ["numlock"] = 0x90, ["scrolllock"] = 0x91,

            // Media and volume keys, so shortcuts can reach players that ignore SMTC.
            ["volumemute"] = 0xAD, ["volumedown"] = 0xAE, ["volumeup"] = 0xAF,
            ["medianext"] = 0xB0, ["mediaprev"] = 0xB1,
            ["mediastop"] = 0xB2, ["mediaplaypause"] = 0xB3,
            ["browserback"] = 0xA6, ["browserforward"] = 0xA7,

            [";"] = 0xBA, ["="] = 0xBB, [","] = 0xBC, ["-"] = 0xBD, ["."] = 0xBE, ["/"] = 0xBF,
            ["`"] = 0xC0, ["["] = 0xDB, ["\\"] = 0xDC, ["]"] = 0xDD, ["'"] = 0xDE,
        };

        for (ushort i = 1; i <= 24; i++) table[$"f{i}"] = (ushort)(0x6F + i);       // F1-F24
        for (ushort i = 0; i <= 9; i++) table[$"num{i}"] = (ushort)(0x60 + i);      // numpad digits

        return table;
    }
}
