using Seamdesk.Core;
using Seamdesk.Core.Tests;
using Seamdesk.Host;
using Seamdesk.Transport;

namespace Seamdesk.Host.Tests;

/// <summary>
/// The cursor's ownership rules, seen from "pc" on <see cref="ExampleDesk"/>. Expected points are hand-computed from
/// its geometry: the 1080p's bottom row is y = 1104, the laptop's 1440p starts at desk (1440, 1105), so pc (x, 1104)
/// continues at laptop (x - 1440, 0). The pc parks at the portrait's centre, (720, 1280).
/// </summary>
public class CursorTests
{
    private static readonly Point ParkPoint = new(720, 1280);
    private static readonly Point Exit = new(2000, 1100);
    private static readonly Key CtrlDown = new(0xA2, 0x1D, Down: true, Extended: false);

    public static TheoryData<string, int, int, int, int, int?, int?, string?> LocalMoves => new()
    {
        // case, from x, y, hook point x, y, arrival on the laptop x, y (null: stays here, event passes), the log line
        { "lands on the 1080p's bottom row moving down (a clipped hook point)", 2000, 1100, 2000, 1104, 560, 2, "bottom edge at (2000, 1104), entering at (560, 2)" },
        { "hook point past the bottom edge (an unclipped one)", 2000, 1100, 2000, 1112, 560, 2, "bottom edge at (2000, 1104), entering at (560, 2)" },
        { "a diagonal push onto the bottom row", 1990, 1101, 2000, 1104, 560, 2, "bottom edge at (2000, 1104), entering at (560, 2)" },
        { "one row short of the edge", 2000, 1100, 2000, 1103, null, null, null },
        { "sliding along the bottom row, no downward motion", 2000, 1104, 2005, 1104, null, null, null },
        { "portrait's right edge where the laptop touches it", 1430, 1500, 1439, 1500, 2, 395, "right edge at (1439, 1500), entering at (2, 395)" },
        { "portrait's right edge above the 1080p: nothing there", 1430, 10, 1439, 10, null, null, null },
    };

    [Theory]
    [MemberData(nameof(LocalMoves))]
    public void A_local_move_crosses_only_when_pushing_past_an_outer_edge_onto_a_neighbour(
        string @case, int fx, int fy, int x, int y, int? ax, int? ay, string? logged)
    {
        _ = @case;
        Cursor c = Pc();
        c.Resync(new Point(fx, fy));

        bool swallowed = c.OnMove(new Point(x, y));

        if (ax is null)
        {
            Assert.False(swallowed);
            Assert.Equal(Mode.Local, c.Mode);
            Assert.Empty(c.TakeEffects());
        }
        else
        {
            Assert.True(swallowed);
            Assert.Equal((Mode.Driving, "laptop"), (c.Mode, c.Peer));
            Assert.Equal<Effect>([new Send("laptop", new Handoff("laptop", new Point(ax.Value, ay!.Value)))], c.TakeEffects());
            Assert.Equal(new Place(ParkPoint, Hidden: true), c.TakePlace()); // parked in the crossing's own hook callback
        }

        Assert.Equal(logged is null ? [] : [$"crossed to laptop through this machine's {logged}"], c.TakeNotes());
    }

    [Theory]
    [InlineData("stay on this machine")]
    [InlineData("a local button held (a drag)")]
    [InlineData("auto-pause: fullscreen app focused")]
    [InlineData("laptop not connected")]
    [InlineData("laptop on its secure desktop")]
    public void Crossing_is_blocked_while(string blocker)
    {
        bool paused = blocker.StartsWith("auto-pause");
        Cursor c = new("pc", ExampleDesk.Flush(), ParkPoint, () => paused);
        if (blocker != "laptop not connected")
        {
            c.LinkUp("laptop");
        }

        switch (blocker)
        {
            case "stay on this machine": c.ToggleStay(); break;
            case "a local button held (a drag)": c.OnButton(MouseButton.Left, true); break;
            case "laptop on its secure desktop": c.OnFrame("laptop", new Locked(true)); break;
        }

        c.Resync(Exit);
        Assert.False(c.OnMove(new Point(2000, 1104)));
        Assert.Equal(Mode.Local, c.Mode);
        Assert.Empty(c.TakeEffects());

        // Lift the blocker: the same push now crosses.
        switch (blocker)
        {
            case "stay on this machine": c.ToggleStay(); break;
            case "a local button held (a drag)": c.OnButton(MouseButton.Left, false); break;
            case "auto-pause: fullscreen app focused": paused = false; break;
            case "laptop not connected": c.LinkUp("laptop"); break;
            case "laptop on its secure desktop": c.OnFrame("laptop", new Locked(false)); break;
        }

        c.Resync(Exit);
        Assert.True(c.OnMove(new Point(2000, 1104)));
        Assert.Equal(Mode.Driving, c.Mode);
    }

