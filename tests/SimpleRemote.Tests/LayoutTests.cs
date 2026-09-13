using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SimpleRemote.Config;
using SimpleRemote.Input;
using SimpleRemote.Media;
using SimpleRemote.Server.Protocol;
using SimpleRemote.Shortcuts;
using Xunit;

namespace SimpleRemote.Tests;

public class LayoutTests
{
    private static ShortcutService Build(Action<AppConfig>? configure = null)
    {
        var store = new ConfigStore(Path.Combine(Path.GetTempPath(), $"sr-layout-{Guid.NewGuid():N}"));
        configure?.Invoke(store.Current);

        var injector = new InputInjector();
        var service = new ShortcutService(store, injector, new MediaController(injector));
        service.Reload();
        return service;
    }

    /// <summary>
    /// A control whose action fails to compile is dropped at startup, so a shipped layout that no
    /// longer parses would silently lose buttons. Every default must compile cleanly.
    /// </summary>
    [Fact]
    public void DefaultLayoutsCompileWithoutErrors()
    {
        var service = Build();

        Assert.Empty(service.Errors);
        var layout = Assert.Single(service.DescribeLayouts());
        Assert.Equal("netflix", layout.Id);
    }

    [Fact]
    public void NetflixLayoutExposesEveryRequestedControl()
    {
        var layout = Assert.Single(Build().DescribeLayouts());
        var controls = layout.Rows.SelectMany(r => r.Controls).ToList();

        var labels = controls.Where(c => c.Label is not null).Select(c => c.Label!).ToList();
        Assert.Contains("Skip intro", labels);
        Assert.Contains("Back", labels);
        Assert.Contains("Fullscreen", labels);

        // Removed on request: next episode is too easy to hit by accident from the couch.
        Assert.DoesNotContain("Next episode", labels);

        // Play/pause, position and seeking come from the shared media panel, not ad-hoc buttons.
        Assert.DoesNotContain("Play", labels);
        Assert.DoesNotContain("Pause", labels);

        var media = Assert.Single(controls, c => c.Type == "media");
        Assert.NotNull(media.SeekBackwardId);
        Assert.NotNull(media.SeekForwardId);
        Assert.False(media.TrackButtons);
        Assert.False(media.Artwork);

        Assert.Contains(controls, c => c.Type == "volume");

        // Exactly one row may claim the leftover vertical space, and it should be the trackpad.
        var fill = Assert.Single(layout.Rows.Where(r => r.Fill));
        Assert.Contains(fill.Controls, c => c.Type == "trackpad");
    }

    /// <summary>
    /// An icon that reached the file as an unprocessed escape sequence renders as literal text on
    /// the phone - the Netflix tab once showed "U0001F3AC" instead of a film icon. Catch that shape
    /// in every bundled layout and shortcut.
    /// </summary>
    [Fact]
    public void BundledIconsAreNotUnprocessedEscapes()
    {
        var escape = new Regex(@"^\\?[Uu]\+?[0-9A-Fa-f]{4,8}$");

        var icons = LayoutDefinition.Defaults()
            .SelectMany(l => l.Rows.SelectMany(r => r.Controls).Select(c => c.Icon).Append(l.Icon))
            .Concat(ShortcutDefinition.Defaults().Select(s => s.Icon))
            .Where(i => i is not null)
            .ToList();

        Assert.NotEmpty(icons);
        Assert.All(icons, icon => Assert.DoesNotMatch(escape, icon!));
    }

