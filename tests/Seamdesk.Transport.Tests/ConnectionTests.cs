using System.Net.Sockets;
using System.Collections.Concurrent;
using Seamdesk.Core;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Seamdesk.Transport.Tests;

public class ConnectionTests
{
    [Fact]
    public async Task Listener_refuses_a_certificate_it_has_not_pinned()
    {
        // A is paired with B. Charlie claims B's name with his own key; he has pinned A, so only A's check stands between.
        LocalNode a = Kit.Local("alpha.localhost");
        LocalNode bravo = Kit.Local("bravo.localhost");
        LocalNode charlie = Kit.Local("bravo.localhost");
        a.Pins.Add(Kit.PinOf(bravo));
        charlie.Pins.Add(Kit.PinOf(a));
        await using Node nodeA = Kit.Start(a);
        await using Node nodeC = Kit.Start(charlie);
        nodeC.PeerPort = nodeA.Port;

        RefusedException e = await Assert.ThrowsAsync<RefusedException>(() => nodeC.DialAsync(Kit.PinOf(a), CancellationToken.None));

        Assert.Equal("not paired with this machine (unknown certificate): pair again", e.Message);
        Assert.Null(nodeA[Kit.PinOf(bravo)].Current);
    }

    /// <summary>The dialler reads why it was refused off the wire; the listener writes it to its own log.</summary>
    [Theory]
    [InlineData("unknown certificate", "not paired with this machine (unknown certificate): pair again")]
    [InlineData("wrong pairing code", "wrong pairing code")]
    public async Task Listener_logs_why_it_refused_a_connection(string row, string reason)
    {
        ConcurrentQueue<string> log = [];
        await using Node a = Kit.Start(Kit.Local("alpha.localhost"), log: log.Enqueue);
        await using Node b = Kit.Start(Kit.Local("bravo.localhost"));
        b.PeerPort = a.Port;
        string code = a.ShowPairingCode().Code;

        await Assert.ThrowsAsync<RefusedException>(() => row == "wrong pairing code"
            ? b.PairAsync("127.0.0.1", ((int.Parse(code) + 1) % 100_000_000).ToString("D8"))
            : b.DialAsync(Kit.PinOf(a.Local) with { Name = "127.0.0.1" }, CancellationToken.None));

        await Kit.Eventually(() => !log.IsEmpty);
        Assert.Equal([$"seamdesk: refused a connection from 127.0.0.1: {reason}"], log);
    }

    /// <summary>
    /// The listener refuses before reading what the dialler already sent. Closing with those bytes unread sends RST, and
    /// Windows then drops the Refuse from the dialler's buffer before it reads it ("forcibly closed" instead of the reason;
    /// Linux keeps it, so this fails only on Windows). A's clock is frozen so no timeout of A's is what closes.
    /// </summary>
    [Fact]
    public async Task A_refused_dialler_reads_the_reason_even_when_it_sent_bytes_the_listener_never_read()
    {
        await using Node a = Kit.Start(Kit.Local("alpha.localhost", new FakeTimeProvider()));
        await using var ssl = await Kit.RawDialAsync(Kit.Local("charlie.localhost"), a.Port);

        await ssl.WriteAsync(new byte[32 * 1024]);
        await Task.Delay(500);

        Assert.Equal([new Refuse("not paired with this machine (unknown certificate): pair again", "0.1.0")], await Kit.ReadUntilClosedAsync(ssl));
    }

