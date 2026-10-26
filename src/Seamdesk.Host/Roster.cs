using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Seamdesk.Core;
using Seamdesk.Mwb;
using Seamdesk.Transport;

namespace Seamdesk.Host;

/// <summary>
/// The peers this machine knows, Seamdesk and MWB, and every rule about them: their settings (saved on each change), the
/// pins, the links of the Seamdesk node and the MWB node, what each link reports, the layout revision and copy and paste.
/// It owns the <see cref="Cursor"/> and keeps its desk. The settings window, the tray and App all go through it.
/// Events are raised on whichever thread made the change, link threads included.
/// </summary>
public sealed class Roster : IAsyncDisposable
{
    private readonly string path;
    private readonly string me;
    private readonly Node node;
    private readonly Func<string, MwbKey?> mwbKey;
    private readonly Action<string> forgetMwbKey;
    private readonly Lock gate = new(); // guards settings, which Apply replaces whole
    private readonly SemaphoreSlim restarting = new(1);
    private readonly ConcurrentDictionary<string, LinkState> states = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<Rect>> reported = new();
    private readonly ConcurrentDictionary<string, int> activeLayouts = new();
    private readonly ConcurrentDictionary<string, ILink> links = new();
    private Settings settings;
    private Screens screens;
    private MwbNode? mwb;

    /// <param name="path">Where <paramref name="settings"/> came from; every change is saved there.</param>
    /// <param name="node">
    /// Makes the Seamdesk node on these pins, the settings' peers. From then on they are the one pin set: pairing and
    /// <see cref="Remove"/> change them, and the settings' <see cref="Settings.Peers"/> are written from them.
    /// </param>
    /// <param name="fullscreen">A fullscreen app is focused (auto-pause).</param>
    /// <param name="mwbKey">An MWB peer's security key, or null if none is kept.</param>
    public Roster(string path, Settings settings, Func<Pins, Node> node, Screens screens, Func<bool> fullscreen,
        Func<string, MwbKey?> mwbKey, Action<string> forgetMwbKey)
    {
        this.path = path;
        this.settings = settings;
        this.screens = screens;
        this.mwbKey = mwbKey;
        this.forgetMwbKey = forgetMwbKey;
        this.node = node(new Pins(settings.Peers));
        me = this.node.Local.Hello.MachineId;
        Cursor = new Cursor(me, Desk(), screens.Park, () => this.settings.AutoPauseFullscreen && fullscreen());
        this.node.Local.Pins.Added += _ =>
        {
            lock (gate)
            {
                Save();
            }

            Changed?.Invoke();
        };
    }

    public Cursor Cursor { get; }

    /// <summary>Every step of every MWB link, and what the roster skips. One line each.</summary>
    public Action<string> Log { get; init; } = _ => { };

    /// <summary>How MWB links wait out the millisecond between two move packages (<see cref="MwbSession.Delay"/>).</summary>
    public Func<TimeSpan, CancellationToken, Task>? MwbDelay { get; init; }

    /// <summary>A peer's link changed, a pairing finished, a peer reported monitors or a layout, or the settings changed.</summary>
    public event Action? Changed;

    /// <summary>A peer's clipboard text, from a peer this machine takes it from (copy and paste both ways).</summary>
    public event Action<string>? ClipboardReceived;

    /// <summary>Something the user should see, from a link.</summary>
    public event Action<string>? Notice;

    /// <summary>A snapshot of the settings in force.</summary>
    public Settings Settings
    {
        get
        {
            lock (gate)
            {
                return Synced().Clone();
            }
        }
    }

    /// <summary>The peer's link, or null if it has not been tried since Seamdesk started (or since it was removed).</summary>
    public LinkState? State(string peer) => states.GetValueOrDefault(peer);

    /// <summary>The saved layout an MWB peer is using now (an index into its Layouts), or -1 if it hasn't said.</summary>
    public int ActiveLayout(string mwbPeer) => activeLayouts.GetValueOrDefault(mwbPeer, -1);

    /// <summary>Listens, dials every Seamdesk peer and starts the MWB peers.</summary>
    public void Start()
    {
        node.LinkChanged += (pin, state) =>
        {
            Supervisor supervisor = node[pin];
            Connection? connection = supervisor.Current;
            OnLink(pin.Name, state, connection, connection?.Incoming, () => Greet(supervisor));
        };
        node.Start();
        StartMwb();
    }

