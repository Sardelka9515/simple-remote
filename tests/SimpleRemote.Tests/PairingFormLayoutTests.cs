using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using SimpleRemote.Clipboard;
using SimpleRemote.Config;
using SimpleRemote.Input;
using SimpleRemote.Media;
using SimpleRemote.Pairing;
using SimpleRemote.Server;
using SimpleRemote.Shortcuts;
using SimpleRemote.Tray;
using Xunit;
using Xunit.Abstractions;

namespace SimpleRemote.Tests;

public class PairingFormLayoutTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _devicesPath = Path.Combine(Path.GetTempPath(), $"sr-test-dev-{Guid.NewGuid():N}.json");

    private PairingForm CreateForm(out RemoteServer server)
    {
        var config = new ConfigStore();
        var devices = new DeviceStore(_devicesPath);
        var pairing = new PairingTokenSource();
        var injector = new InputInjector();
        var media = new MediaController(injector);
        var volume = new VolumeController();
        var clipboard = new ClipboardService();
        var shortcuts = new ShortcutService(config, injector);
        server = new RemoteServer(config, devices, pairing, injector, media, volume, clipboard, shortcuts);
        var host = new WebHost(server);
        return new PairingForm(server, host);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void ButtonsAreNotClippedAtAnyDpi(int targetDpi)
    {
        using var form = CreateForm(out var server);
        using (server)
        {
            var _ = form.Handle;

            if (targetDpi != 96)
            {
                var factor = (float)targetDpi / 96f;
                form.Scale(new SizeF(factor, factor));
            }

            output.WriteLine($"--- Target DPI: {targetDpi} ---");
            output.WriteLine($"Form ClientSize: {form.ClientSize}");

            var root = form.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
            Assert.NotNull(root);

            var buttonsPanel = root.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
            Assert.NotNull(buttonsPanel);
            output.WriteLine($"ButtonsPanel size: {buttonsPanel.Size}");

            var buttons = buttonsPanel.Controls.OfType<Button>().ToList();
            Assert.Equal(5, buttons.Count);

            foreach (var b in buttons)
            {
                output.WriteLine($"Button '{b.Text}' bounds: {b.Bounds}");
                Assert.True(b.Bottom <= buttonsPanel.ClientSize.Height,
                    $"Button '{b.Text}' bottom {b.Bottom} exceeds panel height {buttonsPanel.ClientSize.Height}");
                Assert.True(b.Right <= buttonsPanel.ClientSize.Width,
                    $"Button '{b.Text}' right {b.Right} exceeds panel width {buttonsPanel.ClientSize.Width}");
            }

            // At default initial window size, all 5 buttons should fit on a single line (same Y)
            var firstTop = buttons[0].Top;
            foreach (var b in buttons)
            {
                Assert.Equal(firstTop, b.Top);
            }
        }
    }

    [Fact]
    public void ButtonsDoNotClipWhenWindowIsNarrow()
    {
        using var form = CreateForm(out var server);
        using (server)
        {
            var _ = form.Handle;

            // Make the form narrow enough that buttons must wrap to 2 or 3 lines
            form.ClientSize = new Size(350, 600);

            var root = form.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
            Assert.NotNull(root);

            var buttonsPanel = root.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
            Assert.NotNull(buttonsPanel);

            var buttons = buttonsPanel.Controls.OfType<Button>().ToList();
            Assert.Equal(5, buttons.Count);

            // Verify that even when wrapped across multiple lines, panel expands and no button is clipped
            foreach (var b in buttons)
            {
                Assert.True(b.Bottom <= buttonsPanel.ClientSize.Height,
                    $"Button '{b.Text}' bottom {b.Bottom} exceeds panel height {buttonsPanel.ClientSize.Height}");
            }
        }
    }

    [Fact]
    public void ListViewColumnsFillAvailableWidth()
    {
        using var form = CreateForm(out var server);
        using (server)
        {
            var _ = form.Handle;

            var root = form.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
            Assert.NotNull(root);

            var listView = root.Controls.OfType<ListView>().FirstOrDefault();
            Assert.NotNull(listView);

            Assert.Equal(3, listView.Columns.Count);
            var totalColumnWidth = listView.Columns[0].Width + listView.Columns[1].Width + listView.Columns[2].Width;
            output.WriteLine($"ListView client width: {listView.ClientSize.Width}, total columns width: {totalColumnWidth}");

            // The columns should span roughly the entire client width without large gap
            Assert.True(totalColumnWidth >= listView.ClientSize.Width - 10,
                $"Columns width {totalColumnWidth} leaves large unused space in ListView width {listView.ClientSize.Width}");
        }
    }

    [Fact]
    public void StatusLabelHasAutoEllipsis()
    {
        using var form = CreateForm(out var server);
        using (server)
        {
            var _ = form.Handle;

            var root = form.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
            Assert.NotNull(root);

            var status = root.Controls.OfType<Label>().FirstOrDefault(l => l.Text.Contains("Listening"));
            Assert.NotNull(status);
            Assert.True(status.AutoEllipsis);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_devicesPath)) File.Delete(_devicesPath);
    }
}