    [Fact]
    public void Driving_sends_hook_deltas_from_the_park_summed_and_ahead_of_a_click()
    {
        Cursor c = Driving();

        Assert.True(c.OnMove(new Point(723, 1279)));
        Assert.True(c.OnMove(new Point(730, 1275)));
        Assert.True(c.OnButton(MouseButton.Left, true));
        Assert.True(c.OnMove(new Point(721, 1280))); // a swallowed move never moves the parked cursor
        Assert.True(c.OnKey(CtrlDown));

        IReadOnlyList<Effect> effects = c.TakeEffects();
        Assert.Equal<Effect>(
        [
            new Send("laptop", new PointerMove(13, -6)),
            new Send("laptop", new PointerButton(MouseButton.Left, true)),
            new Send("laptop", new PointerMove(1, 0)),
            new Send("laptop", CtrlDown),
        ], effects);
        Assert.Equal([2, 1], effects.OfType<Send>().Where(s => s.Frame is PointerMove).Select(s => s.Events)); // for the MWB timing lines
        Assert.Equal(["the first hook point after it parked is (723, 1279)"], c.TakeNotes());
        Assert.Null(c.TakePlace());
    }

    [Theory]
    [InlineData("the laptop hands it back on our 1080p's bottom row")]
    [InlineData("the laptop hands it back at a point off our monitors")]
    [InlineData("the link to the laptop drops")]
    [InlineData("the laptop goes to its secure desktop")]
    [InlineData("snap-back hotkey")]
    [InlineData("this machine goes to its secure desktop")]
    public void Driving_ends_and_the_cursor_reappears_here(string why)
    {
        Cursor c = Driving();
        Point back = new(2000, 1104);
        (Effect[] expected, Point at, string logged) = why switch
        {
            "the laptop hands it back on our 1080p's bottom row" =>
                (Do(() => c.OnFrame("laptop", new Handoff("pc", back))), back, "(2000, 1104): it crossed back at the edge"),
            "the laptop hands it back at a point off our monitors" =>
                (Do(() => c.OnFrame("laptop", new Handoff("pc", new Point(5000, 5000))), new Show(Exit)), Exit, "(2000, 1100): laptop sent it back where it left"),
            "the link to the laptop drops" => (Do(() => c.LinkDown("laptop"), new Show(Exit)), Exit, "(2000, 1100): the link to laptop dropped"),
            "the laptop goes to its secure desktop" => (Do(() => c.OnFrame("laptop", new Locked(true)), new Show(Exit)), Exit, "(2000, 1100): laptop locked or stopped reading"),
            "snap-back hotkey" => (Do(() => c.SnapBack(), new Send("laptop", new Handoff("pc", Exit)), new Show(Exit)), Exit, "(2000, 1100): the snap-back hotkey"),
            _ => (Do(() => c.SecureDesktop(true), new Send("laptop", new Locked(true)), new Send("laptop", new Handoff("pc", Exit)), new Show(Exit)), Exit,
                "(2000, 1100): this machine went to its secure desktop (lock, UAC or Ctrl+Alt+Del)"),
        };

        // Only the edge crossing comes with the hand moving; every other return shows the cursor at once, so a still
        // hand never leaves it hidden at the park point with a click going to the blank window.
        Assert.Equal(expected, c.TakeEffects());
        Assert.Equal([$"back from laptop at {logged}"], c.TakeNotes());
        Assert.Equal((Mode.Local, null), (c.Mode, c.Peer));

        // The hand moves on. A move already in flight was computed from the park, so the hook's point is the park plus that
        // motion: it must not pass (the cursor would show by the portrait's centre). The hook shows it at the return point.
        Assert.True(c.OnMove(new Point(720, 1277)));
        Assert.Equal(new Place(at, Hidden: false), c.TakePlace());
        Assert.False(c.OnMove(at + new Point(0, -3))); // local input passes through again, from there
        Assert.Empty(c.TakeEffects());
        Assert.Equal([$"the first hook point after it came back is ({at.X}, {at.Y - 3})"], c.TakeNotes());
    }

