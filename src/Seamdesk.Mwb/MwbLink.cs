using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Seamdesk.Core;
using Seamdesk.Transport;

namespace Seamdesk.Mwb;

/// <summary>Something the user should see, for example that the peer's screens match no saved layout.</summary>
public sealed record Notice(string Text) : Frame;

/// <summary>What this machine is to one MWB peer, and what it knows about that peer.</summary>
/// <param name="Peer">The peer's machine name on Seamdesk's desk.</param>
/// <param name="MyName">This machine's name as MWB knows it (the host name, at most 32 characters).</param>
/// <param name="MyId">This machine's MWB id: any number but 0 (none) and 255 (all).</param>
/// <param name="Me">This machine's name on Seamdesk's desk.</param>
public sealed record MwbSession(string Peer, MwbKey Key, string MyName, uint MyId, string Me, MwbScreens Screens, Func<Desk> Desk, TimeProvider Time)
{
    /// <summary>The wire format the user chose for the peer, or null for Auto: whichever the peer speaks.</summary>
    public MwbFormat? Format { get; init; }

    /// <summary>
    /// The peer has spoken the current format (before, or on any link of this session), so Auto refuses to fall back to
    /// the old one. Set by the link that sees it.
    /// </summary>
    public bool CurrentSeen { get; set; }

    /// <summary>"Let this machine control Seamdesk": the peer's input is taken. Off unless the user turns it on.</summary>
    public bool AllowControl { get; init; }

    /// <summary>The peer's clipboard text is taken (copy and paste both ways). Off unless the user chooses.</summary>
    public bool TakeClipboard { get; init; }

    /// <summary>
    /// How a link waits out the rest of the millisecond between two move packages: unless set, <c>Task.Delay</c> on
    /// <see cref="Time"/>, rounded up to a whole millisecond. Windows' default timer ticks every 15.6 ms, so the app sets a
    /// high-resolution one.
    /// </summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
}

/// <summary>
/// A trusted connection to an MWB peer, in either wire format. Only a link whose handshake succeeded exists as an
/// object, so no input is sent or taken before. Driving the peer, it sends input as MWB packages and surfaces the peer's
/// hand-back as a <see cref="Handoff"/> to this machine, plus <see cref="Monitors"/> when the peer's saved layout
/// changes. Driven by the peer (only if <see cref="MwbSession.AllowControl"/>), it surfaces the peer's input as the
/// frames a Seamdesk controller sends, and hands the cursor back with <c>NextMachine</c>. The peer's clipboard is surfaced
/// only if <see cref="MwbSession.TakeClipboard"/>; every other package is dropped.
/// </summary>
public sealed class MwbLink : ILink
{
    public const int HandshakeCount = 10;

    /// <summary>MWB sends a heartbeat when a link is trusted, then every 1 or 5 minutes (Form/frmScreen.cs:565-577).</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// One move package this often, on average, each summing the moves since the last; one may go early (the cell rate
    /// algorithm, with this as both period and tolerance), so a 1 kHz mouse's jitter holds nothing back. A mouse polled at
    /// 4 or 8 kHz makes that many hook events a second, and the peer's MWB reads and injects packages one at a time, in
    /// order (SocketStuff.cs:1329-1546, Receiver.cs:147-170): sent faster than it injects them, they queue in its receive
    /// buffer and the cursor trails the hand.
    /// </summary>
    public static readonly TimeSpan MovePace = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Nothing from the peer for this long means its end is gone or its MWB hangs: MWB sends a heartbeat (or an awake beat)
    /// at least every 5 minutes (Form/frmScreen.cs:286, :567-571; Common.cs:545-560), so this is two missed and a minute.
    /// </summary>
    public static readonly TimeSpan Silence = TimeSpan.FromMinutes(11);

    /// <summary>
    /// Bytes sent to the peer that its MWB hasn't read: past this while Seamdesk drives it, it has stopped reading. That is
    /// 2048 packages, two seconds of moves at <see cref="MovePace"/>; a peer that keeps up stays within 1 KiB.
    /// </summary>
    private const int StalledBytes = 64 * 1024;

    /// <summary>A stalled peer reads again once its unread bytes are back under this.</summary>
    private const int DrainedBytes = 4 * 1024;

    /// <summary>
    /// A stalled peer may be crossed to again after this, read or not: a receiver may keep its window shut after reading
    /// (Linux does, after a flood of small packets). If it still doesn't read, the cursor comes back again.
    /// </summary>
    private static readonly TimeSpan StallHold = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan WatchEvery = TimeSpan.FromMilliseconds(100);

    // Event.cs:32: relative moves are offset by this much, away from zero.
    private const int Relative = 100000;

    // Clipboard.cs:43-47: text travels in 48-byte slices at the end of big packages; more than 1 MB compressed needs
    // MWB's separate clipboard socket, which Seamdesk never opens.
    private const int SliceSize = 48;
    private const int MaxClipboard = 1024 * 1024;
    private const string TextTypeSeparator = "{4CFF57F7-BEDD-43d5-AE8F-27A61E886F2F}";

    private const int MaxPeerText = 16 * 1024 * 1024;

    private const uint All = 255; // ID.ALL

