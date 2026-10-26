using System.Collections.Concurrent;
using Seamdesk.Core;
using Seamdesk.Core.Tests;
using Seamdesk.Mwb;
using Seamdesk.Transport;
using Seamdesk.Transport.Tests;

namespace Seamdesk.Host.Tests;

/// <summary>
/// The roster on real nodes over loopback, set up as <see cref="Seamdesk.Core.Tests.ExampleDesk"/>: "pc" has the portrait
/// and the 1080p, "laptop" its 1440p and panel, placed under the 1080p at desk (1440, 1105). The laptop pairs with the
/// pc and dials it (the pc's own dials go to a closed port), so both links come up.
/// </summary>
public class RosterTests
{
    private const string Pc = "pc.localhost";
    private const string Laptop = "laptop.localhost";
    private static readonly Display[] PcMonitors = [new(0, 0, 1440, 2560, true), new(1440, 25, 3360, 1105, false)];
    private static readonly Display[] LaptopMonitors = [new(0, 0, 2560, 1440, true), new(2560, 0, 4480, 1200, false)];

    /// <summary>
    /// The pc has a layout (revision 3) and the laptop none. A new link brings each side the other's monitors and the
    /// laptop the pc's layout, so the pc's cursor crosses into the laptop off the 1080p's bottom row: pc (2000, 1104)
    /// continues at laptop (560, 2). Moving the laptop on the pc is revision 4, and reaches the laptop.
    /// </summary>
    [Fact]
    public async Task A_new_link_brings_monitors_and_the_layout_and_a_moved_layout_reaches_the_peer()
    {
        Dictionary<string, Point> desk = new() { [Pc] = new Point(0, 0), [Laptop] = new Point(1440, 1105) };
        await using Side pc = Start(Pc, PcMonitors, new Settings { Offsets = new(desk), LayoutRevision = 3 });
        await using Side laptop = Start(Laptop, LaptopMonitors);

        await Pair(pc, laptop);

        await Kit.Eventually(() => laptop.Saved.LayoutRevision == 3 && laptop.Saved.Monitors.ContainsKey(Pc) && pc.Saved.Monitors.ContainsKey(Laptop));
        Assert.Equal(desk, laptop.Saved.Offsets);
        Assert.Equal([new Rect(0, 0, 2560, 1440), new Rect(2560, 0, 1920, 1200)], pc.Saved.Monitors[Laptop]);
        Assert.Equal([new Rect(0, 0, 1440, 2560), new Rect(1440, 25, 1920, 1080)], laptop.Saved.Monitors[Pc]);
        Cursor cursor = pc.Roster.Cursor;
        await Kit.Eventually(() =>
        {
            cursor.Resync(new Point(2000, 1100));
            return cursor.OnMove(new Point(2000, 1104));
        });
        Assert.Contains(new Send(Laptop, new Handoff(Laptop, new Point(560, 2))), cursor.TakeEffects());

        Settings moved = pc.Roster.Settings;
        moved.Offsets[Laptop] = new Point(1440, 2560);
        pc.Roster.Apply(moved);

        await Kit.Eventually(() => laptop.Saved.LayoutRevision == 4);
        Assert.Equal(4, pc.Saved.LayoutRevision);
        Assert.Equal(new Point(1440, 2560), laptop.Saved.Offsets[Laptop]);
    }

    /// <summary>
    /// The pc crosses into the laptop with "hello" on its clipboard. The text goes only where the pc sends it and the laptop
    /// takes it (both ways); the crossing goes out either way, so the laptop's cursor is held.
    /// </summary>
    [Theory]
    [InlineData(ClipboardMode.Off, ClipboardMode.BothWays, false)]
    [InlineData(ClipboardMode.ToPeer, ClipboardMode.ToPeer, false)] // the laptop sends its own, but takes none
    [InlineData(ClipboardMode.ToPeer, ClipboardMode.BothWays, true)]
    [InlineData(ClipboardMode.BothWays, ClipboardMode.BothWays, true)]
    public async Task Crossing_takes_the_clipboard_along_only_where_this_machine_sends_it_and_the_peer_takes_it(ClipboardMode pcSends, ClipboardMode laptopTakes, bool arrives)
    {
        await using Side pc = Start(Pc, PcMonitors, new Settings { Clipboard = { [Laptop] = pcSends } });
        await using Side laptop = Start(Laptop, LaptopMonitors, new Settings { Clipboard = { [Pc] = laptopTakes } });
        await Pair(pc, laptop);

        await pc.Roster.SendAsync(new Send(Laptop, new Handoff(Laptop, new Point(560, 0))), () => Task.FromResult<string?>("hello"));

        await Kit.Eventually(() => laptop.Roster.Cursor.Mode == Mode.Held);
        Assert.Equal(arrives ? ["hello"] : [], laptop.Clipboard);
    }

