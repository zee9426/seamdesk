using Seamdesk.Core;
using Seamdesk.Core.Tests;
using Seamdesk.Transport;

namespace Seamdesk.Host.Tests;

public class ConfiguredDeskTests
{
    private static readonly Desk Flush = ExampleDesk.Flush();
    private static readonly IReadOnlyList<Rect> Pc = Flush.Machines[0].Monitors;
    private static readonly IReadOnlyList<Rect> Laptop = Flush.Machines[1].Monitors;

    /// <summary>
    /// The example desk, plus "study", a Seamdesk peer not placed yet whose 1080p sits left of and above its own origin, at
    /// (-1920, -300). The example desk's box ends at the laptop's panel, x 1440 + 2560 + 1920 = 5920, and starts at y 0, so
    /// study's leftmost monitor starts 200 px right of it, at 6120, top aligned: offset (6120 + 1920, 0 + 300).
    /// </summary>
    [Fact]
    public void The_editor_starts_a_peer_not_placed_yet_right_of_the_desk_and_the_cursor_leaves_it_off()
    {
        List<Rect> study = [new Rect(-1920, -300, 1920, 1080), new Rect(0, 0, 2560, 1440)];
        Settings s = new()
        {
            Peers = [new Pin("01", "laptop", "192.168.1.20"), new Pin("02", "study", "192.168.1.21")],
            Offsets = { ["pc"] = new Point(0, 0), ["laptop"] = new Point(1440, 1105) },
            Monitors = { ["laptop"] = [.. Laptop], ["study"] = study },
        };

        ConfiguredDesk editor = ConfiguredDesk.Saved(s, "pc", Pc, _ => 0);
        ConfiguredDesk cursor = ConfiguredDesk.Live(s, "pc", Pc, name => name == "study" ? study : Laptop);

        Assert.Equal([("pc", new Point(0, 0)), ("laptop", new Point(1440, 1105)), ("study", new Point(8040, 300))], editor.Machines.Select(m => (m.Name, m.Offset)));
        Assert.True(editor.PlacedNew);
        Assert.Equal(Shape(Flush.Machines), Shape(cursor.Machines));
        Assert.Equal(Shape(cursor.Machines), Shape(editor.Machines.Take(2)));
        Assert.False(cursor.PlacedNew);
    }

    /// <summary>
    /// The laptop runs MWB with two saved layouts; the editor shows the one in use (the second, the example desk's). "old"
    /// is an MWB peer whose settings have no saved layout: it never links, so it is on neither desk, and the editor lists it
    /// as waiting instead of failing. "study" is a Seamdesk peer that never connected.
    /// </summary>
    [Fact]
    public void An_MWB_peer_shows_its_layout_in_use_and_one_without_a_saved_screen_and_a_peer_never_seen_wait()
    {
        Settings s = new()
        {
            Peers = [new Pin("02", "study", "192.168.1.21")],
            Offsets = { ["laptop"] = new Point(1440, 1105), ["old"] = new Point(0, 2560), ["study"] = new Point(-2560, 0) },
            MwbPeers =
            [
                new MwbPeerSettings { Name = "laptop", LegacySecurity = true, Layouts = [[Laptop[0]], [.. Laptop]] },
                new MwbPeerSettings { Name = "old", LegacySecurity = true },
            ],
        };

        ConfiguredDesk editor = ConfiguredDesk.Saved(s, "pc", Pc, name => name == "laptop" ? 1 : 0);
        ConfiguredDesk cursor = ConfiguredDesk.Live(s, "pc", Pc, name => name == "laptop" ? Laptop : null);

        Assert.Equal(Shape(Flush.Machines), Shape(editor.Machines));
        Assert.Equal(["study", "old"], editor.Waiting);
        Assert.False(editor.PlacedNew);
        Assert.Equal(Shape(editor.Machines), Shape(cursor.Machines));
    }

    private static List<(string, Point, string)> Shape(IEnumerable<Machine> machines) =>
        [.. machines.Select(m => (m.Name, m.Offset, string.Join(" ", m.Monitors)))];
}
