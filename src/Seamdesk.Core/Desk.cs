namespace Seamdesk.Core;

public readonly record struct Point(int X, int Y)
{
    public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);
    public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
}

/// <summary>A monitor in physical pixels. Right and Bottom are exclusive, like Windows' RECT.</summary>
public readonly record struct Rect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;

    public bool Contains(Point p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

    public Point Clamp(Point p) => new(Math.Clamp(p.X, Left, Right - 1), Math.Clamp(p.Y, Top, Bottom - 1));
}

public enum Direction { Left, Right, Up, Down }

/// <summary>
/// A machine's monitors in its own desktop coordinates, and where its desktop origin sits on the shared desk plane.
/// </summary>
public sealed record Machine(string Name, Point Offset, IReadOnlyList<Rect> Monitors)
{
    public bool Contains(Point local) => Monitors.Any(m => m.Contains(local));

    /// <summary>The nearest pixel on any monitor, so a delta that lands in a dead zone stops at the closest edge.</summary>
    public Point Clamp(Point local) => Monitors.Select(m => m.Clamp(local)).MinBy(c => DistanceSquared(c, local));

    private static long DistanceSquared(Point a, Point b) => (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y);
}

/// <summary>The cursor: which machine owns it and where, in that machine's pixel coordinates.</summary>
public readonly record struct Location(Machine Machine, Point Point);

/// <summary>All paired machines placed on one desk plane, where one desk unit is one pixel of whichever monitor covers it.</summary>
public sealed class Desk(IReadOnlyList<Machine> machines)
{
    public IReadOnlyList<Machine> Machines { get; } = machines;

    /// <summary>
    /// Leaves <paramref name="from"/> at <paramref name="local"/> towards <paramref name="direction"/>. Returns the monitor
    /// of another machine that owns the next desk pixel, at that same desk pixel, or null when the next pixel is still
    /// on <paramref name="from"/> (not an outer edge) or nothing is there (the cursor stays).
    /// </summary>
    public Location? Cross(Machine from, Point local, Direction direction)
    {
        Point next = local + Step(direction);
        if (from.Contains(next))
        {
            return null;
        }

        Point desk = next + from.Offset;
        foreach (Machine other in Machines)
        {
            if (!ReferenceEquals(other, from) && other.Contains(desk - other.Offset))
            {
                return new Location(other, desk - other.Offset);
            }
        }

        return null;
    }

    /// <summary>
    /// Applies a pixel delta 1:1 on the machine that owns the cursor. A delta that leaves every local monitor is clamped
    /// to the nearest one, then crosses to another machine if one touches that edge on the desk.
    /// </summary>
    public Location Move(Location cursor, int dx, int dy)
    {
        Point target = cursor.Point + new Point(dx, dy);
        if (cursor.Machine.Contains(target))
        {
            return cursor with { Point = target };
        }

        Point clamped = cursor.Machine.Clamp(target);
        int overX = target.X - clamped.X;
        int overY = target.Y - clamped.Y;

        // Known limit: the overshoot past the edge is dropped on arrival; carry it over if fast flicks feel sticky.
        Direction towardX = overX < 0 ? Direction.Left : Direction.Right;
        if (overX != 0 && Cross(cursor.Machine, clamped, towardX) is { } acrossX)
        {
            return Inside(acrossX, towardX);
        }

        Direction towardY = overY < 0 ? Direction.Up : Direction.Down;
        if (overY != 0 && Cross(cursor.Machine, clamped, towardY) is { } acrossY)
        {
            return Inside(acrossY, towardY);
        }

        return cursor with { Point = clamped };
    }

    /// <summary>
    /// A crossing arrives this many pixels past the edge pixel, so a hand sliding along the shared edge with a pixel of
    /// jitter doesn't flip the cursor back and forth: it has to move back at least that far to return. MWB arrives
    /// JUMP_PIXELS (2) inside and leaves from the edge pixel (Core/MachineStuff.cs:33-34, :241-256, :392-669).
    /// </summary>
    private const int ArrivalInset = 2;

    /// <summary><paramref name="at"/> moved <see cref="ArrivalInset"/> pixels on towards <paramref name="d"/>, as far as its monitor reaches.</summary>
    private static Location Inside(Location at, Direction d)
    {
        for (int i = 0; i < ArrivalInset && at.Machine.Contains(at.Point + Step(d)); i++)
        {
            at = at with { Point = at.Point + Step(d) };
        }

        return at;
    }

    private static Point Step(Direction d) => d switch
    {
        Direction.Left => new(-1, 0),
        Direction.Right => new(1, 0),
        Direction.Up => new(0, -1),
        _ => new(0, 1),
    };
}
