using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Seamdesk.App;

/// <summary>
/// Seamdesk's icon, drawn rather than shipped: a blue tile with two screens of a desk, a portrait one and a landscape one
/// set lower, the arrangement Seamdesk exists for.
/// </summary>
internal static class AppIcon
{
    private static readonly Color Tile = Color.FromRgb(0x00, 0x5F, 0xB8);

    public static BitmapSource Image { get; } = Render(64);

    /// <summary>The tray's icon, at the size Windows asks for.</summary>
    public static System.Drawing.Icon Tray(int size)
    {
        PngBitmapEncoder png = new();
        png.Frames.Add(BitmapFrame.Create(Render(size)));
        using MemoryStream stream = new();
        png.Save(stream);
        using System.Drawing.Bitmap bitmap = new(stream);
        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    private static BitmapSource Render(int size)
    {
        DrawingVisual visual = new();
        using (DrawingContext dc = visual.RenderOpen())
        {
            double u = size / 16.0;
            dc.DrawRoundedRectangle(new SolidColorBrush(Tile), null, new Rect(0, 0, size, size), 3 * u, 3 * u);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(3 * u, 3 * u, 4 * u, 8 * u), 0.8 * u, 0.8 * u);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)), null, new Rect(8 * u, 6 * u, 5.5 * u, 4 * u), 0.8 * u, 0.8 * u);
            dc.DrawRectangle(Brushes.White, null, new Rect(3 * u, 12.2 * u, 10.5 * u, 0.9 * u));
        }

        RenderTargetBitmap bitmap = new(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
