using Seamdesk.Core;

namespace Seamdesk.Host;

/// <summary>One monitor as Windows reports it (GetMonitorInfo, physical pixels): edges, right and bottom exclusive.</summary>
public readonly record struct Display(int Left, int Top, int Right, int Bottom, bool Primary)
{
    public Rect Rect => new(Left, Top, Right - Left, Bottom - Top);
}

/// <summary>This machine's monitors, as the desk and the injector see them.</summary>
public sealed record Screens(IReadOnlyList<Display> Monitors)
{
    public IReadOnlyList<Rect> Rects { get; } = [.. Monitors.Select(m => m.Rect)];

    /// <summary>The virtual desktop: the bounding box of every monitor, which MOUSEEVENTF_VIRTUALDESK maps 0..65535 onto.</summary>
    public Rect Virtual { get; } = Bounds(Monitors);

    /// <summary>Where the cursor waits while another machine has it: the centre of the primary monitor, far from any edge.</summary>
    public Point Park
    {
        get
        {
            Rect p = Monitors.FirstOrDefault(m => m.Primary, Monitors[0]).Rect;
            return new(p.Left + (p.Width / 2), p.Top + (p.Height / 2));
        }
    }

    public Machine ToMachine(string name, Point offset) => new(name, offset, Rects);

    /// <summary>
    /// A pixel as normalised absolute coordinates over the virtual desktop. Aims at the pixel's centre, so any mapping
    /// that floors <c>n * width / 65536</c> (or <c>/ 65535</c>) lands on exactly that pixel.
    /// </summary>
    public (int X, int Y) Normalize(Point p) =>
        (Centre(p.X - Virtual.Left, Virtual.Width), Centre(p.Y - Virtual.Top, Virtual.Height));

    private static int Centre(int offset, int size) => (int)((((2L * offset) + 1) * 65536) / (2L * size));

    private static Rect Bounds(IReadOnlyList<Display> ms)
    {
        int left = ms.Min(m => m.Left), top = ms.Min(m => m.Top);
        return new(left, top, ms.Max(m => m.Right) - left, ms.Max(m => m.Bottom) - top);
    }
}