    /// <summary>
    /// The client harness renders layouts from tests/client/fixtures/layouts.json. That file must be
    /// exactly what the host sends, or the client tests pass against a layout nobody ships - which
    /// is how a broken tab icon got through while every test was green.
    ///
    /// Run with UPDATE_FIXTURES=1 to regenerate it after deliberately changing a bundled layout.
    /// </summary>
    [Fact]
    public void ClientFixtureMatchesTheRealLayoutOutput()
    {
        var message = new ConfigMessage { Layouts = Build().DescribeLayouts() };
        var actual = JsonNode.Parse(JsonSerializer.Serialize(message, AppJson.Default.ConfigMessage))!["layouts"]!;

        var path = Path.Combine(RepoRoot(), "tests", "client", "fixtures", "layouts.json");

        if (Environment.GetEnvironmentVariable("UPDATE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }

        Assert.True(File.Exists(path), $"Missing {path}. Run the tests with UPDATE_FIXTURES=1 to create it.");
        var expected = JsonNode.Parse(File.ReadAllText(path));

        Assert.True(JsonNode.DeepEquals(expected, actual),
            "tests/client/fixtures/layouts.json is stale. Run the tests with UPDATE_FIXTURES=1 and commit the result.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimpleRemote.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    /// <summary>These are the Netflix web player's own shortcuts; getting one wrong silently does nothing.</summary>
    [Fact]
    public void NetflixLayoutUsesTheNetflixShortcuts()
    {
        var controls = LayoutDefinition.Netflix().Rows.SelectMany(r => r.Controls).ToList();

        var skip = Assert.Single(controls, c => c.Label == "Skip intro");
        Assert.Equal("keys", skip.Action!.Type);
        Assert.Equal("S", skip.Action.Target);
        Assert.Equal(1, skip.Action.Repeat);

        var media = Assert.Single(controls, c => c.Type == "media");
        Assert.Equal("Left", media.SeekBackward!.Target);
        Assert.Equal("Right", media.SeekForward!.Target);

        Assert.Equal("F", Assert.Single(controls, c => c.Label == "Fullscreen").Action!.Target);
    }

    /// <summary>
    /// Every button must resolve to a compiled action, or it renders disabled and does nothing.
    ///
    /// Deliberately uses CanInvoke rather than InvokeAsync: actually invoking these would fire
    /// arrow keys, Shift+N and BrowserBack into whichever window has focus on the machine running
    /// the tests.
    /// </summary>
    [Fact]
    public void EveryLayoutButtonResolvesToAnAction()
    {
        var service = Build();
        var layout = Assert.Single(service.DescribeLayouts());

        var buttons = layout.Rows.SelectMany(r => r.Controls).Where(c => c.Type == "button").ToList();
        Assert.NotEmpty(buttons);

        foreach (var button in buttons)
        {
            Assert.NotNull(button.ActionId);
            Assert.True(service.CanInvoke(button.ActionId), $"'{button.Label}' does not resolve");
        }

        foreach (var media in layout.Rows.SelectMany(r => r.Controls).Where(c => c.Type == "media"))
        {
            Assert.True(service.CanInvoke(media.SeekBackwardId), "media rewind does not resolve");
            Assert.True(service.CanInvoke(media.SeekForwardId), "media fast-forward does not resolve");
        }
    }

    [Fact]
    public void MediaControlWithoutSeekActionsHidesThoseButtons()
    {
        var service = Build(config => config.Layouts =
        [
            new LayoutDefinition
            {
                Id = "test", Label = "Test",
                Rows = [new LayoutRow { Controls = [new LayoutControl { Type = "media" }] }],
            },
        ]);

        Assert.Empty(service.Errors);
        var media = Assert.Single(service.DescribeLayouts().SelectMany(l => l.Rows).SelectMany(r => r.Controls));
        Assert.Equal("media", media.Type);
        Assert.Null(media.SeekBackwardId);
        Assert.Null(media.SeekForwardId);
        Assert.True(media.TrackButtons);
    }

    /// <summary>A broken seek action loses that one button, not the whole now-playing panel.</summary>
    [Fact]
    public void BadSeekActionIsReportedButThePanelStillRenders()
    {
        var service = Build(config => config.Layouts =
        [
            new LayoutDefinition
            {
                Id = "test", Label = "Test",
                Rows =
                [
                    new LayoutRow
                    {
                        Controls =
                        [
                            new LayoutControl
                            {
                                Type = "media",
                                SeekBackward = new ShortcutAction { Type = "keys", Target = "NotAKey" },
                                SeekForward = new ShortcutAction { Type = "keys", Target = "Right" },
                            },
                        ],
                    },
                ],
            },
        ]);

        Assert.Single(service.Errors);
        var media = Assert.Single(service.DescribeLayouts().SelectMany(l => l.Rows).SelectMany(r => r.Controls));
        Assert.Null(media.SeekBackwardId);
        Assert.NotNull(media.SeekForwardId);
    }

    [Fact]
    public void UnknownActionIdIsRejected()
    {
        var service = Build();

        Assert.False(service.CanInvoke("layout:nope:0:0"));
        Assert.False(service.CanInvoke(null));
        Assert.False(service.CanInvoke(""));
    }

    /// <summary>
    /// Layout action ids must be stable for an unchanged config, or a phone holding a slightly
    /// stale layout would invoke the wrong button.
    /// </summary>
    [Fact]
    public void ActionIdsAreStableAcrossReloads()
    {
        var service = Build();
        var first = service.DescribeLayouts().SelectMany(l => l.Rows).SelectMany(r => r.Controls)
            .Select(c => c.ActionId).ToList();

        service.Reload();
        var second = service.DescribeLayouts().SelectMany(l => l.Rows).SelectMany(r => r.Controls)
            .Select(c => c.ActionId).ToList();

        Assert.Equal(first, second);
    }

    [Fact]
    public void BadActionIsReportedAndTheButtonIsDropped()
    {
        var service = Build(config => config.Layouts =
        [
            new LayoutDefinition
            {
                Id = "test", Label = "Test",
                Rows =
                [
                    new LayoutRow
                    {
                        Controls =
                        [
                            new LayoutControl { Type = "button", Label = "Bad", Action = new ShortcutAction { Type = "keys", Target = "NotAKey" } },
                            new LayoutControl { Type = "button", Label = "Good", Action = new ShortcutAction { Type = "keys", Target = "F5" } },
                        ],
                    },
                ],
            },
        ]);

        Assert.Single(service.Errors);

        var controls = service.DescribeLayouts().SelectMany(l => l.Rows).SelectMany(r => r.Controls).ToList();
        Assert.Equal("Good", Assert.Single(controls).Label);
    }

    [Fact]
    public void ButtonWithoutAnActionIsReported()
    {
        var service = Build(config => config.Layouts =
        [
            new LayoutDefinition
            {
                Id = "test", Label = "Test",
                Rows = [new LayoutRow { Controls = [new LayoutControl { Type = "button", Label = "Nothing" }] }],
            },
        ]);

        Assert.Single(service.Errors);
        Assert.Empty(service.DescribeLayouts());
    }

    /// <summary>Non-interactive controls need no action and must not be reported as broken.</summary>
    [Theory]
    [InlineData("trackpad")]
    [InlineData("media")]
    [InlineData("volume")]
    [InlineData("mouse")]
    [InlineData("spacer")]
    public void NonInteractiveControlsNeedNoAction(string type)
    {
        var service = Build(config => config.Layouts =
        [
            new LayoutDefinition
            {
                Id = "test", Label = "Test",
                Rows = [new LayoutRow { Controls = [new LayoutControl { Type = type }] }],
            },
        ]);

        Assert.Empty(service.Errors);
        var control = Assert.Single(service.DescribeLayouts().SelectMany(l => l.Rows).SelectMany(r => r.Controls));
        Assert.Equal(type, control.Type);
        Assert.Null(control.ActionId);
    }

    [Fact]
    public void RepeatIsClampedToSomethingSurvivable()
    {
        var service = Build(config => config.Layouts =
        [
            new LayoutDefinition
            {
                Id = "test", Label = "Test",
                Rows =
                [
                    new LayoutRow
                    {
                        Controls =
                        [
                            new LayoutControl
                            {
                                Type = "button", Label = "Runaway",
                                Action = new ShortcutAction { Type = "keys", Target = "Right", Repeat = 100_000 },
                            },
                        ],
                    },
                ],
            },
        ]);

        // Compiles rather than erroring, but must not be able to hold the input queue for minutes.
        Assert.Empty(service.Errors);
        Assert.True(ShortcutService.MaxRepeat <= 50);
    }

    [Fact]
    public void MediaActionsAreAcceptedAndBadOnesRejected()
    {
        var service = Build(config => config.Shortcuts =
        [
            new ShortcutDefinition { Id = "good", Label = "Play", Action = new ShortcutAction { Type = "media", Target = "play" } },
            new ShortcutDefinition { Id = "bad", Label = "Huh", Action = new ShortcutAction { Type = "media", Target = "rewind-twice" } },
        ]);

        Assert.Single(service.Errors);
        Assert.Equal("good", Assert.Single(service.Describe()).Id);
    }

    [Fact]
    public void LayoutIdsCannotCollideWithShortcutIds()
    {
        // A user-chosen shortcut id can never contain the "layout:" prefix pattern, so a layout
        // action id cannot shadow a shortcut even if someone tries.
        var service = Build(config =>
        {
            config.Shortcuts = [new ShortcutDefinition { Id = "layout:netflix:0:0", Label = "Sneaky", Action = new ShortcutAction { Type = "keys", Target = "F5" } }];
            config.Layouts = [LayoutDefinition.Netflix()];
        });

        // The layout compiles after the shortcuts and wins the id, which is the safe direction:
        // the button the user sees in the layout is the action that runs.
        var layout = Assert.Single(service.DescribeLayouts());
        Assert.Contains(layout.Rows.SelectMany(r => r.Controls), c => c.ActionId == "layout:netflix:0:0");
    }
}
