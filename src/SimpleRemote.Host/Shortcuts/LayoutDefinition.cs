namespace SimpleRemote.Shortcuts;

/// <summary>
/// A custom control page, declared in config.json and rendered generically by the web UI.
///
/// The point of this shape is that adding a page for a new app needs no code on either side: the
/// host compiles and validates the actions, the client renders whatever control types it is given.
/// </summary>
public sealed class LayoutDefinition
{
    public required string Id { get; set; }
    public required string Label { get; set; }
    public string? Icon { get; set; }

    public List<LayoutRow> Rows { get; set; } = [];

    public static List<LayoutDefinition> Defaults() => [Netflix()];

    /// <summary>
    /// Netflix, driven by the shortcuts the Netflix web player actually honours.
    ///
    /// S skips the intro, the arrow keys seek 10 seconds and F toggles fullscreen. The middle of the
    /// page is the same now-playing interface as the Media tab, so artwork, position and play/pause
    /// state come from the Windows media session; play/pause there is a real session command, not
    /// the space bar, so it cannot desynchronise from what is actually on screen.
    ///
    /// Previous/next track are hidden on purpose: for Netflix they mean jumping episodes, which is
    /// too easy to hit by accident from the couch. Artwork is hidden too - for video it is usually a
    /// random frame, and the room is better spent on the trackpad.
    ///
    /// Icons are names from the client's SVG icon set rather than Unicode glyphs, which many phone
    /// fonts cannot render.
    /// </summary>
    public static LayoutDefinition Netflix() => new()
    {
        Id = "netflix",
        Label = "Netflix",
        Icon = "film",
        Rows =
        [
            new LayoutRow
            {
                Controls =
                [
                    new LayoutControl
                    {
                        Type = "button", Label = "Skip intro", Icon = "skip", Accent = true,
                        Action = new ShortcutAction { Type = "keys", Target = "S" },
                    },
                ],
            },
            new LayoutRow
            {
                Controls =
                [
                    new LayoutControl
                    {
                        Type = "media",
                        TrackButtons = false,
                        Artwork = false,
                        SeekBackward = new ShortcutAction { Type = "keys", Target = "Left" },
                        SeekForward = new ShortcutAction { Type = "keys", Target = "Right" },
                    },
                ],
            },
            new LayoutRow { Controls = [new LayoutControl { Type = "volume" }] },
            new LayoutRow { Controls = [new LayoutControl { Type = "trackpad" }], Fill = true },
            new LayoutRow
            {
                Controls =
                [
                    new LayoutControl
                    {
                        Type = "button", Label = "Back", Icon = "back",
                        Action = new ShortcutAction { Type = "keys", Target = "BrowserBack" },
                    },
                    new LayoutControl
                    {
                        Type = "button", Label = "Fullscreen", Icon = "fullscreen",
                        Action = new ShortcutAction { Type = "keys", Target = "F" },
                    },
                ],
            },
        ],
    };
}

public sealed class LayoutRow
{
    public List<LayoutControl> Controls { get; set; } = [];

    /// <summary>Row takes the leftover vertical space. At most one row per layout should set this.</summary>
    public bool Fill { get; set; }
}

/// <summary>
/// One control in a layout.
///
/// <see cref="Type"/> is a string rather than an enum so that a client which knows a newer control
/// type still receives it, and an older client can skip what it does not recognise instead of
/// failing to render the page at all.
/// </summary>
public sealed class LayoutControl
{
    /// <summary>button | media | trackpad | volume | mouse | spacer | label.</summary>
    public string Type { get; set; } = "button";

    public string? Label { get; set; }
    /// <summary>
    /// A name from the client's icon set (play, pause, prev, next, rewind, forward, skip, back,
    /// fullscreen, film, grid), which renders as SVG, or any other text such as an emoji.
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>Draw this button as the primary action on its row.</summary>
    public bool Accent { get; set; }

    /// <summary>Relative width within the row. 1 unless a control should be wider than its siblings.</summary>
    public int Span { get; set; } = 1;

    /// <summary>Required for <c>button</c>; ignored by every other type.</summary>
    public ShortcutAction? Action { get; set; }

    /// <summary>
    /// For <c>media</c>: an action run by the rewind button, typically a key such as Left. Omit it
    /// and the button is not shown. Relative seeking is a host action rather than a media session
    /// command because browser video rarely reports a seekable timeline to the session.
    /// </summary>
    public ShortcutAction? SeekBackward { get; set; }

    /// <summary>For <c>media</c>: an action run by the fast-forward button. Omit it to hide the button.</summary>
    public ShortcutAction? SeekForward { get; set; }

    /// <summary>For <c>media</c>: show previous/next track buttons.</summary>
    public bool TrackButtons { get; set; } = true;

    /// <summary>For <c>media</c>: show the album-art thumbnail.</summary>
    public bool Artwork { get; set; } = true;
}