    /// <summary>
    /// Connections still in their handshake are capped per source address and in total, so one LAN host can't hold them
    /// all. The stalled ones come from other loopback addresses; B, from 127.0.0.1, still gets in. A's clock is frozen, so
    /// its handshake timeout never frees a stalled one.
    /// </summary>
    [Fact]
    public async Task Handshakes_in_progress_are_capped_per_source_and_in_total()
    {
        ConcurrentQueue<string> log = [];
        LocalNode a = Kit.Local("alpha.localhost", new FakeTimeProvider());
        LocalNode b = Kit.Local("bravo.localhost");
        a.Pins.Add(Kit.PinOf(b));
        b.Pins.Add(Kit.PinOf(a));
        await using Node nodeA = Kit.Start(a, log: log.Enqueue);
        await using Node nodeB = Kit.Start(b);
        nodeB.PeerPort = nodeA.Port;
        List<Socket> stalled = [];
        try
        {
            for (int i = 0; i < Node.MaxHandshakesPerSource; i++)
            {
                stalled.Add(await Kit.StallAsync("127.0.0.2", nodeA.Port));
            }

            Assert.True(await Kit.ClosedSoonAsync(await Kit.StallAsync("127.0.0.2", nodeA.Port)), "one source past its cap");
            await using (await nodeB.DialAsync(Kit.PinOf(a), CancellationToken.None))
            {
            }

            for (int i = Node.MaxHandshakesPerSource; i < Node.MaxHandshakes; i++)
            {
                stalled.Add(await Kit.StallAsync($"127.0.0.{3 + (i / Node.MaxHandshakesPerSource)}", nodeA.Port));
            }

            Assert.True(await Kit.ClosedSoonAsync(await Kit.StallAsync("127.0.0.100", nodeA.Port)), "past the total cap");
            Assert.Contains("seamdesk: refused a connection from 127.0.0.2: too many connections still in their handshake", log);
            Assert.Contains("seamdesk: refused a connection from 127.0.0.100: too many connections still in their handshake", log);
        }
        finally
        {
            stalled.ForEach(s => s.Dispose());
        }
    }

    [Fact]
    public void Repeated_log_lines_are_counted_in_bounded_memory()
    {
        // Each refused source address makes its own line, and a host on the LAN can use many (IPv6 link-local): past 1000
        // distinct lines the counts start over, so a line seen before is news again.
        Repeats said = new();
        Assert.Equal("x", said.Next("x"));
        Assert.Null(said.Next("x"));

        for (int i = 0; i < 1000; i++)
        {
            said.Next($"refused a connection from fe80::{i:x}");
        }

        Assert.Equal("x", said.Next("x"));
    }

    [Fact]
    public async Task Dialer_refuses_a_peer_whose_certificate_changed()
    {
        // B paired with A, but A's address now answers with another key (reinstall or impostor) that has pinned B.
        LocalNode a = Kit.Local("alpha.localhost");
        LocalNode b = Kit.Local("bravo.localhost");
        LocalNode impostor = Kit.Local("alpha.localhost");
        b.Pins.Add(Kit.PinOf(a));
        impostor.Pins.Add(Kit.PinOf(b));
        await using Node nodeImpostor = Kit.Start(impostor);
        await using Node nodeB = Kit.Start(b);
        nodeB.PeerPort = nodeImpostor.Port;

        RefusedException e = await Assert.ThrowsAsync<RefusedException>(() => nodeB.DialAsync(Kit.PinOf(a), CancellationToken.None));

        Assert.Equal("alpha.localhost's identity changed: pair again", e.Message);
        Assert.Null(nodeImpostor[Kit.PinOf(b)].Current);
    }

    [Fact]
    public async Task Unpairing_closes_the_link_stops_dialling_and_refuses_the_peer()
    {
        // A's clock is fake: after the unpair it jumps past any backoff, so a supervisor still running would redial B.
        FakeTimeProvider time = new();
        LocalNode a = Kit.Local("alpha.localhost", time);
        LocalNode b = Kit.Local("bravo.localhost");
        a.Pins.Add(Kit.PinOf(b));
        b.Pins.Add(Kit.PinOf(a));
        await using Node nodeA = Kit.Start(a);
        await using Node nodeB = Kit.Start(b);
        (nodeA.PeerPort, nodeB.PeerPort) = (nodeB.Port, nodeA.Port);
        await Kit.Eventually(() => nodeB[Kit.PinOf(a)].Current is not null);
        Connection link = nodeB[Kit.PinOf(a)].Current!;

        nodeA.Unpair(Kit.PinOf(b));

        await link.Closed.WaitAsync(Kit.Patience);
        Assert.Empty(a.Pins.All);
        RefusedException e = await Assert.ThrowsAsync<RefusedException>(() => nodeB.DialAsync(Kit.PinOf(a), CancellationToken.None));
        Assert.Equal("not paired with this machine (unknown certificate): pair again", e.Message);
        await Task.Delay(200);
        time.Advance(TimeSpan.FromSeconds(31));
        await Task.Delay(1000);
        Assert.Null(nodeB[Kit.PinOf(a)].Current);
    }