    private readonly Stream stream;
    private readonly MwbSession s;
    private readonly Action<string> log;
    private readonly SendTiming timing;
    private readonly Action<string> driving; // unfolded: the timing lines and the stall's
    private MwbKey key; // the session's key, derived as the peer's MWB derives it
    private long received; // bytes read off the stream, for the log
    private ICryptoTransform encryptor = null!;
    private ICryptoTransform decryptor = null!;
    private byte[] replay = []; // what detecting the format read off the stream, still to be decrypted
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource<string> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<Frame> incoming = Channel.CreateUnbounded<Frame>();
    private int nextId = RandomNumberGenerator.GetInt32(int.MaxValue / 2); // MWB skips ids it saw recently (Receiver.cs PreProcess)
    private string? lastClipboard;
    private MemoryStream? peerClipboard;
    private volatile bool holding;
    private volatile bool driven;
    private Point at; // driven: where the controller's last absolute move put the cursor
    private readonly Lock moveGate = new(); // guards the moves not yet written
    private Point there; // holding: where the peer's cursor is on Seamdesk's desk, in the peer's pixels
    private bool moved; // there has changed since the last move written
    private int moveEvents; // the hook events they sum, the first seen at moveSince, for the timing lines
    private long moveSince;
    private bool pacing; // a move is held back until nextMove - MovePace
    private readonly long origin;
    private TimeSpan nextMove; // since origin: when the next move package is due; it may go a MovePace early
    private readonly Lock watchGate = new();
    private long heard; // when the last package from the peer was read
    private int widest; // the widest TCP window the peer has advertised on this link
    private bool stalled;
    private long stalledAt;
    private ITimer? watch;

    private MwbLink(Stream stream, MwbSession session, string dialedBy, Action<string>? log, Action<string>? timing)
    {
        this.stream = stream;
        s = session;
        key = session.Key;
        origin = session.Time.GetTimestamp();
        DialedBy = dialedBy;
        this.log = log ?? (_ => { });
        driving = timing ?? (_ => { });
        this.timing = new SendTiming(session.Time, driving, () => Tcp() is { } t ? $", tcp rtt {t.RttMs:F1} ms, peer window {t.Window / 1024} KiB" : "");
    }

    public string DialedBy { get; }

    public uint PeerId { get; private set; }

    /// <summary>The name the peer's MWB gave in its handshake reply.</summary>
    public string PeerName { get; private set; } = "";

    /// <summary>The wire format the peer spoke, so the settings can show what Auto found.</summary>
    public MwbFormat Format { get; private set; }

    /// <summary><see cref="Monitors"/>, then <see cref="Handoff"/>s and <see cref="Notice"/>s.</summary>
    public ChannelReader<Frame> Incoming => incoming.Reader;

    public Task<string> Closed => closed.Task;