    [Fact]
    public void Held_applies_moves_1to1_follows_the_local_mouse_and_hands_the_cursor_on_at_the_edge()
    {
        Cursor c = Held();

        c.OnFrame("laptop", new PointerMove(5, -3));
        c.OnFrame("laptop", CtrlDown);
        Assert.False(c.OnMove(new Point(2100, 1000))); // someone uses the pc's own mouse
        c.OnFrame("laptop", new PointerMove(1, 1));
        c.OnFrame("laptop", new PointerMove(0, 200)); // (2101, 1201): past the 1080p's bottom, onto the laptop
        c.OnFrame("laptop", new PointerMove(1, 1));   // no longer ours: ignored

        Assert.Equal<Effect>(
        [
            new MoveTo(new Point(2005, 1087)),
            new Inject(CtrlDown),
            new MoveTo(new Point(2101, 1001)),
            new Send("laptop", new Handoff("laptop", new Point(661, 2))),
            new Inject(CtrlDown with { Down = false }),
        ], c.TakeEffects());
        Assert.Equal(Mode.Local, c.Mode);
    }

    [Fact]
    public void Held_drag_stops_at_the_edge_instead_of_crossing()
    {
        Cursor c = Held();

        c.OnFrame("laptop", new PointerButton(MouseButton.Left, true));
        c.OnFrame("laptop", new PointerMove(0, 200));

        Assert.Equal<Effect>([new Inject(new PointerButton(MouseButton.Left, true)), new MoveTo(new Point(2000, 1104))], c.TakeEffects());
        Assert.Equal(Mode.Held, c.Mode);
    }

    [Theory]
    [InlineData("the laptop takes it elsewhere")]
    [InlineData("the link to the laptop drops")]
    [InlineData("this machine goes to its secure desktop")]
    [InlineData("snap-back hotkey pressed here")]
    public void Held_ends_and_every_key_the_controller_held_is_released(string why)
    {
        Cursor c = Held();
        c.OnFrame("laptop", CtrlDown);
        c.OnFrame("laptop", new PointerButton(MouseButton.Right, true));
        _ = c.TakeEffects();

        Effect[] first = why switch
        {
            "the laptop takes it elsewhere" => Do(() => c.OnFrame("laptop", new Handoff("laptop", new Point(10, 10)))),
            "the link to the laptop drops" => Do(() => c.LinkDown("laptop")),
            "this machine goes to its secure desktop" => Do(() => c.SecureDesktop(true), new Send("laptop", new Locked(true))),
            // (2000, 1090) on the pc is desk (2000, 1090), which is laptop (560, -15): off its monitors, so it
            // releases where its cursor left.
            _ => Do(() => c.SnapBack(), new Send("laptop", new Handoff("laptop", new Point(560, -15)))),
        };

        Assert.Equal([.. first, new Inject(CtrlDown with { Down = false }), new Inject(new PointerButton(MouseButton.Right, false))], c.TakeEffects());
        Assert.Equal(Mode.Local, c.Mode);
    }

    [Fact]
    public void A_machine_on_its_secure_desktop_refuses_the_cursor()
    {
        Cursor c = Pc();
        c.SecureDesktop(true);
        _ = c.TakeEffects();

        c.OnFrame("laptop", new Handoff("pc", new Point(2000, 1090)));

        Assert.Equal<Effect>([new Send("laptop", new Locked(true))], c.TakeEffects());
        Assert.Equal(Mode.Local, c.Mode);
    }

    private static Cursor Pc()
    {
        Cursor c = new("pc", ExampleDesk.Flush(), ParkPoint);
        c.LinkUp("laptop");
        return c;
    }

    /// <summary>The pc drives the laptop: it pushed down off (2000, 1100) onto the laptop's (560, 2); the hook parked it.</summary>
    private static Cursor Driving()
    {
        Cursor c = Pc();
        c.Resync(Exit);
        Assert.True(c.OnMove(new Point(2000, 1104)));
        _ = c.TakePlace();
        _ = c.TakeEffects();
        _ = c.TakeNotes();
        return c;
    }

    /// <summary>The laptop drives the pc's cursor, which it handed over at (2000, 1090).</summary>
    private static Cursor Held()
    {
        Cursor c = Pc();
        c.OnFrame("laptop", new Handoff("pc", new Point(2000, 1090)));
        Assert.Equal<Effect>([new MoveTo(new Point(2000, 1090))], c.TakeEffects());
        Assert.Equal((Mode.Held, "laptop"), (c.Mode, c.Peer));
        return c;
    }

    private static Effect[] Do(Action act, params Effect[] expected)
    {
        act();
        return expected;
    }
}