    /// <summary>What a listener answers to a pinned peer's opening frames, read off the wire until it closes.</summary>
    [Theory]
    [InlineData("input before Hello")]
    [InlineData("input after Hello, before Ready")]
    [InlineData("protocol mismatch")]
    public async Task Listener_closes_on_frames_out_of_order_and_on_version_mismatch(string row)
    {
        // A's clock is frozen so its handshake timeout cannot be what closes the connection.
        LocalNode a = Kit.Local("alpha.localhost", new FakeTimeProvider());
        LocalNode c = Kit.Local("charlie.localhost", protocol: row == "protocol mismatch" ? 2 : 1, appVersion: "0.2.0");
        a.Pins.Add(Kit.PinOf(c));
        await using Node nodeA = Kit.Start(a);
        Hello helloA = new(1, "0.1.0", "alpha.localhost");
        (Frame[] send, Frame[] expected) = row switch
        {
            "input before Hello" => (new Frame[] { new PointerMove(5, 5) }, new Frame[] { helloA }),
            "input after Hello, before Ready" => ([c.Hello, new PointerMove(5, 5)], [helloA, new ReadyFrame()]),
            _ => ([c.Hello], [helloA, new Refuse("charlie.localhost runs Seamdesk 0.2.0 (protocol 2), alpha.localhost runs Seamdesk 0.1.0 (protocol 1): update one of them", "0.1.0")]),
        };

        await using var ssl = await Kit.RawDialAsync(c, nodeA.Port);
        foreach (Frame frame in send)
        {
            await FrameCodec.WriteAsync(ssl, frame);
        }

        Assert.Equal(expected, await Kit.ReadUntilClosedAsync(ssl));
        Assert.Null(nodeA[Kit.PinOf(c)].Current);
    }