    /// <summary>
    /// Unpairing a Seamdesk peer forgets its pin, place, monitors and copy and paste, here and in the file, without a new layout
    /// revision: the machines still paired with it keep its place.
    /// </summary>
    [Fact]
    public async Task Removing_a_Seamdesk_peer_forgets_its_pin_and_everything_kept_for_it_but_keeps_the_layout_revision()
    {
        await using Side pc = Start(Pc, PcMonitors, new Settings { Offsets = { [Laptop] = new Point(1440, 1105) }, LayoutRevision = 3, Clipboard = { [Laptop] = ClipboardMode.BothWays } });
        await using Side laptop = Start(Laptop, LaptopMonitors);
        await Pair(pc, laptop);
        await Kit.Eventually(() => pc.Saved.Monitors.ContainsKey(Laptop));

        pc.Roster.Remove(Laptop);

        foreach (Settings s in new[] { pc.Roster.Settings, pc.Saved })
        {
            Assert.Equal((0, 0, 0, 0, 3L), (s.Peers.Count, s.Offsets.Count, s.Monitors.Count, s.Clipboard.Count, s.LayoutRevision));
        }

        Assert.Null(pc.Roster.State(Laptop));
        Assert.Empty(pc.Node.Local.Pins.All);
        Assert.Empty(pc.Forgotten);
    }

    /// <summary>
    /// Removing an MWB peer forgets its settings, place and key. Its place reached the laptop only through the pc's layout,
    /// so a new revision takes it off the laptop's desk too.
    /// </summary>
    [Fact]
    public async Task Removing_an_MWB_peer_forgets_its_key_and_takes_it_off_every_Seamdesk_peers_desk()
    {
        Settings settings = new()
        {
            Offsets = { [Pc] = new Point(0, 0), ["old"] = new Point(0, 2560) },
            LayoutRevision = 2,
            MwbPeers = [new MwbPeerSettings { Name = "old", Host = "old.lan", LegacySecurity = true, Layouts = [[new Rect(0, 0, 1920, 1080)]] }],
        };
        await using Side pc = Start(Pc, PcMonitors, settings);
        await using Side laptop = Start(Laptop, LaptopMonitors);
        await Pair(pc, laptop);
        await Kit.Eventually(() => laptop.Saved.Offsets.ContainsKey("old"));

        pc.Roster.Remove("old");

        await Kit.Eventually(() => laptop.Saved.LayoutRevision == 3);
        Assert.Equal([Pc], laptop.Saved.Offsets.Keys);
        Assert.Equal(["old"], pc.Forgotten);
        Assert.Empty(pc.Saved.MwbPeers);
        Assert.Equal([Pc], pc.Saved.Offsets.Keys);
        Assert.Equal([Kit.PinOf(laptop.Node.Local)], pc.Saved.Peers);
    }

    /// <summary>
    /// Regression: re-pairing a machine that is still pinned used to save a second pin, and the next start failed. The
    /// laptop pairs, the pc forgets it, and the laptop, still pinning the pc, pairs again: one pin, the newer pairing.
    /// </summary>
    [Fact]
    public async Task Pairing_saves_the_pin_and_pairing_a_pinned_machine_again_replaces_it()
    {
        await using Side pc = Start(Pc, PcMonitors);
        await using Side laptop = Start(Laptop, LaptopMonitors);
        await Pair(pc, laptop);
        Assert.Equal([Kit.PinOf(laptop.Node.Local)], pc.Saved.Peers);

        pc.Roster.Remove(Laptop);
        await laptop.Roster.PairAsync("127.0.0.1", pc.Roster.ShowPairingCode().Code).WaitAsync(Kit.Patience);

        Assert.Equal([Kit.PinOf(pc.Node.Local)], laptop.Saved.Peers);
        Assert.Equal([Kit.PinOf(pc.Node.Local)], laptop.Roster.Settings.Peers);
    }