    /// <summary>
    /// MWB's handshake (Class/SocketStuff.cs:1261-1565, unchanged from a9e1f0f20^ to 48c4bad07), the same on both ends:
    /// each side writes its stream start and ten Handshake packages holding four random words before it reads, answers
    /// every Handshake with the words inverted, and trusts the other once an answer inverts its own words. Seamdesk reads the
    /// peer's start first: it says which format the peer speaks and whether its key is ours, and Seamdesk answers in that
    /// format. A package that fails the magic number or checksum means the keys differ. Throws
    /// <see cref="RefusedException"/>, having closed the stream. <paramref name="log"/> gets each step and the close, never
    /// key material or what a package carries; <paramref name="timing"/> gets, unfolded, the send path's timing lines and
    /// a stalled peer's lines while driving.
    /// </summary>
    public static async Task<MwbLink> EstablishAsync(
        Stream stream, MwbSession session, bool dialer, CancellationToken ct, Action<string>? log = null, Action<string>? timing = null)
    {
        MwbLink link = new(stream, session, dialer ? session.MyName : session.Peer, log, timing);
        int answered = 0;
        try
        {
            await link.DetectAsync(ct);

            // The current format's salt and IV go first, in the clear (Encryption.cs:125-142 at 48c4bad07); then, in both,
            // a random block (Common.cs:1528-1560, SocketStuff.cs:98-120) and the Handshakes.
            byte[]? header = link.Format == MwbFormat.Current ? RandomNumberGenerator.GetBytes(MwbKey.HeaderSize) : null;
            if (header is not null)
            {
                await stream.WriteAsync(header, ct);
            }

            link.encryptor = link.key.Encryptor(header);
            await link.WriteRawAsync(RandomNumberGenerator.GetBytes(16), ct);
            Package hello = new(RandomNumberGenerator.GetBytes(Package.Big)) { Type = PackageType.Handshake, Name = session.MyName };
            for (int i = 0; i < HandshakeCount; i++)
            {
                await link.WriteAsync(hello, ct);
            }

            for (int untrusted = 0; ;)
            {
                Package p = await link.ReadAsync(ct) ?? throw new RefusedException($"{session.Peer} closed the connection during the handshake");
                switch (p.Type)
                {
                    case PackageType.Invalid:
                        throw new RefusedException($"the security key doesn't match {session.Peer}'s Mouse Without Borders key");
                    case PackageType.Handshake:
                        await link.AnswerAsync(p, ct);
                        answered++;
                        break;
                    case PackageType.HandshakeAck when Enumerable.Range(0, 4).All(i => p.Machine(i) == ~hello.Machine(i)):
                        (link.PeerId, link.PeerName) = (p.Src, p.Name);
                        link.log($"handshake: trusted after reading {link.received} bytes; it calls itself {p.Name}, id {p.Src}; answered {answered} of its handshakes");
                        link.Start();
                        return link;
                    case PackageType.HandshakeAck:
                        throw new RefusedException($"{session.Peer} answered the handshake wrongly");
                    default:
                        if (++untrusted >= HandshakeCount)
                        {
                            throw new RefusedException($"{session.Peer} sent {untrusted} packages before the handshake");
                        }

                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            await link.DisposeAsync();
            throw new RefusedException($"no complete handshake from {session.Peer} in time ({link.received} bytes read)");
        }
        catch (Exception e) when (e is not RefusedException)
        {
            await link.DisposeAsync();
            throw new RefusedException($"handshake with {session.Peer} failed: {e.Message}");
        }
        catch
        {
            await link.DisposeAsync();
            throw;
        }
    }

    /// <summary>Translates one of the cursor's frames into MWB packages. A frame it has no case for throws ArgumentException.</summary>
    public async Task SendAsync(Frame frame, CancellationToken ct = default)
    {
        switch (frame)
        {
            case Handoff h when driven:
                // Seamdesk's desk moved the cursor off this machine, or the user snapped it back: the peer's MWB takes it back
                // at that point (Receiver.cs:195-203, Event.cs:157-236, Helper.cs:99-113, as MWB's receiver sends it:
                // Receiver.cs:155-170). A point off its saved screens (a snap-back, or
                // a machine MWB can't reach) sends it to the centre of its box.
                driven = false;
                (int bx, int by) = h.Machine == s.Peer && s.Screens.Current.Any(m => m.Contains(h.Point)) ? s.Screens.Universal(h.Point) : (32767, 32767);
                await WriteAsync(Stamp(new Package(PackageType.NextMachine) { Des = PeerId, X = bx, Y = by, WheelDelta = (int)PeerId }), ct);
                break;
            case PointerMove m:
                if (Due(m, out Location? left))
                {
                    await WriteRawAsync(null, ct); // writes the move
                }
                else if (left is { } to)
                {
                    // Seamdesk's desk moved the cursor off the peer: MWB's controller decides that in absolute mode
                    // (Event.cs:73-94); the peer's MWB runs no edge test on an absolute move (Receiver.cs:180-184).
                    incoming.Writer.TryWrite(new Handoff(to.Machine.Name, to.Point));
                    await LetGoAsync(ct);
                }

                break; // otherwise held back (PaceAsync, or the next package written, sends it), or nowhere new to go
            case PointerButton b:
                // Without a move MWB sends "relative zero" (InputHook.cs:311-312); the peer clicks where its cursor is.
                (int flags, int data) = Button(b);
                await SendMouseAsync(Relative, Relative, flags, data, ct);
                break;
            case Wheel w:
                await SendMouseAsync(Relative, Relative, w.Horizontal ? 0x20E : 0x20A, w.Delta, ct); // WM_MOUSEHWHEEL, WM_MOUSEWHEEL
                break;
            case Key k:
                // InputSimulation.cs SendKey reads LLKHF_UP (0x80) and LLKHF_EXTENDED (0x01), and maps the scan code itself.
                Package key = new(PackageType.Keyboard) { Des = PeerId, Vk = k.VirtualKey, KeyFlags = (k.Extended ? 0x01 : 0) | (k.Down ? 0 : 0x80) };
                await WriteAsync(Stamp(key), ct);
                break;
            case Handoff h when h.Machine == s.Peer:
                // MWB's controller sends the arrival as one absolute move (InputHook.cs:266-281, Event.cs:77-87), then
                // every move absolute too: see Due.
                (int x, int y) = s.Screens.Universal(h.Point);
                lock (moveGate)
                {
                    (there, moved, holding) = (h.Point, false, true);
                }

                await SendMouseAsync(x, y, 0x200, 0, ct);
                break;
            case Handoff when holding:
                await LetGoAsync(ct);
                break;
            case ClipboardText c when c.Text != lastClipboard:
                await SendClipboardAsync(c.Text, ct);
                break;
            case Handoff or ClipboardText or Locked:
                break; // a handoff between other machines, text the peer has already, or a lock (MWB has no message for it)
            default:
                throw new ArgumentException($"unknown frame {frame}");
        }
    }

    /// <summary>
    /// The move about to be sent sums <paramref name="events"/> hook events, the first seen at <paramref name="since"/> (a
    /// timestamp of the session's clock): for the timing lines.
    /// </summary>
    public void Moving(long since, int events)
    {
        lock (moveGate)
        {
            moveSince = moveEvents == 0 ? since : moveSince;
            moveEvents += events;
        }
    }

    public ValueTask DisposeAsync()
    {
        Close("closed by Seamdesk");
        return ValueTask.CompletedTask;
    }

    private TimeSpan Now => s.Time.GetElapsedTime(origin);

    private static (int Flags, int Data) Button(PointerButton b) => b.Button switch
    {
        MouseButton.Left => (b.Down ? 0x201 : 0x202, 0),
        MouseButton.Right => (b.Down ? 0x204 : 0x205, 0),
        MouseButton.Middle => (b.Down ? 0x207 : 0x208, 0),
        _ => (b.Down ? 0x20B : 0x20C, b.Button == MouseButton.X1 ? 1 : 2), // WM_XBUTTON*, XBUTTON1/2 in WheelDelta
    };

    private void Start()
    {
        heard = s.Time.GetTimestamp();
        incoming.Writer.TryWrite(new Monitors(s.Screens.Current));
        _ = ReadLoop();
        _ = HeartbeatLoop();
        watch = s.Time.CreateTimer(_ => Watch(), null, WatchEvery, WatchEvery);
    }

    /// <summary>
    /// Moves the peer's cursor on Seamdesk's desk, as a Seamdesk peer does (the cursor's MoveHeld). True if the new position may
    /// go now: none is held back, and the pace allows a package. Otherwise <see cref="PaceAsync"/> writes it once the pace
    /// does. <paramref name="left"/> is where the cursor went if the desk moved it off the peer.
    /// <para>
    /// MWB's controller sends absolute positions unless "move mouse relatively" is on, and it is off by default
    /// (Event.cs:104-114). A relative move is injected as one (InputSimulation.cs:283-301), so the peer's pointer speed and
    /// acceleration apply again on top of this machine's, unevenly as the pace sums hook events; and the peer's MWB then
    /// runs its own edge test on it (Receiver.cs:147-170, MachineStuff.cs:239-257), whose matrix need not match Seamdesk's
    /// desk. An absolute position is injected as it is (Receiver.cs:180-184, InputSimulation.cs:160-171).
    /// </para>
    /// </summary>
    private bool Due(PointerMove m, out Location? left)
    {
        left = null;
        Desk desk = s.Desk();
        lock (moveGate)
        {
            if (!holding)
            {
                return false; // moves still on their way after the cursor left: they belong to the next holder
            }

            if (desk.Machines.FirstOrDefault(x => x.Name == s.Peer) is not { } peer)
            {
                left = new Location(desk.Machines.First(x => x.Name == s.Me), new Point(int.MinValue, int.MinValue)); // comes back where it left
                holding = false;
                return false;
            }

            Location to = desk.Move(new Location(peer, there), m.Dx, m.Dy);
            if (to.Machine.Name != s.Peer)
            {
                (left, holding) = (to, false);
                return false;
            }

            if (to.Point == there)
            {
                return false; // against an edge with nothing beyond it
            }

            (there, moved) = (to.Point, true);
            if (pacing)
            {
                return false;
            }

            if (Now >= nextMove - MovePace)
            {
                return true;
            }

            pacing = true;
        }

        _ = PaceAsync();
        return false;
    }

    /// <summary>Writes the held-back moves as the pace allows, until none are left.</summary>
    private async Task PaceAsync()
    {
        try
        {
            while (true)
            {
                TimeSpan wait;
                lock (moveGate)
                {
                    wait = nextMove - MovePace - Now;
                }

                if (wait > TimeSpan.Zero)
                {
                    // Task.Delay counts whole milliseconds and would make 0.4 ms no wait at all.
                    await (s.Delay ?? ((t, ct) => Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(t.TotalMilliseconds)), s.Time, ct)))(wait, stop.Token);
                }

                await WriteRawAsync(null, stop.Token);
                lock (moveGate)
                {
                    if (!moved)
                    {
                        pacing = false;
                        return;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Closed: Close logged why.
        }
    }

    /// <summary>The peer's cursor, if it moved since the last, as one absolute Mouse package with the timing lines' count; null if not.</summary>
    private (byte[] Plain, long Since, int Events)? TakeMove()
    {
        lock (moveGate)
        {
            if (!moved)
            {
                return null;
            }

            (int x, int y) = s.Screens.Universal(there);
            Package p = Stamp(new Package(PackageType.Mouse) { Des = PeerId, Src = s.MyId, X = x, Y = y, MouseFlags = 0x200 }); // WM_MOUSEMOVE
            (long since, int events) = (moveSince, moveEvents);
            moved = false;
            moveEvents = 0;
            nextMove = (nextMove > Now ? nextMove : Now) + MovePace;
            return (key.Seal(p), since, events);
        }
    }

    /// <summary>
    /// Every 100 ms: a link silent for <see cref="Silence"/> is closed (half-open), and while Seamdesk drives the peer, a peer
    /// that stops reading gets the cursor taken back, as a Seamdesk peer on its lock screen does (<see cref="Locked"/>), until
    /// it reads again or <see cref="StallHold"/> has passed. Its TCP window says how much it hasn't read.
    /// </summary>
    private void Watch()
    {
        lock (watchGate)
        {
            if (s.Time.GetElapsedTime(Interlocked.Read(ref heard)) >= Silence)
            {
                Close($"nothing from {s.Peer} for {Silence.TotalMinutes:F0} minutes, and its Mouse Without Borders sends a heartbeat at least every 5");
                return;
            }

            if (Tcp() is not { Window: var window })
            {
                return;
            }

            widest = Math.Max(widest, window);
            int unread = widest - window;
            if (!stalled && holding && unread >= StalledBytes)
            {
                (stalled, stalledAt) = (true, s.Time.GetTimestamp());
                driving($"stopped reading: {unread / 1024} KiB sent to it are unread (its TCP window fell from {widest / 1024} to {window / 1024} KiB); " +
                    $"the cursor comes back here and won't cross to it until it reads them, or for {StallHold.TotalSeconds:F0} s");
                incoming.Writer.TryWrite(new Locked(true));
                _ = LetGoAsync(stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);
            }
            else if (stalled && unread <= DrainedBytes)
            {
                stalled = false;
                driving("reading again: the cursor may cross to it");
                incoming.Writer.TryWrite(new Locked(false));
            }
            else if (stalled && s.Time.GetElapsedTime(stalledAt) >= StallHold)
            {
                stalled = false;
                driving($"{unread / 1024} KiB still unread after {StallHold.TotalSeconds:F0} s: the cursor may cross to it again, and comes back if it still doesn't read");
                incoming.Writer.TryWrite(new Locked(false));
            }
        }
    }

    private async Task ReadLoop()
    {
        try
        {
            while (await ReadAsync(stop.Token) is { } p)
            {
                Interlocked.Exchange(ref heard, s.Time.GetTimestamp());
                switch (p.Type)
                {
                    case PackageType.Invalid:
                        Close($"{s.Peer} sent a corrupt package");
                        return;
                    case PackageType.Handshake:
                        await AnswerAsync(p, stop.Token); // MWB answers every Handshake, trusted or not
                        break;
                    case PackageType.NextMachine when holding && p.Des == s.MyId && (uint)p.WheelDelta == s.MyId:
                        await HandBackAsync(p.X, p.Y);
                        break;
                    case PackageType.NextMachine when holding:
                        // MWB's controller cancels a switch to a machine it has no link to (Event.cs:171-177): so does Seamdesk.
                        log($"its Mouse Without Borders handed the cursor to machine id {(uint)p.WheelDelta}, not this one (id {s.MyId}); ignored");
                        break;
                    case PackageType.MachineSwitched when holding:
                        // Its MWB switched to this machine by itself (Event.cs:207-231), as its own edge test does (Event.cs:73-94)
                        // on an injected move it takes for the user's: its hook tells them apart by one flag, not the injected
                        // bit (InputHook.cs:205-227). It then parks its cursor, and standalone MWB may later put it back where
                        // that test fired.
                        log("its Mouse Without Borders switched to this machine by itself while Seamdesk drove it (its own edge test)");
                        break;
                    case PackageType.Hello:
                        await WriteAsync(Stamp(Named(PackageType.Heartbeat, All)), stop.Token); // Receiver.cs: Hello gets a heartbeat
                        break;
                    case PackageType.ByeBye:
                        Close($"{s.Peer} said goodbye");
                        return;
                    case PackageType.Mouse or PackageType.Keyboard when s.AllowControl && (p.Des == s.MyId || p.Des == All):
                        Driven(p);
                        break;
                    case PackageType.HideMouse when driven:
                        // The peer's MWB took the cursor elsewhere (Event.cs:211-215); MWB's receiver hides its cursor and
                        // releases every key (Receiver.cs:383-388), as leaving Held does.
                        driven = false;
                        incoming.Writer.TryWrite(new Handoff(s.Peer, default));
                        break;
                    case PackageType.ClipboardText when s.TakeClipboard:
                        // Clipboard.cs:308-340: 48-byte slices until ClipboardDataEnd.
                        peerClipboard ??= new MemoryStream();
                        if (peerClipboard.Length < MaxClipboard)
                        {
                            peerClipboard.Write(p.Bytes, Package.Big - SliceSize, SliceSize);
                        }

                        break;
                    case PackageType.ClipboardDataEnd when s.TakeClipboard && peerClipboard is not null:
                        if (peerClipboard.Length < MaxClipboard && PeerText(peerClipboard.ToArray()) is { } text)
                        {
                            lastClipboard = text; // so it is not sent straight back
                            incoming.Writer.TryWrite(new ClipboardText(text));
                        }

                        peerClipboard = null;
                        break;

                        // Everything else is the peer's business: drag and drop, images, its matrix.
                }
            }

            Close($"{s.Peer} closed the connection");
        }
        catch (Exception e)
        {
            Close(e.Message);
        }
    }

    /// <summary>
    /// The peer's MWB drives this machine. Core/Receiver.cs:118-193 at 48c4bad07 (unchanged since a9e1f0f20^): both
    /// coordinates at least 100000 away from zero (:147) is a relative move, or a click where the cursor is; anything
    /// else is absolute, 0..65535 over this machine's outer box (Class/InputSimulation.cs:160-171). Its first package
    /// after switching here is the arrival as an absolute move (Class/InputHook.cs:268-281). Seamdesk applies them as a Seamdesk
    /// controller's frames, so its own desk decides the edges in relative mode (MWB's receiver does: Receiver.cs:155-170).
    /// </summary>
    private void Driven(Package p)
    {
        Rect box = MwbScreens.Box(s.Desk().Machines.First(m => m.Name == s.Me).Monitors);
        bool relative = Math.Abs(p.X) >= Relative && Math.Abs(p.Y) >= Relative;
        Point? to = p.Type == PackageType.Mouse && !relative ? new Point(box.Left + Scale(p.X, box.Width), box.Top + Scale(p.Y, box.Height)) : null;
        if (!driven)
        {
            driven = true;
            at = to ?? new Point(box.Left + (box.Width / 2), box.Top + (box.Height / 2));
            incoming.Writer.TryWrite(new Handoff(s.Me, at));
        }

        if (p.Type == PackageType.Keyboard)
        {
            // InputSimulation.cs SendKey: LLKHF_UP (0x80) and LLKHF_EXTENDED (0x01); the scan code is mapped on injection.
            incoming.Writer.TryWrite(new Key((ushort)p.Vk, 0, (p.KeyFlags & 0x80) == 0, (p.KeyFlags & 0x01) != 0));
            return;
        }

        if (to is { } point && point != at)
        {
            incoming.Writer.TryWrite(new PointerMove(point.X - at.X, point.Y - at.Y));
            at = point;
        }
        else if (relative && p.MouseFlags == 0x200)
        {
            incoming.Writer.TryWrite(new PointerMove(p.X < 0 ? p.X + Relative : p.X - Relative, p.Y < 0 ? p.Y + Relative : p.Y - Relative));
        }

        Frame? input = p.MouseFlags switch
        {
            0x201 or 0x202 => new PointerButton(MouseButton.Left, p.MouseFlags == 0x201),
            0x204 or 0x205 => new PointerButton(MouseButton.Right, p.MouseFlags == 0x204),
            0x207 or 0x208 => new PointerButton(MouseButton.Middle, p.MouseFlags == 0x207),
            0x20B or 0x20C => new PointerButton(p.WheelDelta == 2 ? MouseButton.X2 : MouseButton.X1, p.MouseFlags == 0x20B),
            0x20A or 0x20E => new Wheel(p.WheelDelta, p.MouseFlags == 0x20E),
            _ => null,
        };
        if (input is not null)
        {
            incoming.Writer.TryWrite(input);
        }
    }

    private static int Scale(int universal, int size) => Math.Clamp((int)((long)universal * size / 65535), 0, size - 1);

    /// <summary>
    /// The text in the peer's clipboard data (Clipboard.cs SetClipboardData, :1101-1180 at 48c4bad07): deflated UTF-16,
    /// "TXT", "RTF" and "HTM" sections each ended by the separator. Only the text is taken; null if there is none.
    /// </summary>
    internal static string? PeerText(byte[] data)
    {
        try
        {
            using DeflateStream inflate = new(new MemoryStream(data), CompressionMode.Decompress);
            using MemoryStream plain = new();
            byte[] buffer = new byte[81920];
            for (int n; (n = inflate.Read(buffer)) > 0;)
            {
                plain.Write(buffer, 0, n);
                if (plain.Length > MaxPeerText)
                {
                    return null; // Known limit: a cap, not a stream; raise it if 8 million characters of text are too few
                }
            }

            string all = Encoding.Unicode.GetString(plain.GetBuffer(), 0, (int)plain.Length & ~1);
            return all.Split(TextTypeSeparator, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(t => t.StartsWith("TXT", StringComparison.OrdinalIgnoreCase))?[3..];
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private async Task HandBackAsync(int x, int y)
    {
        HandBack back = s.Screens.HandBack(s.Desk(), s.Me, s.Peer, x, y);
        if (back.NewLayout is { } layout)
        {
            incoming.Writer.TryWrite(new Monitors(layout));
        }
        else if (back.Unmatched)
        {
            incoming.Writer.TryWrite(new Notice($"{s.Peer}'s screens changed: check layout"));
        }

        // A point off this machine's monitors makes the cursor come back where it left.
        incoming.Writer.TryWrite(new Handoff(s.Me, back.To ?? new Point(int.MinValue, int.MinValue)));
        await LetGoAsync(stop.Token);
    }

    /// <summary>MWB's controller tells the machine it leaves to hide its cursor and release keys (Event.cs:184-188).</summary>
    private async Task LetGoAsync(CancellationToken ct)
    {
        holding = false;
        await WriteAsync(Stamp(Named(PackageType.HideMouse, PeerId)), ct);
    }

    private async Task HeartbeatLoop()
    {
        try
        {
            while (true)
            {
                await WriteAsync(Stamp(Named(PackageType.Heartbeat, All)), stop.Token); // SocketStuff.cs:1451, Common.cs:509-512
                await Task.Delay(HeartbeatInterval, s.Time, stop.Token);
            }
        }
        catch (Exception e)
        {
            Close(e.Message);
        }
    }

    /// <summary>Clipboard.cs:169-222 with FormHelper.cs:187: "TXT" + text + separator, UTF-16, deflated, in slices.</summary>
    private async Task SendClipboardAsync(string text, CancellationToken ct)
    {
        using MemoryStream compressed = new();
        using (DeflateStream deflate = new(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            deflate.Write(Encoding.Unicode.GetBytes("TXT" + text + TextTypeSeparator));
        }

        if (compressed.Length > MaxClipboard)
        {
            return; // Known limit: big clipboards need MWB's clipboard socket; add it if 1 MB of compressed text is too little
        }

        lastClipboard = text;
        byte[] data = compressed.ToArray();
        for (int i = 0; i < data.Length; i += SliceSize)
        {
            Package slice = new(PackageType.ClipboardText) { Des = All };
            data.AsSpan(i, Math.Min(SliceSize, data.Length - i)).CopyTo(slice.Bytes.AsSpan(Package.Big - SliceSize));
            await WriteAsync(Stamp(slice), ct);
        }

        await WriteAsync(Stamp(new Package(PackageType.ClipboardDataEnd) { Des = All }), ct);
    }

    private Task SendMouseAsync(int x, int y, int flags, int data, CancellationToken ct) =>
        WriteAsync(Stamp(new Package(PackageType.Mouse) { Des = PeerId, X = x, Y = y, MouseFlags = flags, WheelDelta = data }), ct);

    private Package Named(PackageType type, uint des) => new(type) { Des = des, Name = s.MyName };

    /// <summary>Common.cs SkSend numbers every package it sends.</summary>
    private Package Stamp(Package p)
    {
        p.Id = Interlocked.Increment(ref nextId);
        return p;
    }

    /// <summary>The handshake answer: the words inverted, our id and name (SocketStuff.cs:1371-1385).</summary>
    private Task AnswerAsync(Package handshake, CancellationToken ct)
    {
        Package ack = new(handshake.Bytes) { Type = PackageType.HandshakeAck, Src = 0, Name = s.MyName };
        for (int i = 0; i < 4; i++)
        {
            ack.SetMachine(i, ~handshake.Machine(i));
        }

        return WriteAsync(ack, ct);
    }

    /// <summary>SocketStuff.cs TcpSend: a package without a source gets ours.</summary>
    private Task WriteAsync(Package p, CancellationToken ct)
    {
        if (p.Src == 0)
        {
            p.Src = s.MyId;
        }

        return WriteRawAsync(key.Seal(p), ct, p.Type);
    }

    /// <summary>
    /// Writes the moves not yet written, if any, then <paramref name="plain"/>, if any: every package goes after the moves
    /// summed before it, so a click lands where the pointer is.
    /// </summary>
    private async Task WriteRawAsync(byte[]? plain, CancellationToken ct, PackageType? type = null)
    {
        bool queued = writeLock.CurrentCount == 0;
        await writeLock.WaitAsync(ct);
        try
        {
            if (TakeMove() is { } move)
            {
                await EncryptAsync(move.Plain, PackageType.Mouse, queued, ct);
                if (move.Events > 0)
                {
                    timing.Moved(move.Since, move.Events);
                }
            }

            if (plain is not null)
            {
                await EncryptAsync(plain, type, queued, ct);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Close(e.Message);
            throw;
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task EncryptAsync(byte[] plain, PackageType? type, bool queued, CancellationToken ct)
    {
        long start = s.Time.GetTimestamp();
        byte[] cipher = new byte[plain.Length];
        encryptor.TransformBlock(plain, 0, plain.Length, cipher, 0);
        await stream.WriteAsync(cipher, ct);
        await stream.FlushAsync(ct);
        if (type is { } t)
        {
            timing.Wrote(t, s.Time.GetElapsedTime(start).TotalMilliseconds, queued);
        }
    }

    /// <summary>
    /// One package, or null at the end of the stream (SocketStuff.cs TcpReceiveData). A package that fails the magic
    /// number or checksum comes back as <see cref="PackageType.Invalid"/>, without reading its second half.
    /// </summary>
    private async Task<Package?> ReadAsync(CancellationToken ct)
    {
        byte[] bytes = new byte[Package.Big];
        if (!await ReadDecryptedAsync(bytes.AsMemory(0, Package.Small), ct))
        {
            return null;
        }

        if (!key.Open(bytes))
        {
            return new Package(PackageType.Invalid);
        }

        Package p = new(bytes);
        return !p.IsBig || await ReadDecryptedAsync(p.Bytes.AsMemory(Package.Small), ct) ? p : null;
    }

    /// <summary>
    /// Reads the peer's stream start: the old format's random block and first package, or the current format's header
    /// and the same. Whichever opens with this key is the peer's format: the old one as PowerToys or as standalone MWB
    /// derives the key, or the current one. Nothing has been written yet: every MWB writes its start before it reads
    /// (SocketStuff.cs MainTCPRoutine, TcpSk.EncryptedStream; the same in standalone MWB 2.2.1).
    /// </summary>
    private async Task DetectAsync(CancellationToken ct)
    {
        byte[] start = new byte[MwbKey.HeaderSize + 16 + Package.Small];
        for (int n; received < start.Length; received += n)
        {
            if ((n = await stream.ReadAsync(start.AsMemory((int)received), ct)) == 0)
            {
                throw new RefusedException($"{s.Peer} closed the connection after {received} of the {start.Length} bytes that start its stream");
            }
        }

        byte[] header = start[..MwbKey.HeaderSize];
        (MwbFormat Format, MwbKey Key)? opened = Opens(s.Key, null, start) ? (MwbFormat.Old, s.Key)
            : Opens(s.Key.Standalone, null, start) ? (MwbFormat.Old, s.Key.Standalone)
            : Opens(s.Key, header, start[MwbKey.HeaderSize..]) ? (MwbFormat.Current, s.Key) : null;
        MwbFormat? seen = opened?.Format;
        log($"handshake: read the {start.Length}-byte stream start; it opens as {opened switch
        {
            null => "nothing known: not the old format (PowerToys or standalone key) nor the current one",
            (MwbFormat.Old, { IsStandalone: true }) => "the old format, standalone MWB 2.x key",
            (MwbFormat.Old, _) => "the old format, PowerToys key (0.100 or earlier)",
            _ => "the current format (PowerToys 0.101 or later)",
        }}");
        string? refusal = (s.Format, seen) switch
        {
            (_, null) => $"the security key doesn't match {s.Peer}'s Mouse Without Borders key",
            (MwbFormat.Old, MwbFormat.Current) => $"{s.Peer} runs PowerToys 0.101 or later: choose Current or Auto for it in Seamdesk's settings",
            (MwbFormat.Current, MwbFormat.Old) => $"{s.Peer} runs Mouse Without Borders older than PowerToys 0.101: choose Old or Auto for it in Seamdesk's settings",
            (null, MwbFormat.Old) when s.CurrentSeen =>
                $"{s.Peer} now offers the old Mouse Without Borders format after using 0.101 or later; Seamdesk won't fall back on its own: choose Old for it in Seamdesk's settings",
            _ => null,
        };
        if (refusal is not null)
        {
            throw new RefusedException(refusal);
        }

        (Format, key) = opened!.Value;
        s.CurrentSeen |= Format == MwbFormat.Current;
        (decryptor, replay) = Format == MwbFormat.Current ? (key.Decryptor(header), start[MwbKey.HeaderSize..]) : (key.Decryptor(), start);
        await ReadDecryptedAsync(new byte[16], ct); // the random block
    }

    /// <summary>Whether a random block and the package after it decrypt to <paramref name="k"/>'s magic and checksum.</summary>
    private static bool Opens(MwbKey k, byte[]? header, byte[] cipher)
    {
        byte[] plain = new byte[16 + Package.Small];
        k.Decryptor(header).TransformBlock(cipher, 0, plain.Length, plain, 0);
        return k.Open(plain[16..]);
    }

    private async Task<bool> ReadDecryptedAsync(Memory<byte> buffer, CancellationToken ct)
    {
        byte[] cipher = new byte[buffer.Length];
        int have = Math.Min(replay.Length, cipher.Length);
        replay.AsSpan(0, have).CopyTo(cipher);
        replay = replay[have..];
        if (have < cipher.Length)
        {
            int read = await stream.ReadAtLeastAsync(cipher.AsMemory(have), cipher.Length - have, throwOnEndOfStream: false, ct);
            received += read;
            if (read < cipher.Length - have)
            {
                return false;
            }
        }

        byte[] plain = new byte[cipher.Length];
        decryptor.TransformBlock(cipher, 0, cipher.Length, plain, 0);
        plain.CopyTo(buffer);
        return true;
    }

    /// <summary>
    /// The round trip and the peer's TCP window from the OS, or null where it says neither: Windows' <c>SIO_TCP_INFO</c>
    /// (<c>TCP_INFO_v0</c>: <c>RttUs</c> at 20, <c>SndWnd</c> at 36, mstcpip.h) or Linux's <c>TCP_INFO</c>
    /// (<c>tcpi_rtt</c> at 68, <c>tcpi_snd_wnd</c> at 228, linux/tcp.h). The peer's window is what its receive buffer
    /// has free: it shrinks by what has arrived there and its MWB hasn't read yet (MWB asks for 320000 bytes,
    /// Class/SocketStuff.cs:1284). A long round trip with the window open is a queue on the network instead.
    /// </summary>
    private (double RttMs, int Window)? Tcp()
    {
        try
        {
            if (stream is not NetworkStream { Socket: var socket })
            {
                return null;
            }

            byte[] info = new byte[256];
            (int rtt, int window) = OperatingSystem.IsWindows() && socket.IOControl(unchecked((int)0xD8000027), new byte[4], info) >= 40 ? (20, 36)
                : OperatingSystem.IsLinux() && socket.GetRawSocketOption(6, 11, info) >= 232 ? (68, 228) // IPPROTO_TCP, TCP_INFO
                : (-1, -1);
            return rtt < 0 ? null : (BitConverter.ToUInt32(info, rtt) / 1000.0, (int)Math.Min(BitConverter.ToUInt32(info, window), int.MaxValue));
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private void Close(string reason)
    {
        if (closed.TrySetResult(reason))
        {
            log($"closed: {reason}");
            watch?.Dispose();
            stop.Cancel();
            incoming.Writer.TryComplete();
            stream.Dispose();
        }
    }
}
