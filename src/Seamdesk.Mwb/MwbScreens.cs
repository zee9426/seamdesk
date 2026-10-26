using Seamdesk.Core;

namespace Seamdesk.Mwb;

/// <summary>Where an MWB peer handed the cursor back. <c>To</c> is null when Seamdesk's desk has no neighbour on that edge.</summary>
public sealed record HandBack(Point? To, IReadOnlyList<Rect>? NewLayout, bool Unmatched);

/// <summary>
/// An MWB peer's screens. MWB never sends its monitors: no package carries screen geometry (Core/PackageType.cs). It only
/// maps positions to 0..65535 over its outer box (MachineStuff.cs:181-190). So the user saves the peer's layouts, and
/// the one in use is checked against every hand-back, which reveals one side of the box (<see cref="Fits"/>).
/// </summary>
public sealed class MwbScreens
{
    // MachineStuff.cs:34. The hand-back point sits this far inside the opposite edge, in pixels of the peer's box.
    private const int JumpPixels = 2;

    private readonly IReadOnlyList<IReadOnlyList<Rect>> layouts;
    private volatile int current;

    public MwbScreens(IReadOnlyList<IReadOnlyList<Rect>> layouts)
    {
        if (layouts.Count == 0 || layouts.Any(l => l.Count == 0))
        {
            throw new ArgumentException("an MWB peer needs at least one saved layout with a monitor", nameof(layouts));
        }

        this.layouts = layouts;
    }

    public IReadOnlyList<Rect> Current => layouts[current];

    public static Rect Box(IReadOnlyList<Rect> monitors)
    {
        int left = monitors.Min(m => m.Left), top = monitors.Min(m => m.Top);
        return new Rect(left, top, monitors.Max(m => m.Right) - left, monitors.Max(m => m.Bottom) - top);
    }

    /// <summary>
    /// A pixel of the peer as MWB's absolute coordinates, 0..65535 over its outer box (InputSimulation.cs SendMouse maps
    /// them back), aimed at the pixel's centre.
    /// </summary>
    public (int X, int Y) Universal(Point p)
    {
        Rect box = Box(Current);
        return (Aim(p.X - box.Left, box.Width), Aim(p.Y - box.Top, box.Height));
    }

    /// <summary>
    /// The peer's MWB sent NextMachine with (<paramref name="x"/>, <paramref name="y"/>): where the cursor would land on
    /// the next machine, in 0..65535 over its own box (MachineStuff.cs:203-256 and Move*). One coordinate is the exit
    /// point along the edge; the other is <see cref="JumpPixels"/> inside the opposite edge, which identifies the edge and
    /// the box's size across it. The current layout is tried first, then the other saved ones. The returned point is
    /// where <paramref name="desk"/> (with <paramref name="peer"/> on that layout) puts the cursor on <paramref name="me"/>.
    /// </summary>
    public HandBack HandBack(Desk desk, string me, string peer, int x, int y)
    {
        if (desk.Machines.FirstOrDefault(m => m.Name == peer) is not { } machine)
        {
            return new HandBack(null, null, false);
        }

        int now = current;
        foreach (int i in Enumerable.Range(0, layouts.Count).OrderBy(i => i != now))
        {
            Rect box = Box(layouts[i]);
            Machine there = machine with { Monitors = layouts[i] };
            Desk withLayout = new([.. desk.Machines.Select(m => m.Name == peer ? there : m)]);
            Direction[] edges = [.. Enum.GetValues<Direction>().Where(d => Fits(box, d, x, y))];
            if (edges.Length == 0)
            {
                continue;
            }

            current = i;
            Point? to = edges.Select(d => withLayout.Cross(there, there.Clamp(Exit(box, d, x, y)), d))
                .FirstOrDefault(l => l?.Machine.Name == me)?.Point;
            return new HandBack(to, i == now ? null : layouts[i], false);
        }

        return new HandBack(null, null, true);
    }

    /// <summary>Whether MWB, leaving <paramref name="box"/> through <paramref name="edge"/>, would send this point.</summary>
    public static bool Fits(Rect box, Direction edge, int x, int y) => edge switch
    {
        Direction.Down => y == JumpPixels * 65535 / box.Height,
        Direction.Up => y == (box.Height - JumpPixels) * 65535 / box.Height,
        Direction.Right => x == JumpPixels * 65535 / box.Width,
        _ => x == (box.Width - JumpPixels) * 65535 / box.Width,
    };

    /// <summary>The pixel on the box's edge the cursor left from. Inverts MWB's floor(p * 65535 / size).</summary>
    private static Point Exit(Rect box, Direction edge, int x, int y) => edge switch
    {
        Direction.Down => new(Along(box.Left, box.Width, x), box.Bottom - 1),
        Direction.Up => new(Along(box.Left, box.Width, x), box.Top),
        Direction.Right => new(box.Right - 1, Along(box.Top, box.Height, y)),
        _ => new(box.Left, Along(box.Top, box.Height, y)),
    };

    private static int Along(int start, int size, int universal) => start + (int)((((long)universal * size) + 65534) / 65535);

    private static int Aim(int offset, int size) => (int)((((2L * offset) + 1) * 65535) / (2L * size));
}
