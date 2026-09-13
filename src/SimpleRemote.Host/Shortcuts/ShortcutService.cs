using System.Diagnostics;
using SimpleRemote.Config;
using SimpleRemote.Input;
using SimpleRemote.Media;
using SimpleRemote.Server.Protocol;

namespace SimpleRemote.Shortcuts;

public sealed class ShortcutAction
{
    /// <summary>keys | launch | media.</summary>
    public string Type { get; set; } = "keys";

    /// <summary>
    /// A combo such as "Ctrl+Shift+Esc" for <c>keys</c>, a path or URL for <c>launch</c>, or one of
    /// playpause/play/pause/next/prev/stop for <c>media</c>.
    /// </summary>
    public string Target { get; set; } = "";

    /// <summary>
    /// How many times to fire a <c>keys</c> action. Used for things like seeking, where the target
    /// application only understands one fixed step per keypress.
    /// </summary>
    public int Repeat { get; set; } = 1;
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
/// Compiles and runs everything the phone can invoke by id: the shortcut buttons and every action
/// embedded in a custom layout.
///
/// Layout actions are registered here rather than given their own execution path, so a button in a
/// layout is validated, invoked and reported on exactly like a shortcut. That keeps one code path
/// for "the phone asked us to do a thing" no matter which page the button lives on.
///
/// Compilation happens once at load, so a typo in config.json shows up as a missing button at
/// startup instead of a silent no-op the first time someone taps it.
/// </summary>
public sealed class ShortcutService(ConfigStore config, InputInjector injector, MediaController media)
{
    /// <summary>Upper bound on Repeat, so a stray large value cannot wedge the input queue.</summary>
    public const int MaxRepeat = 50;

    /// <summary>
    /// Gap between repeats. Applications that seek per keypress drop keys delivered faster than
    /// they can process them, so nine instant presses would seek far less than nine steps.
    /// </summary>
    private static readonly TimeSpan RepeatGap = TimeSpan.FromMilliseconds(35);

    private readonly Dictionary<string, CompiledAction> _actions = new(StringComparer.Ordinal);
    private readonly List<ShortcutInfo> _shortcuts = [];
    private readonly List<LayoutInfo> _layouts = [];
    private readonly List<string> _errors = [];

    public IReadOnlyList<string> Errors => _errors;

    public void Reload()
    {
        _actions.Clear();
        _shortcuts.Clear();
        _layouts.Clear();
        _errors.Clear();

        CompileShortcuts();
        CompileLayouts();
    }

    private void CompileShortcuts()
    {
        foreach (var def in config.Current.Shortcuts)
        {
            if (string.IsNullOrWhiteSpace(def.Id))
            {
                _errors.Add("Shortcut with a missing id");
                continue;
            }

            if (!TryCompile(def.Action, $"shortcut '{def.Id}'", out var compiled))
                continue;

            if (!_actions.TryAdd(def.Id, compiled))
            {
                _errors.Add($"Duplicate shortcut id '{def.Id}'");
                continue;
            }

            _shortcuts.Add(new ShortcutInfo { Id = def.Id, Label = def.Label, Icon = def.Icon });
        }
    }

