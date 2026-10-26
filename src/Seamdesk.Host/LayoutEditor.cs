using Seamdesk.Core;

namespace Seamdesk.Host;

/// <summary>
/// Where two machines' monitors touch on the desk: the cursor leaving <see cref="From"/>'s monitor through
/// <see cref="Edge"/> arrives on <see cref="To"/>'s monitor, along <see cref="Length"/> desk pixels starting at
/// <see cref="Start"/> (an x for a top or bottom edge, a y for a left or right one). Monitors are indexes into each
/// machine's list. The cursor crosses the same stretch the other way too.
/// </summary>
public sealed record Touch(string From, int FromMonitor, Direction Edge, string To, int ToMonitor, int Start, int Length);

/// <summary>
/// The layout editor's state, without any UI: every machine with its monitors as Windows arranges them, and the offset
/// the user is placing it at. Whole machines move; a machine's own monitors never move relative to each other.
/// </summary>
public sealed class LayoutEditor
{
    private List<Machine> saved;

    public LayoutEditor(IEnumerable<Machine> machines)
    {
        saved = [.. machines];
        Machines = saved;
    }

    public IReadOnlyList<Machine> Machines { get; private set; }

    public bool IsDirty => !Machines.Select(m => m.Offset).SequenceEqual(saved.Select(m => m.Offset));

    /// <summary>Why the layout can't be saved (monitors of two machines overlap), or null.</summary>
    public string? Problem => Overlap(Machines) is var (a, b) ? $"{a} and {b} overlap. Move one so their screens only touch." : null;

    /// <summary>Every place the cursor can cross between machines, once per touching pair, as <see cref="Desk.Cross"/> decides.</summary>
    public IReadOnlyList<Touch> Touches => FindTouches(new Desk(Machines));

    /// <summary>
    /// Drags <paramref name="name"/> to <paramref name="offset"/>, snapping to another machine's monitor edge within
    /// <paramref name="snap"/> desk pixels.
    /// </summary>
    public void MoveTo(string name, Point offset, int snap) => Set(name, Snap(Machines, name, offset, snap));

    /// <summary>Arrow keys: a plain step, no snapping.</summary>
    public void Nudge(string name, int dx, int dy) => Set(name, Machines.First(m => m.Name == name).Offset + new Point(dx, dy));

    /// <summary>An MWB peer's screens as the user enters them (Seamdesk machines report their own).</summary>
    public void SetMonitors(string name, IReadOnlyList<Rect> monitors) =>
        Machines = [.. Machines.Select(m => m.Name == name ? m with { Monitors = monitors } : m)];

    public void Revert() => Machines = saved;

    /// <summary>After the host applied the layout: it is the new saved state.</summary>
    public void Saved() => saved = [.. Machines];

    /// <summary>A machine's monitors on the desk.</summary>
    public static IEnumerable<Rect> OnDesk(Machine m) => m.Monitors.Select(r => r with { Left = r.Left + m.Offset.X, Top = r.Top + m.Offset.Y });

    /// <summary>The box around every monitor on the desk.</summary>
    public static Rect Bounds(IEnumerable<Machine> machines)
    {
        List<Rect> all = [.. machines.SelectMany(OnDesk)];
        int left = all.Min(r => r.Left), top = all.Min(r => r.Top);
        return new Rect(left, top, all.Max(r => r.Right) - left, all.Max(r => r.Bottom) - top);
    }

