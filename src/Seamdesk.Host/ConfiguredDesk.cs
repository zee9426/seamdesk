using Seamdesk.Core;

namespace Seamdesk.Host;

/// <summary>
/// The desk the settings describe, built one way for the cursor and for the layout editor, so the editor's preview is
/// exactly what the cursor will do: this machine at its offset, then each peer, Seamdesk then MWB, whose monitors are known
/// and that has an offset.
/// </summary>
/// <param name="Waiting">Peers whose monitors are not known, so they are not on the desk.</param>
/// <param name="PlacedNew">The editor started a peer without an offset to the right of the desk.</param>
public sealed record ConfiguredDesk(IReadOnlyList<Machine> Machines, IReadOnlyList<string> Waiting, bool PlacedNew)
{
    /// <summary>The cursor's desk: each peer with the monitors its link reported (null if none yet).</summary>
    public static ConfiguredDesk Live(Settings s, string me, IReadOnlyList<Rect> mine, Func<string, IReadOnlyList<Rect>?> reported) =>
        Build(s, me, mine, reported, placeNew: false);

    /// <summary>
    /// The editor's desk: a Seamdesk peer with the monitors it last reported, an MWB peer with the saved layout
    /// <paramref name="showing"/> picks. An MWB peer without a saved layout that has a monitor waits, as it never links.
    /// A peer not placed yet starts to the right of the desk, top aligned, for the user to drag into place.
    /// </summary>
    public static ConfiguredDesk Saved(Settings s, string me, IReadOnlyList<Rect> mine, Func<string, int> showing) =>
        Build(s, me, mine, name => s.MwbPeers.FirstOrDefault(p => p.Name == name) is { } mwb
            ? (mwb.HasScreens ? mwb.Layouts[showing(name)] : null)
            : s.Monitors.GetValueOrDefault(name), placeNew: true);

    private static ConfiguredDesk Build(Settings s, string me, IReadOnlyList<Rect> mine, Func<string, IReadOnlyList<Rect>?> monitors, bool placeNew)
    {
        List<Machine> machines = [new Machine(me, s.Offsets.GetValueOrDefault(me), mine)];
        List<string> waiting = [];
        bool placedNew = false;
        foreach (string name in s.PeerNames)
        {
            if (monitors(name) is not { Count: > 0 } rects)
            {
                waiting.Add(name);
            }
            else if (s.Offsets.TryGetValue(name, out Point offset))
            {
                machines.Add(new Machine(name, offset, rects));
            }
            else if (placeNew)
            {
                Rect box = LayoutEditor.Bounds(machines);
                machines.Add(new Machine(name, new Point(box.Right + 200 - rects.Min(m => m.Left), box.Top - rects.Min(m => m.Top)), rects));
                placedNew = true;
            }
        }

        return new ConfiguredDesk(machines, waiting, placedNew);
    }
}