    public PairingCode ShowPairingCode() => node.ShowPairingCode();

    /// <summary>Pairs with the machine showing <paramref name="code"/>. Returns its name.</summary>
    public async Task<string> PairAsync(string host, string code) => (await node.PairAsync(host, code)).Name;

    /// <summary>
    /// Saves <paramref name="changed"/> and puts it into effect: the cursor's desk, a moved layout as a new layout
    /// revision sent to every Seamdesk peer, and a changed MWB peer restarts the MWB node. Pins and the monitors peers
    /// reported change only through pairing and the peers, so <paramref name="changed"/>'s are ignored.
    /// </summary>
    public void Apply(Settings changed)
    {
        Settings old;
        lock (gate)
        {
            old = settings;
            bool moved = !changed.Offsets.OrderBy(o => o.Key).SequenceEqual(old.Offsets.OrderBy(o => o.Key));
            changed.LayoutRevision = moved ? old.LayoutRevision + 1 : old.LayoutRevision;
            changed.Monitors = old.Monitors;
            settings = changed;
            Save();
        }

        SetDesk();
        if (changed.LayoutRevision != old.LayoutRevision)
        {
            SendLayout();
        }

        if (JsonSerializer.Serialize(old.MwbPeers) != JsonSerializer.Serialize(changed.MwbPeers))
        {
            _ = RestartMwbAsync();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Forgets a peer, Seamdesk or MWB: its pin (so its next dial is refused as unpaired), its place on the desk, what it
    /// reported, its copy and paste, and an MWB peer's key. An MWB peer reaches the other Seamdesk peers' desks only
    /// through this machine's layout, so its offset leaves theirs too (a new layout revision); a Seamdesk peer's stays,
    /// for the machines still paired with it.
    /// </summary>
    public void Remove(string peer)
    {
        if (node.Local.Pins.All.FirstOrDefault(p => p.Name == peer) is { } pin)
        {
            node.Unpair(pin);
        }

        bool wasMwb, placed;
        lock (gate)
        {
            placed = settings.Offsets.Remove(peer);
            settings.Monitors.Remove(peer);
            settings.Clipboard.Remove(peer);
            wasMwb = settings.MwbPeers.RemoveAll(p => p.Name == peer) > 0;
            if (wasMwb && placed)
            {
                settings.LayoutRevision++;
            }

            Save();
        }

        states.TryRemove(peer, out _);
        reported.TryRemove(peer, out _);
        activeLayouts.TryRemove(peer, out _);
        SetDesk();
        if (wasMwb)
        {
            forgetMwbKey(peer);
            if (placed)
            {
                SendLayout();
            }

            _ = RestartMwbAsync();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Sends one of the cursor's frames to a peer, if linked. Crossing into a peer that gets this machine's clipboard
    /// sends the text first (<paramref name="clipboard"/> reads it), so a paste there has it. The crossing goes out
    /// even if the text doesn't (the wire refuses text over 64 KiB); that error is thrown after. A move sent to an MWB
    /// peer is first counted in its link's timing lines (the link may hold it back and sum it with the next).
    /// </summary>
    public async Task SendAsync(Send send, Func<Task<string?>> clipboard)
    {
        (string peer, Frame frame) = (send.Peer, send.Frame);
        if (Peer(peer) is not { } link)
        {
            return;
        }

        try
        {
            if (frame is Handoff h && h.Machine == peer && Copying(peer).Sends() && await clipboard() is { } text)
            {
                await link.SendAsync(new ClipboardText(text));
            }
        }
        finally
        {
            if (send.Events > 0 && link is Supervisor<MwbLink> { Current: { } mwbLink })
            {
                mwbLink.Moving(send.Since, send.Events);
            }

            await link.SendAsync(frame);
        }
    }

    /// <summary>This machine's monitors changed: a new desk for the cursor, and the monitors go to every Seamdesk peer.</summary>
    public void DisplaysChanged(Screens now)
    {
        lock (gate)
        {
            screens = now;
        }

        SetDesk();
        foreach (Pin pin in node.Local.Pins.All)
        {
            _ = node[pin].SendAsync(new Monitors(now.Rects));
        }
    }

    /// <summary>Retry every peer now (resume, unlock, address change, tray).</summary>
    public void KickAll()
    {
        node.KickAll();
        mwb?.KickAll();
    }

    /// <summary>After the MWB peers' settings or keys changed: drop every MWB link and start again from settings.</summary>
    public async Task RestartMwbAsync()
    {
        await restarting.WaitAsync();
        try
        {
            if (mwb is { } old)
            {
                mwb = null;
                await old.DisposeAsync();
                foreach (string name in links.Keys.Where(n => old[n] is not null).ToList())
                {
                    links.TryRemove(name, out _);
                    Cursor.LinkDown(name);
                }
            }

            StartMwb();
        }
        finally
        {
            restarting.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await node.DisposeAsync();
        if (mwb is { } m)
        {
            await m.DisposeAsync();
        }
    }

    /// <summary>
    /// The MWB peers to link with: legacy security on, not paired as Seamdesk since, with a saved layout that has a monitor
    /// and with a key. A peer without an MWB id gets one; <paramref name="newIds"/> says the settings need saving.
    /// </summary>
    public static List<MwbPeer> MwbSessions(Settings s, Func<string, MwbKey?> key, Func<Desk> desk, string me, Action<string> log, out bool newIds)
    {
        // MWB knows this machine by its host name, cut to 32 characters (Core/Common.cs:1361-1385 at a9e1f0f20^).
        string myName = Dns.GetHostName().Trim();
        myName = myName.Length > 32 ? myName[..32] : myName;
        newIds = false;
        List<MwbPeer> peers = [];
        foreach (MwbPeerSettings p in s.MwbPeers)
        {
            if (!p.LegacySecurity || s.Peers.Any(pin => string.Equals(pin.Name, p.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!p.HasScreens)
            {
                log($"mwb {p.Name}: no saved layout with a monitor; skipped");
                continue;
            }

            if (p.MachineId is 0 or 255) // MWB's "none" and "all"
            {
                p.MachineId = (uint)RandomNumberGenerator.GetInt32(256, int.MaxValue);
                newIds = true;
            }

            if (key(p.Name) is not { } k)
            {
                log($"mwb {p.Name}: no security key yet; set it in Settings > Machines");
                continue;
            }

            peers.Add(new MwbPeer(p.Host, new MwbSession(
                p.Name, k, myName, p.MachineId, me, new MwbScreens([.. p.Layouts]), desk, TimeProvider.System)
            {
                Format = p.Version switch { MwbVersion.Old => MwbFormat.Old, MwbVersion.Current => MwbFormat.Current, _ => null },
                CurrentSeen = p.Detected == MwbVersion.Current,
                AllowControl = p.AllowControl,
                TakeClipboard = p.Clipboard.Takes(),
            }));
        }

        return peers;
    }

    /// <summary>MWB compatibility: the peers with legacy security on (design.md, "MWB compatibility mode").</summary>
    private void StartMwb()
    {
        List<MwbPeer> peers;
        lock (gate)
        {
            peers = MwbSessions(Synced(), mwbKey, Desk, me, Log, out bool newIds);
            if (newIds)
            {
                Save();
            }
        }

        if (peers.Count == 0)
        {
            return;
        }

        MwbNode started = new([.. peers.Select(p => p with { Session = p.Session with { Delay = MwbDelay } })]) { Log = Log };
        started.LinkChanged += (name, state) =>
        {
            MwbLink? link = started[name]?.Current;
            OnLink(name, state, link, link?.Incoming, () => Detected(name, link!.Format));
        };
        mwb = started;
        started.Start();
    }

    /// <summary>A link of either node changed. Raised from supervisor threads.</summary>
    /// <param name="greet">Runs once per new link, after it is read.</param>
    private void OnLink(string name, LinkState state, ILink? link, ChannelReader<Frame>? incoming, Action greet)
    {
        states[name] = state;
        Changed?.Invoke();
        if (state.Status == LinkStatus.Connected && link is not null)
        {
            if (links.TryGetValue(name, out ILink? known) && known == link)
            {
                return;
            }

            links[name] = link;
            Cursor.LinkUp(name);
            _ = ReadAsync(name, incoming!); // an MWB peer's saved layout first, then hand-backs and, if allowed, its input
            greet();
        }
        else if (state.Status != LinkStatus.Connected && links.TryRemove(name, out _))
        {
            Cursor.LinkDown(name);
        }
    }

    /// <summary>A new Seamdesk link gets this machine's monitors and, once there is one, the layout.</summary>
    private void Greet(Supervisor peer)
    {
        _ = peer.SendAsync(new Monitors(screens.Rects));
        if (LayoutNow() is { Revision: > 0 } layout)
        {
            _ = peer.SendAsync(layout);
        }
    }

    /// <summary>Keeps the format an MWB peer spoke, for the settings window and for Auto's no-fallback rule.</summary>
    private void Detected(string peer, MwbFormat format)
    {
        MwbVersion version = format == MwbFormat.Current ? MwbVersion.Current : MwbVersion.Old;
        lock (gate)
        {
            if (settings.MwbPeers.FirstOrDefault(p => p.Name == peer) is not { } p || p.Detected == version)
            {
                return;
            }

            p.Detected = version;
            Save();
        }

        Log($"mwb {peer}: speaks the {version} format");
        Changed?.Invoke();
    }

    private async Task ReadAsync(string peer, ChannelReader<Frame> incoming)
    {
        await foreach (Frame frame in incoming.ReadAllAsync())
        {
            switch (frame)
            {
                case Monitors m:
                    reported[peer] = m.Rects;
                    lock (gate)
                    {
                        if (settings.MwbPeers.FirstOrDefault(p => p.Name == peer) is { } mwbPeer)
                        {
                            activeLayouts[peer] = mwbPeer.Layouts.FindIndex(l => l.SequenceEqual(m.Rects));
                        }
                        else if (!settings.Monitors.TryGetValue(peer, out List<Rect>? known) || !known.SequenceEqual(m.Rects))
                        {
                            settings.Monitors[peer] = [.. m.Rects];
                            Save();
                        }
                    }

                    SetDesk();
                    Changed?.Invoke();
                    break;
                case ClipboardText c when Copying(peer).Takes():
                    ClipboardReceived?.Invoke(c.Text);
                    break;
                case Layout l:
                    lock (gate)
                    {
                        if (l.Revision > settings.LayoutRevision)
                        {
                            settings.LayoutRevision = l.Revision;
                            settings.Offsets = l.Offsets.ToDictionary(o => o.Machine, o => o.Offset);
                            Save();
                        }
                    }

                    SetDesk();
                    Changed?.Invoke();
                    break;
                case Notice n:
                    Notice?.Invoke(n.Text);
                    break;
                default:
                    Cursor.OnFrame(peer, frame);
                    break;
            }
        }
    }

    /// <summary>A peer's link supervisor, Seamdesk or MWB, or null if it is neither.</summary>
    private IPeer? Peer(string name) =>
        node.Local.Pins.All.FirstOrDefault(p => p.Name == name) is { } pin ? node[pin] : mwb?[name];

    /// <summary>Copy and paste with a peer, Seamdesk or MWB.</summary>
    private ClipboardMode Copying(string peer)
    {
        lock (gate)
        {
            return settings.MwbPeers.FirstOrDefault(p => p.Name == peer) is { } mwbPeer ? mwbPeer.Clipboard : settings.Clipboard.GetValueOrDefault(peer);
        }
    }

    private void SendLayout()
    {
        Layout layout = LayoutNow();
        foreach (Pin pin in node.Local.Pins.All)
        {
            _ = node[pin].SendAsync(layout);
        }
    }

    private Layout LayoutNow()
    {
        lock (gate)
        {
            return new Layout(settings.LayoutRevision, [.. settings.Offsets.Select(o => new MachineOffset(o.Key, o.Value))]);
        }
    }

    private void SetDesk() => Cursor.SetDesk(Desk(), screens.Park);

    /// <summary>The cursor's desk: each peer with the monitors its link last reported.</summary>
    private Desk Desk()
    {
        lock (gate)
        {
            return new Desk(ConfiguredDesk.Live(Synced(), me, screens.Rects, reported.GetValueOrDefault).Machines);
        }
    }

    /// <summary>The settings with the pins as their peers. Call under the gate.</summary>
    private Settings Synced()
    {
        settings.Peers = [.. node.Local.Pins.All];
        return settings;
    }

    /// <summary>Call under the gate.</summary>
    private void Save() => Synced().Save(path);
}