    private void CompileLayouts()
    {
        foreach (var layout in config.Current.Layouts)
        {
            if (string.IsNullOrWhiteSpace(layout.Id))
            {
                _errors.Add("Layout with a missing id");
                continue;
            }

            var info = new LayoutInfo { Id = layout.Id, Label = layout.Label, Icon = layout.Icon };

            for (var r = 0; r < layout.Rows.Count; r++)
            {
                var row = layout.Rows[r];
                var rowInfo = new LayoutRowInfo { Fill = row.Fill };

                for (var c = 0; c < row.Controls.Count; c++)
                {
                    var control = row.Controls[c];
                    var controlInfo = new LayoutControlInfo
                    {
                        Type = string.IsNullOrWhiteSpace(control.Type) ? "button" : control.Type.ToLowerInvariant(),
                        Label = control.Label,
                        Icon = control.Icon,
                        Accent = control.Accent,
                        Span = Math.Clamp(control.Span, 1, 6),
                    };

                    if (controlInfo.Type == "button")
                    {
                        if (control.Action is null)
                        {
                            _errors.Add($"Layout '{layout.Id}' row {r} control {c}: button without an action");
                            continue;
                        }

                        // Ids are derived from position so they stay stable across restarts for an
                        // unchanged config, and can never collide with a user-chosen shortcut id.
                        var id = $"layout:{layout.Id}:{r}:{c}";
                        if (!TryCompile(control.Action, $"layout '{layout.Id}' row {r} control {c}", out var compiled))
                            continue;

                        _actions[id] = compiled;
                        controlInfo.ActionId = id;
                    }
                    else if (controlInfo.Type == "media")
                    {
                        controlInfo.TrackButtons = control.TrackButtons;
                        controlInfo.Artwork = control.Artwork;

                        // Seek buttons are optional. One that fails to compile is reported and hidden,
                        // but the rest of the media panel still renders.
                        var prefix = $"layout:{layout.Id}:{r}:{c}";
                        var where = $"layout '{layout.Id}' row {r} control {c}";

                        if (control.SeekBackward is not null
                            && TryCompile(control.SeekBackward, where + " seekBackward", out var back))
                        {
                            _actions[prefix + ":back"] = back;
                            controlInfo.SeekBackwardId = prefix + ":back";
                        }

                        if (control.SeekForward is not null
                            && TryCompile(control.SeekForward, where + " seekForward", out var forward))
                        {
                            _actions[prefix + ":forward"] = forward;
                            controlInfo.SeekForwardId = prefix + ":forward";
                        }
                    }

                    rowInfo.Controls.Add(controlInfo);
                }

                if (rowInfo.Controls.Count > 0) info.Rows.Add(rowInfo);
            }

            if (info.Rows.Count > 0) _layouts.Add(info);
        }
    }

    private bool TryCompile(ShortcutAction action, string where, out CompiledAction compiled)
    {
        compiled = default;
        var repeat = Math.Clamp(action.Repeat <= 0 ? 1 : action.Repeat, 1, MaxRepeat);

        switch (action.Type?.ToLowerInvariant())
        {
            case "keys":
                if (!KeyComboParser.TryParse(action.Target, out var combo))
                {
                    _errors.Add($"{where}: cannot parse key combo '{action.Target}'");
                    return false;
                }
                compiled = new CompiledAction("keys", combo, null, repeat);
                return true;

            case "launch":
                if (string.IsNullOrWhiteSpace(action.Target))
                {
                    _errors.Add($"{where}: launch target is empty");
                    return false;
                }
                compiled = new CompiledAction("launch", null, action.Target, 1);
                return true;

            case "media":
                var command = action.Target?.ToLowerInvariant();
                if (command is not ("playpause" or "play" or "pause" or "next" or "prev" or "stop"))
                {
                    _errors.Add($"{where}: unknown media command '{action.Target}'");
                    return false;
                }
                compiled = new CompiledAction("media", null, command, 1);
                return true;

            default:
                _errors.Add($"{where}: unknown action type '{action.Type}'");
                return false;
        }
    }

    /// <summary>The shortcut buttons the web UI renders. Only entries that actually work appear.</summary>
    public List<ShortcutInfo> Describe() => [.. _shortcuts];

    /// <summary>The custom layout pages the web UI renders.</summary>
    public List<LayoutInfo> DescribeLayouts() => [.. _layouts];

    /// <summary>
    /// Whether an id resolves to a compiled action, without running it.
    ///
    /// Exists so the wiring can be asserted without injecting real keystrokes into whatever window
    /// happens to have focus.
    /// </summary>
    public bool CanInvoke(string? id) => id is not null && _actions.ContainsKey(id);

    public async Task<bool> InvokeAsync(string? id)
    {
        if (id is null || !_actions.TryGetValue(id, out var action)) return false;

        switch (action.Kind)
        {
            case "keys":
                var combo = action.Combo!.Value;
                for (var i = 0; i < action.Repeat; i++)
                {
                    injector.SendCombo(combo.Modifiers, combo.Key);
                    if (i < action.Repeat - 1) await Task.Delay(RepeatGap).ConfigureAwait(false);
                }
                return true;

            case "media":
                return await media.CommandAsync(action.Target, null).ConfigureAwait(false);

            case "launch":
                try
                {
                    // UseShellExecute lets a target be a URL, a document or a Store app.
                    Process.Start(new ProcessStartInfo(action.Target!) { UseShellExecute = true })?.Dispose();
                    return true;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                               or InvalidOperationException or FileNotFoundException)
                {
                    return false;
                }

            default:
                return false;
        }
    }

    private readonly record struct CompiledAction(string Kind, KeyCombo? Combo, string? Target, int Repeat);
}
