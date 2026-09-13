using System.Diagnostics;
using SimpleRemote.Config;
using SimpleRemote.Input;
using SimpleRemote.Server.Protocol;

namespace SimpleRemote.Shortcuts;

public sealed class ShortcutAction
{
    /// <summary>keys | launch.</summary>
    public string Type { get; set; } = "keys";

    /// <summary>A combo such as "Ctrl+Shift+Esc", or a path/URL for launch.</summary>
    public string Target { get; set; } = "";
}

public sealed class ShortcutDefinition
{
    public required string Id { get; set; }
    public required string Label { get; set; }
    public string? Icon { get; set; }
    public ShortcutAction Action { get; set; } = new();

    public static List<ShortcutDefinition> Defaults() =>
    [
        new() { Id = "fullscreen", Label = "Fullscreen", Icon = "⛶", Action = new ShortcutAction { Type = "keys", Target = "F" } },
        new() { Id = "mute", Label = "Mute", Icon = "\U0001F507", Action = new ShortcutAction { Type = "keys", Target = "VolumeMute" } },
        new() { Id = "close", Label = "Close window", Icon = "✕", Action = new ShortcutAction { Type = "keys", Target = "Alt+F4" } },
        new() { Id = "desktop", Label = "Show desktop", Icon = "\U0001F5A5", Action = new ShortcutAction { Type = "keys", Target = "Win+D" } },
        new() { Id = "project", Label = "Project", Icon = "\U0001F4FA", Action = new ShortcutAction { Type = "keys", Target = "Win+P" } },
        new() { Id = "tasks", Label = "Task manager", Icon = "⚙", Action = new ShortcutAction { Type = "keys", Target = "Ctrl+Shift+Esc" } },
    ];
}

/// <summary>
/// Runs the user-defined shortcut buttons.
///
/// Combos are parsed once at load rather than per invocation, both to keep the invoke path cheap
/// and so a typo in config.json surfaces as a missing button at startup instead of a silent no-op
/// the first time someone taps it.
/// </summary>
public sealed class ShortcutService(ConfigStore config, InputInjector injector)
{
    private readonly Dictionary<string, (ShortcutDefinition Def, KeyCombo? Combo)> _compiled = new(StringComparer.Ordinal);
    private readonly List<string> _errors = [];

    public IReadOnlyList<string> Errors => _errors;

    public void Reload()
    {
        _compiled.Clear();
        _errors.Clear();

        foreach (var def in config.Current.Shortcuts)
        {
            if (string.IsNullOrWhiteSpace(def.Id) || _compiled.ContainsKey(def.Id))
            {
                _errors.Add($"Shortcut with missing or duplicate id: {def.Id}");
                continue;
            }

            switch (def.Action.Type?.ToLowerInvariant())
            {
                case "keys":
                    if (!KeyComboParser.TryParse(def.Action.Target, out var combo))
                    {
                        _errors.Add($"Shortcut '{def.Id}': cannot parse key combo '{def.Action.Target}'");
                        continue;
                    }
                    _compiled[def.Id] = (def, combo);
                    break;

                case "launch":
                    if (string.IsNullOrWhiteSpace(def.Action.Target))
                    {
                        _errors.Add($"Shortcut '{def.Id}': launch target is empty");
                        continue;
                    }
                    _compiled[def.Id] = (def, null);
                    break;

                default:
                    _errors.Add($"Shortcut '{def.Id}': unknown action type '{def.Action.Type}'");
                    break;
            }
        }
    }

    /// <summary>The shortcut list the web UI renders. Only entries that actually work are included.</summary>
    public List<ShortcutInfo> Describe() =>
        [.. _compiled.Values.Select(v => new ShortcutInfo { Id = v.Def.Id, Label = v.Def.Label, Icon = v.Def.Icon })];

    public bool Invoke(string? id)
    {
        if (id is null || !_compiled.TryGetValue(id, out var entry)) return false;

        if (entry.Combo is { } combo)
        {
            injector.SendCombo(combo.Modifiers, combo.Key);
            return true;
        }

        try
        {
            // UseShellExecute lets a target be a URL, a document, or a Store app, not just an exe.
            Process.Start(new ProcessStartInfo(entry.Def.Action.Target) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }
}