    /// <summary>
    /// The offset nearest <paramref name="offset"/> that puts one of the machine's monitor edges on another machine's
    /// edge (side by side, or aligned), on each axis separately, if one is within <paramref name="snap"/> pixels. Only
    /// monitors level with each other on the other axis count, so a monitor far above doesn't pull a side.
    /// </summary>
    public static Point Snap(IReadOnlyList<Machine> machines, string name, Point offset, int snap)
    {
        Machine moving = machines.First(m => m.Name == name);
        List<Rect> mine = [.. OnDesk(moving with { Offset = offset })];
        List<Rect> theirs = [.. machines.Where(m => m.Name != name).SelectMany(OnDesk)];

        int dx = Nearest(snap,
            from a in mine
            from b in theirs
            where a.Top < b.Bottom + snap && b.Top < a.Bottom + snap
            from d in new[] { b.Right - a.Left, b.Left - a.Right, b.Left - a.Left, b.Right - a.Right }
            select d);
        int dy = Nearest(snap,
            from a in mine
            from b in theirs
            where a.Left < b.Right + snap && b.Left < a.Right + snap
            from d in new[] { b.Bottom - a.Top, b.Top - a.Bottom, b.Top - a.Top, b.Bottom - a.Bottom }
            select d);
        return offset + new Point(dx, dy);
    }

    /// <summary>The first two machines whose monitors share a desk pixel, or null.</summary>
    public static (string, string)? Overlap(IReadOnlyList<Machine> machines)
    {
        for (int i = 0; i < machines.Count; i++)
        {
            for (int j = i + 1; j < machines.Count; j++)
            {
                if (OnDesk(machines[i]).Any(a => OnDesk(machines[j]).Any(b => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom)))
                {
                    return (machines[i].Name, machines[j].Name);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Walks every outer edge pixel of every monitor and asks <see cref="Desk.Cross"/> where the cursor would go, so the
    /// preview is exactly what the cursor does. Runs of pixels landing on the same monitor become one touch; each pair
    /// is reported from the machine listed first.
    /// </summary>
    public static IReadOnlyList<Touch> FindTouches(Desk desk)
    {
        List<Touch> touches = [];
        for (int i = 0; i < desk.Machines.Count; i++)
        {
            Machine from = desk.Machines[i];
            for (int mi = 0; mi < from.Monitors.Count; mi++)
            {
                Rect r = from.Monitors[mi];
                foreach (Direction edge in Enum.GetValues<Direction>())
                {
                    bool horizontal = edge is Direction.Up or Direction.Down;
                    int length = horizontal ? r.Width : r.Height;
                    Touch? run = null;
                    for (int k = 0; k <= length; k++)
                    {
                        Touch? here = null;
                        if (k < length)
                        {
                            Point p = edge switch
                            {
                                Direction.Up => new(r.Left + k, r.Top),
                                Direction.Down => new(r.Left + k, r.Bottom - 1),
                                Direction.Left => new(r.Left, r.Top + k),
                                _ => new(r.Right - 1, r.Top + k),
                            };
                            if (desk.Cross(from, p, edge) is { } to && IndexOf(desk.Machines, to.Machine) > i)
                            {
                                int along = (horizontal ? p.X + from.Offset.X : p.Y + from.Offset.Y);
                                here = new Touch(from.Name, mi, edge, to.Machine.Name, MonitorAt(to), along, 1);
                            }
                        }

                        if (run is not null && (here is null || here.To != run.To || here.ToMonitor != run.ToMonitor))
                        {
                            touches.Add(run);
                            run = null;
                        }

                        if (here is not null)
                        {
                            run = run is null ? here : run with { Length = run.Length + 1 };
                        }
                    }
                }
            }
        }

        return touches;
    }

    private void Set(string name, Point offset) =>
        Machines = [.. Machines.Select(m => m.Name == name ? m with { Offset = offset } : m)];

    private static int Nearest(int snap, IEnumerable<int> deltas) =>
        deltas.Where(d => Math.Abs(d) <= snap).OrderBy(d => Math.Abs(d)).FirstOrDefault();

    private static int IndexOf(IReadOnlyList<Machine> machines, Machine m)
    {
        for (int i = 0; i < machines.Count; i++)
        {
            if (ReferenceEquals(machines[i], m))
            {
                return i;
            }
        }

        return -1;
    }

    private static int MonitorAt(Location at)
    {
        for (int i = 0; i < at.Machine.Monitors.Count; i++)
        {
            if (at.Machine.Monitors[i].Contains(at.Point))
            {
                return i;
            }
        }

        return -1;
    }
}
