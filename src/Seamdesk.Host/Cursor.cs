using System.Diagnostics;
using Seamdesk.Core;
using Seamdesk.Transport;

namespace Seamdesk.Host;

/// <summary>Something the Windows host must do. Effects come out of <see cref="Cursor.TakeEffects"/> in order.</summary>
public abstract record Effect;

/// <summary>
/// Send a frame to a peer. A move also says how many hook events it sums (<see cref="Events"/>) and when the hook saw the
/// first (<see cref="Since"/>, a <see cref="Stopwatch"/> timestamp), for the MWB timing lines; equality ignores both.
/// </summary>
public sealed record Send(string Peer, Frame Frame) : Effect
{
    public int Events { get; init; }

    public long Since { get; init; }

    public bool Equals(Send? other) => other is not null && Peer == other.Peer && Frame == other.Frame;

    public override int GetHashCode() => HashCode.Combine(Peer, Frame);
}

/// <summary>Inject a button, wheel or key event (a <see cref="PointerButton"/>, <see cref="Wheel"/> or <see cref="Key"/>) here.</summary>
public sealed record Inject(Frame Input) : Effect;

/// <summary>Inject an absolute move to this pixel of this machine.</summary>
public sealed record MoveTo(Point Point) : Effect;

/// <summary>
/// Show the real cursor at this pixel now, outside the hook: a return with the hand still (a hotkey, a dropped link, a
/// lock), where no move is in flight to race and none may come to place it. The hook still places it again at the next
/// mouse event's callback, in case one was in flight after all.
/// </summary>
public sealed record Show(Point At) : Effect;

/// <summary>
/// Where the low-level hook puts the real cursor, inside the callback that took it (<see cref="Cursor.TakePlace"/>):
/// hidden over the blank window (parked) or shown. The callback swallows a move, so Windows drops that move's point and
/// computes the next from here. Moved from anywhere else, an event Windows already computed from the old position would
/// land after it.
/// </summary>
public readonly record struct Place(Point At, bool Hidden);

public enum Mode
{
    /// <summary>The cursor is on this machine and nobody else drives it. Local input passes through.</summary>
    Local,

    /// <summary>This machine's mouse and keyboard drive another machine (the holder). Local input is swallowed and forwarded.</summary>
    Driving,

    /// <summary>Another machine (the controller) drives the cursor on this machine.</summary>
    Held,
}

/// <summary>
/// Who has the cursor, from this machine's side. Fed by the input hooks (physical input only, never Seamdesk's own
/// injections), by frames from peers and by link, lock and hotkey events. Every method is thread-safe and quick, so the
/// hook thread can call it inside a low-level hook. What the host must do comes out of <see cref="TakeEffects"/>.
/// </summary>
public sealed class Cursor(string me, Desk desk, Point park, Func<bool>? paused = null)
{
    private readonly Lock gate = new();
    private readonly Queue<Effect> effects = new();
    private readonly List<string> notes = [];
    private readonly MoveCoalescer moves = new();
    private readonly HashSet<string> up = [];
    private readonly HashSet<string> locked = [];
    private readonly HashSet<MouseButton> buttonsDown = [];
    private readonly HashSet<Key> injectedKeys = [];
    private readonly HashSet<MouseButton> injectedButtons = [];
    private Desk desk = desk;
    private Point park = park;
    private string? peer;
    private Point last;   // Local, Held: where the real cursor is
    private Point anchor; // Driving: where the parked cursor really sits, so a hook point minus it is the delta
    private Point exit;   // Driving: where the cursor left this machine
    private Point tracked; // Held: the cursor's position as moved by the controller
    private int moveEvents; // Driving: hook events in the pending move
    private long moveSince; // Driving: when the hook saw the first of them
    private Place? place; // for the hook to do in this callback, or (a return) in the next mouse event's
    private string? placed; // the last placement, until the next hook point is logged
    private bool stay;
    private bool secure;

    /// <summary>Raised (inside the lock: keep it cheap) whenever there is something for <see cref="TakeEffects"/>.</summary>
    public event Action? EffectsReady;

    public Mode Mode { get; private set; }

    /// <summary>The holder while Driving, the controller while Held.</summary>
    public string? Peer
    {
        get
        {
            lock (gate)
            {
                return peer;
            }
        }
    }

    private Machine Me => desk.Machines.First(m => m.Name == me);

