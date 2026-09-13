using System.Drawing;
using System.Drawing.Drawing2D;

namespace SimpleRemote.Tray;

/// <summary>
/// Draws the tray icon at runtime.
///
/// Generating it beats shipping an .ico file: it scales correctly for whatever DPI the tray asks
/// for, and it lets the icon carry state - connected devices are shown as a filled dot, so the
/// user can tell at a glance whether a phone is attached.
/// </summary>
public static partial class AppIcon
{
    public static Icon Create(bool connected, int size = 32)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var scale = size / 32f;

            // Remote body: a rounded upright rectangle.
            var body = new RectangleF(9 * scale, 3 * scale, 14 * scale, 26 * scale);
            using var bodyBrush = new SolidBrush(connected
                ? Color.FromArgb(255, 64, 158, 255)
                : Color.FromArgb(255, 150, 150, 158));

            using var path = RoundedRect(body, 4 * scale);
            g.FillPath(bodyBrush, path);

            // Screen and buttons, drawn as knockouts so the glyph reads at 16px.
            using var knockout = new SolidBrush(Color.FromArgb(255, 26, 28, 32));
            g.FillRectangle(knockout, 11.5f * scale, 5.5f * scale, 9 * scale, 5 * scale);

            for (var row = 0; row < 3; row++)
            for (var col = 0; col < 2; col++)
            {
                g.FillEllipse(knockout,
                    (12.5f + col * 5f) * scale,
                    (13.5f + row * 4.5f) * scale,
                    2.5f * scale, 2.5f * scale);
            }
        }

        // FromHandle does not own the handle, so clone into a managed icon and release it.
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }

    private static partial class NativeMethods
    {
        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static partial bool DestroyIcon(nint handle);
    }
}