    [Fact]
    public async Task Crossed_dials_settle_on_the_one_dialled_by_the_lower_machine_id()
    {
        LocalNode a = Kit.Local("alpha.localhost");
        LocalNode b = Kit.Local("bravo.localhost");
        a.Pins.Add(Kit.PinOf(b));
        b.Pins.Add(Kit.PinOf(a));
        await using Node nodeA = Kit.Start(a);
        await using Node nodeB = Kit.Start(b);
        nodeB.PeerPort = nodeA.Port;
        Supervisor atA = nodeA[Kit.PinOf(b)], atB = nodeB[Kit.PinOf(a)];

        // B's dial lands first and both ends take it.
        nodeB.KickAll();
        await Kit.Eventually(() => atA.Current is { DialedBy: "bravo.localhost" } && atB.Current is { DialedBy: "bravo.localhost" });
        Connection first = atA.Current!;

        // Then A's own dial, already in flight, completes: B is offered it on accept, A when its dial returns.
        nodeA.PeerPort = nodeB.Port;
        atA.Offer(await nodeA.DialAsync(Kit.PinOf(b), CancellationToken.None));

        await Kit.Eventually(() => atA.Current is { DialedBy: "alpha.localhost" } && atB.Current is { DialedBy: "alpha.localhost" });
        await first.Closed.WaitAsync(Kit.Patience); // whichever end closed it first
        Assert.True(await atA.SendAsync(new Locked(true)));
        Assert.Equal(new Locked(true), await atB.Current!.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));
    }

    [Fact]
    public async Task Clipboard_text_goes_before_the_handoff_and_a_frame_the_wire_cannot_carry_throws_without_dropping_the_link()
    {
        LocalNode a = Kit.Local("alpha.localhost");
        LocalNode b = Kit.Local("bravo.localhost");
        a.Pins.Add(Kit.PinOf(b));
        b.Pins.Add(Kit.PinOf(a));
        await using Node nodeA = Kit.Start(a);
        await using Node nodeB = Kit.Start(b);
        nodeB.PeerPort = nodeA.Port;
        nodeB.KickAll();
        Supervisor atA = nodeA[Kit.PinOf(b)];
        await Kit.Eventually(() => atA.Current is not null && nodeB[Kit.PinOf(a)].Current is not null);
        Connection atB = nodeB[Kit.PinOf(a)].Current!;

        // The crossing as App sends it to a peer that gets this machine's clipboard.
        string text = "copied on alpha, ünïcödé\r\n" + new string('x', 5000);
        Assert.True(await atA.SendAsync(new ClipboardText(text)));
        Assert.True(await atA.SendAsync(new Handoff("bravo.localhost", new Point(10, 20))));
        Assert.Equal(new ClipboardText(text), await atB.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));
        Assert.Equal(new Handoff("bravo.localhost", new Point(10, 20)), await atB.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));

        // A frame the Seamdesk wire has no type for, or clipboard text over 64 KiB, throws; the link still carries the next frame.
        await Assert.ThrowsAsync<ArgumentException>(() => atA.SendAsync(new Unknown()));
        await Assert.ThrowsAsync<ArgumentException>(() => atA.SendAsync(new ClipboardText(new string('x', FrameCodec.MaxLength))));
        Assert.True(await atA.SendAsync(new Handoff("alpha.localhost", new Point(0, 0))));
        Assert.Equal(new Handoff("alpha.localhost", new Point(0, 0)), await atB.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));
        Assert.False(atB.Closed.IsCompleted);
    }

    [Fact]
    public async Task Silent_peer_is_dropped_after_three_missed_pings_and_redialled()
    {
        // A's clock never moves, so A never pings: once A stops sending input, B hears nothing.
        LocalNode a = Kit.Local("alpha.localhost", new FakeTimeProvider());
        FakeTimeProvider timeB = new();
        LocalNode b = Kit.Local("bravo.localhost", timeB);
        a.Pins.Add(Kit.PinOf(b));
        b.Pins.Add(Kit.PinOf(a));
        await using Node nodeA = Kit.Start(a);
        Node nodeB = new(b, port: 0) { PeerPort = nodeA.Port, Random = () => 0.5 };
        Channel<(LinkState State, TimeSpan At)> states = Channel.CreateUnbounded<(LinkState, TimeSpan)>();
        DateTimeOffset start = timeB.GetUtcNow();
        nodeB.LinkChanged += (_, state) => states.Writer.TryWrite((state, timeB.GetUtcNow() - start));
        await using (nodeB)
        {
            nodeB.Start();
            await NextAsync(states, LinkStatus.Connecting);
            await NextAsync(states, LinkStatus.Connected);
            await Kit.Eventually(() => nodeA[Kit.PinOf(b)].Current is not null);
            Connection fromA = nodeA[Kit.PinOf(b)].Current!;
            Connection atB = nodeB[Kit.PinOf(a)].Current!;

            // A frame every 2 s keeps B's connection alive well past 6 s: any frame counts as liveness.
            for (int i = 0; i < 5; i++)
            {
                await fromA.SendAsync(new PointerMove(i, 0));
                Assert.Equal(new PointerMove(i, 0), await atB.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));
                timeB.Advance(Connection.PingInterval);
            }

            // Last frame at 8 s. Ticks at 12 s (4 s silent) keep it; the tick at 14 s (6 s silent) drops it.
            await Task.Delay(200);
            timeB.Advance(Connection.PingInterval);
            await Task.Delay(200);
            timeB.Advance(Connection.PingInterval);
            (LinkState dropped, TimeSpan at) = await NextAsync(states, LinkStatus.Waiting);
            Assert.Equal(TimeSpan.FromSeconds(14), at);
            Assert.Equal(new LinkState(LinkStatus.Waiting, TimeSpan.FromSeconds(1), "no reply for 6 s"), dropped);
            Assert.False(await nodeB[Kit.PinOf(a)].SendAsync(new PointerMove(1, 1)));

            // The supervisor did not disarm: one second later it dials again and A takes it.
            timeB.Advance(TimeSpan.FromSeconds(1));
            await NextAsync(states, LinkStatus.Connecting);
            await NextAsync(states, LinkStatus.Connected);
            Assert.NotSame(atB, nodeB[Kit.PinOf(a)].Current);
        }
    }

    private static async Task<(LinkState, TimeSpan)> NextAsync(Channel<(LinkState State, TimeSpan At)> states, LinkStatus status)
    {
        (LinkState State, TimeSpan At) next = await states.Reader.ReadAsync().AsTask().WaitAsync(Kit.Patience);
        Assert.Equal(status, next.State.Status);
        return next;
    }

    private sealed record Unknown : Frame;
}