    public void SetDesk(Desk newDesk, Point newPark)
    {
        lock (gate)
        {
            (desk, park) = (newDesk, newPark);
            last = Me.Clamp(last);
            tracked = Me.Clamp(tracked);
        }
    }

    /// <summary>Where the real cursor is, from GetCursorPos (at start, after a display change).</summary>
    public void Resync(Point at)
    {
        lock (gate)
        {
            last = at;
        }
    }

    /// <summary>A physical move to <paramref name="pt"/> (the hook's point). True: swallow it.</summary>
    public bool OnMove(Point pt)
    {
        lock (gate)
        {
            if (placed is not null)
            {
                Note($"the first hook point after it {placed} is ({pt.X}, {pt.Y})");
                placed = null;
            }

            if (place is not null)
            {
                return true; // the real cursor is still parked, so this point is the park's plus the motion: placed instead
            }

            if (Mode == Mode.Driving)
            {
                moves.Add(pt.X - anchor.X, pt.Y - anchor.Y);
                if (moveEvents++ == 0)
                {
                    moveSince = Stopwatch.GetTimestamp();
                }

                EffectsReady?.Invoke();
                return true;
            }

            Point from = last;
            last = Me.Contains(pt) ? pt : Me.Clamp(pt);
            if (Mode == Mode.Held)
            {
                tracked = last; // someone is using this machine's own mouse: follow it
            }

            if (stay || buttonsDown.Count > 0 || Crossing(from, pt) is not { } to || (paused?.Invoke() ?? false))
            {
                return false;
            }

            if (Mode == Mode.Held)
            {
                // The controller routes it (and releases its own parked cursor if it is the target).
                Enqueue(new Send(peer!, new Handoff(to.Machine.Name, to.Point)));
                LeaveHeld();
            }
            else if (up.Contains(to.Machine.Name) && !locked.Contains(to.Machine.Name))
            {
                Enqueue(new Send(to.Machine.Name, new Handoff(to.Machine.Name, to.Point)));
                (Mode, peer, exit, anchor, place) = (Mode.Driving, to.Machine.Name, from, park, new Place(park, Hidden: true));
                Note($"crossed to {peer} through this machine's {Edge(last, to)} edge at ({last.X}, {last.Y}), entering at ({to.Point.X}, {to.Point.Y})");
            }
            else
            {
                return false; // crossing into a peer that is down or locked does nothing
            }

            last = from; // the swallowed event leaves the real cursor where it was
            return true;
        }
    }

    public bool OnButton(MouseButton button, bool down)
    {
        lock (gate)
        {
            if (Forward(new PointerButton(button, down)))
            {
                return true;
            }

            _ = down ? buttonsDown.Add(button) : buttonsDown.Remove(button);
            return false;
        }
    }

    public bool OnWheel(int delta, bool horizontal)
    {
        lock (gate)
        {
            return Forward(new Wheel(delta, horizontal));
        }
    }

    public bool OnKey(Key key)
    {
        lock (gate)
        {
            return Forward(key);
        }
    }

    /// <summary>
    /// For the low-level hook, after it has handed over a mouse event: where to put the real cursor before it returns, or
    /// null. Crossing parks it in the crossing's callback; a return (from any thread) shows it at the return point in
    /// the next mouse event's.
    /// </summary>
    public Place? TakePlace()
    {
        lock (gate)
        {
            Place? p = place;
            if (p is { } now)
            {
                (place, placed) = (null, now.Hidden ? "parked" : "came back");
            }

            return p;
        }
    }

    /// <summary>"Snap back to this machine": the cursor comes home, whoever has it. <paramref name="why"/> goes in the note.</summary>
    public void SnapBack(string why = "the snap-back hotkey")
    {
        lock (gate)
        {
            if (Mode == Mode.Driving)
            {
                Enqueue(new Send(peer!, new Handoff(me, exit)));
                LeaveDriving(exit, why);
            }
            else if (Mode == Mode.Held)
            {
                // A desk point on this machine is never on the controller's monitors, so it releases where it left.
                Enqueue(new Send(peer!, new Handoff(peer!, OnDeskOf(peer!, tracked))));
                LeaveHeld();
            }
        }
    }

    /// <summary>"Stay on this machine": while on, the cursor does not leave; turning it on snaps the cursor back first.</summary>
    public bool ToggleStay()
    {
        lock (gate)
        {
            stay = !stay;
            if (stay)
            {
                SnapBack("the stay hotkey");
            }

            return stay;
        }
    }