    /// <summary>
    /// The MWB peers Seamdesk links with, and what each session is told. Skipped: legacy security off, a machine since paired
    /// as Seamdesk (any case), one without a saved screen, one without a key. A peer without an MWB id gets one.
    /// </summary>
    [Fact]
    public void MWB_settings_become_sessions_only_for_peers_Seamdesk_can_link_with()
    {
        List<List<Rect>> one = [[new Rect(0, 0, 1920, 1080)]];
        Settings s = new()
        {
            Peers = [new Pin("01", "Study", "192.168.1.21")],
            MwbPeers =
            [
                new MwbPeerSettings { Name = "laptop", Host = "laptop.lan", LegacySecurity = true, Version = MwbVersion.Current, Detected = MwbVersion.Current, AllowControl = true, Clipboard = ClipboardMode.BothWays, Layouts = one },
                new MwbPeerSettings { Name = "auto", Host = "auto.lan", LegacySecurity = true, Detected = MwbVersion.Old, Clipboard = ClipboardMode.ToPeer, MachineId = 4242, Layouts = one },
                new MwbPeerSettings { Name = "off", LegacySecurity = false, Layouts = one },
                new MwbPeerSettings { Name = "study", LegacySecurity = true, Layouts = one },
                new MwbPeerSettings { Name = "blank", LegacySecurity = true, Layouts = [[]] },
                new MwbPeerSettings { Name = "nokey", LegacySecurity = true, MachineId = 7, Layouts = one },
            ],
        };
        MwbKey key = new("0123456789abcdef");
        List<string> log = [];

        List<MwbPeer> peers = Roster.MwbSessions(s, name => name == "nokey" ? null : key, () => ExampleDesk.Flush(), Pc, log.Add, out bool newIds);

        Assert.Equal([("laptop.lan", "laptop"), ("auto.lan", "auto")], peers.Select(p => (p.Host, p.Session.Peer)));
        MwbSession laptop = peers[0].Session, auto = peers[1].Session;
        Assert.Equal((MwbFormat.Current, true, true, true), (laptop.Format, laptop.CurrentSeen, laptop.AllowControl, laptop.TakeClipboard));
        Assert.Equal(((MwbFormat?)null, false, false, false, 4242u), (auto.Format, auto.CurrentSeen, auto.AllowControl, auto.TakeClipboard, auto.MyId));
        Assert.True(newIds);
        Assert.Equal(laptop.MyId, s.MwbPeers[0].MachineId);
        Assert.DoesNotContain(laptop.MyId, new uint[] { 0, 255 });
        Assert.Equal(Pc, laptop.Me);
        Assert.Equal(["mwb blank: no saved layout with a monitor; skipped", "mwb nokey: no security key yet; set it in Settings > Machines"], log);
    }

    /// <summary>One machine: a roster on a real node on loopback, its settings file, and what it handed to the app.</summary>
    private sealed class Side(string name, Roster roster, Func<Node> node, string path, List<string> forgotten) : IAsyncDisposable
    {
        public string Name => name;

        public Roster Roster => roster;

        public Node Node => node();

        public Settings Saved => Settings.Load(path);

        public List<string> Forgotten => forgotten;

        public ConcurrentQueue<string> Clipboard { get; } = new();

        public ValueTask DisposeAsync() => roster.DisposeAsync();
    }

    private static Side Start(string name, Display[] monitors, Settings? settings = null)
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory("seamdesk-roster-").FullName, "settings.json");
        LocalNode local = Kit.Local(name);
        Node? node = null;
        List<string> forgotten = [];
        Roster roster = new(path, settings ?? new Settings(), pins => node = new Node(local with { Pins = pins }, port: 0) { PeerPort = 1, Random = () => 0.5 },
            new Screens(monitors), () => false, _ => null, forgotten.Add);
        Side side = new(name, roster, () => node!, path, forgotten);
        roster.ClipboardReceived += side.Clipboard.Enqueue;
        roster.Start();
        return side;
    }

    /// <summary>The laptop pairs with the pc and dials it; waits for both links.</summary>
    private static async Task Pair(Side pc, Side laptop)
    {
        laptop.Node.PeerPort = pc.Node.Port;
        await laptop.Roster.PairAsync("127.0.0.1", pc.Roster.ShowPairingCode().Code).WaitAsync(Kit.Patience);
        await Kit.Eventually(() => pc.Roster.State(laptop.Name)?.Status == LinkStatus.Connected && laptop.Roster.State(pc.Name)?.Status == LinkStatus.Connected);
    }
}