    public void LinkUp(string name)
    {
        lock (gate)
        {
            up.Add(name);
        }
    }

    public void LinkDown(string name)
    {
        lock (gate)
        {
            up.Remove(name);
            locked.Remove(name);
            if (name == peer)
            {
                Leave($"the link to {name} dropped");
            }
        }
    }

    /// <summary>This machine went to (or left) the secure desktop: lock screen, UAC, Ctrl+Alt+Del.</summary>
    public void SecureDesktop(bool active)
    {
        lock (gate)
        {
            secure = active;
            foreach (string name in up)
            {
                Enqueue(new Send(name, new Locked(active)));
            }

            if (active && Mode == Mode.Driving)
            {
                Enqueue(new Send(peer!, new Handoff(me, exit)));
            }

            if (active)
            {
                Leave("this machine went to its secure desktop (lock, UAC or Ctrl+Alt+Del)"); // Held: the controller gets the cursor back from our Locked(true)
            }
        }
    }

    public void OnFrame(string from, Frame frame)
    {
        lock (gate)
        {
            bool fromPeer = from == peer;
            switch (frame)
            {
                case Handoff h when h.Machine == me && Mode == Mode.Driving:
                    if (fromPeer)
                    {
                        // The holder hands it back: across the shared edge (the hand moving), or (a point off this machine) by
                        // its own snap-back or backstop.
                        bool edge = Me.Contains(h.Point);
                        LeaveDriving(edge ? h.Point : exit, edge ? "it crossed back at the edge" : $"{from} sent it back where it left", still: !edge);
                    }

                    break;
                case Handoff h when h.Machine == me:
                    if (secure)
                    {
                        Enqueue(new Send(from, new Locked(true)));
                        break;
                    }

                    if (Mode == Mode.Held)
                    {
                        LeaveHeld();
                    }

                    (Mode, peer, tracked) = (Mode.Held, from, Me.Clamp(h.Point));
                    place = place is null ? null : new Place(tracked, Hidden: false); // a return not yet shown: show it there
                    Enqueue(new MoveTo(tracked));
                    break;
                case Handoff h when Mode == Mode.Driving && fromPeer:
                    // The holder's cursor crossed to another machine: deltas go there from now on.
                    if (h.Machine != me && up.Contains(h.Machine) && !locked.Contains(h.Machine))
                    {
                        // Not Enqueue: moves still pending belong after this handoff, on the new holder.
                        effects.Enqueue(new Send(h.Machine, h));
                        Note($"{peer} passed it to {h.Machine}, entering at ({h.Point.X}, {h.Point.Y})");
                        peer = h.Machine;
                    }
                    else
                    {
                        LeaveDriving(exit, $"it left there for {h.Machine}, which is down or locked", still: false);
                    }

                    break;
                case Handoff when Mode == Mode.Held && fromPeer:
                    LeaveHeld(); // the controller took it away
                    break;
                case PointerMove m when Mode == Mode.Held && fromPeer:
                    MoveHeld(m.Dx, m.Dy);
                    break;
                case PointerButton or Wheel or Key when Mode == Mode.Held && fromPeer:
                    Track(frame);
                    Enqueue(new Inject(frame));
                    break;
                case Locked l:
                    if (!l.IsLocked)
                    {
                        locked.Remove(from);
                    }
                    else
                    {
                        locked.Add(from);
                        if (Mode == Mode.Driving && fromPeer)
                        {
                            LeaveDriving(exit, $"{from} locked or stopped reading");
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>A line for the log for each crossing to a peer and each return from one, in order.</summary>
    public IReadOnlyList<string> TakeNotes()
    {
        lock (gate)
        {
            string[] all = [.. notes];
            notes.Clear();
            return all;
        }
    }

    /// <summary>Everything to do, in order. While Driving this includes the moves summed since the last call.</summary>
    public IReadOnlyList<Effect> TakeEffects()
    {
        lock (gate)
        {
            FlushMove();
            Effect[] all = [.. effects];
            effects.Clear();
            return all;
        }
    }

    /// <summary>
    /// Where a physical move from <paramref name="from"/> to the hook's <paramref name="pt"/> leaves this machine, or null.
    /// A point on an outer-edge pixel that moved towards that edge counts as one pixel past it, so a crossing is seen
    /// whether or not Windows clips the hook's point to the desktop.
    /// </summary>
    private Location? Crossing(Point from, Point pt)
    {
        Machine m = Me;
        Point target = pt;
        if (m.Contains(pt))
        {
            target += new Point(Push(m, pt, Math.Sign(pt.X - from.X), 0), Push(m, pt, 0, Math.Sign(pt.Y - from.Y)));
        }

        Location to = desk.Move(new Location(m, from), target.X - from.X, target.Y - from.Y);
        return to.Machine.Name == me ? null : to;
    }

    private static int Push(Machine m, Point pt, int sx, int sy) =>
        (sx | sy) != 0 && !m.Contains(pt + new Point(sx, sy)) ? sx + sy : 0;

    private void MoveHeld(int dx, int dy)
    {
        Location to = desk.Move(new Location(Me, tracked), dx, dy);
        if (to.Machine.Name == me || injectedButtons.Count > 0)
        {
            // A drag never leaves the machine: it stops at the edge.
            tracked = to.Machine.Name == me ? to.Point : Me.Clamp(tracked + new Point(dx, dy));
            Enqueue(new MoveTo(tracked));
            return;
        }

        Enqueue(new Send(peer!, new Handoff(to.Machine.Name, to.Point)));
        LeaveHeld();
    }

    private bool Forward(Frame input)
    {
        if (Mode != Mode.Driving)
        {
            return false;
        }

        Enqueue(new Send(peer!, input)); // after any pending move, so a click lands where the pointer is
        return true;
    }

    private void Track(Frame input)
    {
        switch (input)
        {
            case Key k:
                _ = k.Down ? injectedKeys.Add(k) : injectedKeys.Remove(k with { Down = true });
                break;
            case PointerButton b:
                _ = b.Down ? injectedButtons.Add(b.Button) : injectedButtons.Remove(b.Button);
                break;
        }
    }

    private void Leave(string why)
    {
        if (Mode == Mode.Driving)
        {
            LeaveDriving(exit, why);
        }
        else if (Mode == Mode.Held)
        {
            LeaveHeld();
        }
    }

    /// <summary><paramref name="still"/>: nothing says the hand is moving, so the cursor shows at once, not at the next move.</summary>
    private void LeaveDriving(Point at, string why, bool still = true)
    {
        Note($"back from {peer} at ({at.X}, {at.Y}): {why}");
        FlushMove();
        if (still)
        {
            Enqueue(new Show(at));
        }

        (Mode, peer, last, place, placed) = (Mode.Local, null, at, new Place(at, Hidden: false), null);
    }

    /// <summary>Lets go of every key and button the controller left down, so nothing sticks.</summary>
    private void LeaveHeld()
    {
        foreach (Key k in injectedKeys)
        {
            Enqueue(new Inject(k with { Down = false }));
        }

        foreach (MouseButton b in injectedButtons)
        {
            Enqueue(new Inject(new PointerButton(b, false)));
        }

        injectedKeys.Clear();
        injectedButtons.Clear();
        (Mode, peer, last) = (Mode.Local, null, tracked);
    }

    private Point OnDeskOf(string machine, Point local)
    {
        Machine? other = desk.Machines.FirstOrDefault(m => m.Name == machine);
        return other is null ? local : local + Me.Offset - other.Offset;
    }

    /// <summary>Which edge of this machine a crossing from its edge pixel <paramref name="from"/> to <paramref name="to"/> went through.</summary>
    private string Edge(Point from, Location to)
    {
        Point d = to.Point + to.Machine.Offset - (from + Me.Offset);
        return Math.Abs(d.X) >= Math.Abs(d.Y) ? (d.X < 0 ? "left" : "right") : (d.Y < 0 ? "top" : "bottom");
    }

    private void Note(string line)
    {
        notes.Add(line);
        EffectsReady?.Invoke();
    }

    private void FlushMove()
    {
        if (Mode == Mode.Driving && moves.Take() is { } m)
        {
            effects.Enqueue(new Send(peer!, new PointerMove(m.Dx, m.Dy)) { Events = moveEvents, Since = moveSince });
        }

        moveEvents = 0;
    }

    private void Enqueue(Effect effect)
    {
        FlushMove();
        effects.Enqueue(effect);
        EffectsReady?.Invoke();
    }
}
