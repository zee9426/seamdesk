using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Seamdesk.Core;
using Seamdesk.Transport;
using Microsoft.Extensions.Time.Testing;

namespace Seamdesk.Mwb.Tests;

public class LinkTests
{
    private const string Separator = "{4CFF57F7-BEDD-43d5-AE8F-27A61E886F2F}";

    [Fact]
    public async Task No_input_reaches_the_peer_before_its_handshake_answer()
    {
        // The peer sends its handshake but never answers Seamdesk's. Frozen time: the 10 s handshake deadline never fires.
        using var listener = Kit.Listen();
        (MwbNode node, _) = Kit.Start(Kit.Session(new FakeTimeProvider()), "127.0.0.1", Kit.Port(listener));
        await using (node)
        await using (FakeMwb fake = await FakeMwb.AcceptAsync(listener, Kit.Key))
        {
            // The fake trusts Seamdesk on Seamdesk's first answer; Seamdesk never gets one, so it does not trust the fake.
            await fake.HandshakeAsync(answer: false).WaitAsync(Kit.Patience);

            Assert.False(await node["peer"]!.SendAsync(new PointerMove(5, 5)));
            Assert.False(await node["peer"]!.SendAsync(new Key(0x41, 0x1E, true, false)));

            // After its ten Handshakes Seamdesk sent only its answers to the fake's ten, and nothing else.
            for (int i = 1; i < 10; i++)
            {
                Assert.Equal(PackageType.HandshakeAck, (await fake.ReadAsync().WaitAsync(Kit.Patience))!.Type);
            }

            await Assert.ThrowsAsync<TimeoutException>(() => fake.ReadAsync().WaitAsync(TimeSpan.FromMilliseconds(500)));
        }
    }

    [Theory]
    [InlineData(MwbFormat.Old)]
    [InlineData(MwbFormat.Current)]
    public async Task A_peer_with_another_key_is_dropped_with_a_plain_reason_and_logged_without_either_key(MwbFormat format)
    {
        using var listener = Kit.Listen();
        Channel<string> log = Channel.CreateUnbounded<string>();
        (MwbNode node, Channel<LinkState> states) = Kit.Start(Kit.Session(), "127.0.0.1", Kit.Port(listener), log);
        await using (node)
        await using (FakeMwb fake = await FakeMwb.AcceptAsync(listener, new MwbKey("SomeOtherMadeUpKey-9876"), format))
        {
            _ = fake.HandshakeAsync(); // its packages fail Seamdesk's magic number and checksum in either format

            LinkState waiting = await Kit.Until(states, LinkStatus.Waiting);
            Assert.Equal("the security key doesn't match peer's Mouse Without Borders key", waiting.Reason);
            Assert.Null(node["peer"]!.Current);

            // seamdesk.log: the dial, what the stream start opened as, and the same reason.
            List<string> lines = await Kit.UntilLogged(log, "mwb peer: refused: the security key doesn't match peer's Mouse Without Borders key");
            Assert.Contains($"mwb peer: dialled 127.0.0.1:{Kit.Port(listener)}: connected", lines);
            Assert.Contains("mwb peer: handshake: read the 80-byte stream start; it opens as nothing known: not the old format (PowerToys or standalone key) nor the current one", lines);
            Assert.DoesNotContain(lines, l => l.Contains("NotARealKey") || l.Contains("MadeUpKey"));
        }
    }

    [Theory]
    [InlineData(MwbFormat.Old)]
    [InlineData(MwbFormat.Current)]
    public async Task Auto_answers_in_the_format_the_peer_speaks_and_says_which(MwbFormat format)
    {
        using var listener = Kit.Listen();
        (MwbNode node, FakeMwb fake, MwbLink link) = await Kit.ConnectedAsync(listener, format: format);
        await using (node)
        await using (fake)
        {
            Assert.Equal(format, link.Format);
            await link.SendAsync(new Handoff("peer", new Point(1000, 0)));
            Assert.Equal((PackageType.Mouse, 14635, 22), await fake.NextAsync() is var p ? (p.Type, p.X, p.Y) : default);
        }
    }

    [Theory]
    [InlineData(MwbFormat.Old, false, MwbFormat.Current, "peer runs PowerToys 0.101 or later: choose Current or Auto for it in Seamdesk's settings")]
    [InlineData(MwbFormat.Current, false, MwbFormat.Old, "peer runs Mouse Without Borders older than PowerToys 0.101: choose Old or Auto for it in Seamdesk's settings")]
    [InlineData(null, true, MwbFormat.Old, "peer now offers the old Mouse Without Borders format after using 0.101 or later; Seamdesk won't fall back on its own: choose Old for it in Seamdesk's settings")]
    public async Task A_format_other_than_the_chosen_or_the_one_auto_saw_is_refused_with_the_reason(MwbFormat? chosen, bool currentSeen, MwbFormat peer, string reason)
    {
        using var listener = Kit.Listen();
        (MwbNode node, Channel<LinkState> states) = Kit.Start(Kit.Session() with { Format = chosen, CurrentSeen = currentSeen }, "127.0.0.1", Kit.Port(listener));
        await using (node)
        await using (FakeMwb fake = await FakeMwb.AcceptAsync(listener, Kit.Key, peer))
        {
            _ = fake.HandshakeAsync();

            Assert.Equal(reason, (await Kit.Until(states, LinkStatus.Waiting)).Reason);
            Assert.Null(node["peer"]!.Current);
        }
    }

    [Fact]
    public async Task Auto_that_saw_the_current_format_refuses_the_old_one_on_the_next_link()
    {
        using var listener = Kit.Listen();
        (MwbNode node, Channel<LinkState> states) = Kit.Start(Kit.Session(), "127.0.0.1", Kit.Port(listener));
        await using (node)
        {
            await using (FakeMwb current = await FakeMwb.AcceptAsync(listener, Kit.Key, MwbFormat.Current))
            {
                await current.HandshakeAsync();
                await Kit.Until(states, LinkStatus.Connected);
            }

            // The peer goes away (closed or reset, depending on the system) and comes back speaking the old format.
            await Kit.Until(states, LinkStatus.Waiting);
            await using FakeMwb old = await FakeMwb.AcceptAsync(listener, Kit.Key, MwbFormat.Old);
            _ = old.HandshakeAsync();
            Assert.Equal(
                "peer now offers the old Mouse Without Borders format after using 0.101 or later; Seamdesk won't fall back on its own: choose Old for it in Seamdesk's settings",
                (await Kit.Until(states, LinkStatus.Waiting)).Reason);
        }
    }

    [Theory]
    [InlineData("127.0.0.2", true, true, "mwb peer: accepted a connection from 127.0.0.2")]
    [InlineData("127.0.0.1", true, false, "mwb: refused a connection from 127.0.0.1: not the address of any Mouse Without Borders machine in Seamdesk's settings")]
    [InlineData("127.0.0.2", false, false, "mwb peer: refused a connection from 127.0.0.2: incoming control is off for this machine (\"Let this machine control Seamdesk\")")]
    public async Task The_listener_answers_only_the_configured_address_of_a_peer_allowed_to_control_this_machine_and_logs_why(string from, bool allowControl, bool answered, string logged)
    {
        // Configured: 127.0.0.2. Its own dials go to port 1 and fail.
        Channel<string> log = Channel.CreateUnbounded<string>();
        (MwbNode node, _) = Kit.Start(Kit.Session() with { AllowControl = allowControl }, "127.0.0.2", 1, log);
        await using (node)
        {
            using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Bind(new IPEndPoint(IPAddress.Parse(from), 0));
            await client.ConnectAsync(IPAddress.Loopback, node.Port);
            using NetworkStream stream = new(client);

            // Seamdesk answers MWB's start with its own: a 16-byte random block and ten 64-byte Handshakes. Anyone else is
            // closed before a byte, which may reset the connection while the start is still being written.
            byte[] buffer = new byte[16 + (10 * Package.Big)];
            int read = 0;
            try
            {
                await new FakeMwb(stream, Kit.Key).WriteStartAsync();
                read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false).AsTask().WaitAsync(Kit.Patience);
            }
            catch (IOException) when (!answered)
            {
            }

            Assert.Equal(answered ? buffer.Length : 0, read);
            await Kit.UntilLogged(log, logged);
        }
    }

    [Fact]
    public async Task No_input_from_the_peer_is_taken_before_its_handshake()
    {
        // The peer may control this machine, but sends input before it answers Seamdesk's handshake.
        Package[] early =
        [
            new(PackageType.Mouse) { Des = Kit.SeamdeskId, X = 16384, Y = 65535, MouseFlags = 0x200 },
            new(PackageType.Mouse) { Des = Kit.SeamdeskId, X = 100000, Y = 100000, MouseFlags = 0x201 },
            new(PackageType.Keyboard) { Des = Kit.SeamdeskId, Vk = 0x41 },
        ];
        using var listener = Kit.Listen();
        (MwbNode node, FakeMwb fake, MwbLink link) = await Kit.ConnectedAsync(listener, Kit.Controller, before: early);
        await using (node)
        await using (fake)
        {
            await fake.SendAsync(new Package(PackageType.Hello) { Des = 255, Name = "MWB-PEER" });
            for (int heartbeats = 0; heartbeats < 2;)
            {
                // Seamdesk's first heartbeat, then its answer to Hello: it has read everything sent so far.
                heartbeats += (await fake.ReadAsync().WaitAsync(Kit.Patience))!.Type == PackageType.Heartbeat ? 1 : 0;
            }

            Assert.True(link.Incoming.TryRead(out Frame? monitors));
            Assert.IsType<Monitors>(monitors);
            Assert.False(link.Incoming.TryRead(out Frame? extra), $"surfaced {extra}");
        }
    }

    [Fact]
    public async Task The_peers_input_drives_this_machine_and_the_desk_hands_the_cursor_back()
    {
        using var listener = Kit.Listen();
        (MwbNode node, FakeMwb fake, MwbLink link) = await Kit.ConnectedAsync(listener, Kit.Controller);
        await using (node)
        await using (fake)
        {
            Assert.IsType<Monitors>(await link.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));

            // MWB's controller: the arrival as an absolute move, then relative and absolute moves, a click, a button, the
            // horizontal wheel and a key. The PC's box is 1920 x 1080.
            await fake.SendAsync(new Package(PackageType.Mouse) { Des = Kit.SeamdeskId, X = 16384, Y = 65535, MouseFlags = 0x200 });
            await fake.SendAsync(new Package(PackageType.Mouse) { Des = Kit.SeamdeskId, X = 100005, Y = -100003, MouseFlags = 0x200 });
            await fake.SendAsync(new Package(PackageType.Mouse) { Des = Kit.SeamdeskId, X = 32768, Y = 32768, MouseFlags = 0x200 });
            await fake.SendAsync(new Package(PackageType.Mouse) { Des = Kit.SeamdeskId, X = 100000, Y = 100000, MouseFlags = 0x201 });
            await fake.SendAsync(new Package(PackageType.Mouse) { Des = Kit.SeamdeskId, X = 100000, Y = 100000, MouseFlags = 0x20C, WheelDelta = 2 });
            await fake.SendAsync(new Package(PackageType.Mouse) { Des = Kit.SeamdeskId, X = 100000, Y = 100000, MouseFlags = 0x20E, WheelDelta = -240 });
            await fake.SendAsync(new Package(PackageType.Keyboard) { Des = Kit.SeamdeskId, Vk = 0x25, KeyFlags = 0x81 });
            Frame[] expected =
            [
                new Handoff("pc", new Point(480, 1079)), // 16384 * 1920 / 65535; 65535 is the bottom row
                new PointerMove(5, -3),
                new PointerMove(480, -539), // to (960, 540) from the last absolute point
                new PointerButton(MouseButton.Left, true),
                new PointerButton(MouseButton.X2, false),
                new Wheel(-240, true),
                new Key(0x25, 0, false, true),
            ];
            foreach (Frame frame in expected)
            {
                Assert.Equal(frame, await link.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));
            }

            // Seamdesk's desk moves the cursor down onto the peer at (1000, 0): MWB is told to take it there, over its 4480 x 1440 box.
            await link.SendAsync(new Handoff("peer", new Point(1000, 0)));
            Package next = await fake.NextAsync();
            Assert.Equal((PackageType.NextMachine, FakeMwb.Id, 14635, 22, (int)FakeMwb.Id), (next.Type, next.Des, next.X, next.Y, next.WheelDelta));

            // MWB then hides this machine's cursor, which no longer matters. Later it drives here again and moves away itself.
            await fake.SendAsync(new Package(PackageType.HideMouse) { Des = Kit.SeamdeskId });
            await fake.SendAsync(new Package(PackageType.Mouse) { Des = Kit.SeamdeskId, X = 0, Y = 0, MouseFlags = 0x200 });
            await fake.SendAsync(new Package(PackageType.HideMouse) { Des = Kit.SeamdeskId });
            Assert.Equal(new Handoff("pc", new Point(0, 0)), await link.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));
            Assert.Equal(new Handoff("peer", default), await link.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));
        }
    }

    [Fact]
    public async Task The_peers_clipboard_text_is_taken_with_copy_and_paste_both_ways()
    {
        using var listener = Kit.Listen();
        (MwbNode node, FakeMwb fake, MwbLink link) = await Kit.ConnectedAsync(listener, Kit.Session() with { TakeClipboard = true });
        await using (node)
        await using (fake)
        {
            // As MWB's CheckClipboardEx sends it: every format it has, deflated, plus the spare zeros of its buffer.
            string text = "the peer's clipboard, ünïcödé\r\n" + new string('x', 300);
            using MemoryStream data = new();
            using (DeflateStream deflate = new(data, CompressionMode.Compress, leaveOpen: true))
            {
                deflate.Write(Encoding.Unicode.GetBytes("RTF{\\rtf1 x}" + Separator + "TXT" + text + Separator));
            }

            foreach (byte[] slice in data.ToArray().Concat(new byte[100]).Chunk(48))
            {
                Package p = new(PackageType.ClipboardText) { Des = 255 };
                slice.CopyTo(p.Bytes, 16);
                await fake.SendAsync(p);
            }

            await fake.SendAsync(new Package(PackageType.ClipboardDataEnd) { Des = 255 });

            Assert.IsType<Monitors>(await link.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));
            Assert.Equal(new ClipboardText(text), await link.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));
        }
    }

    [Fact]
    public async Task The_peers_clipboard_input_and_other_packages_are_dropped_unless_allowed_and_never_answered()
    {
        using var listener = Kit.Listen();
        (MwbNode node, FakeMwb fake, MwbLink link) = await Kit.ConnectedAsync(listener);
        await using (node)
        await using (fake)
        {
            using MemoryStream text = new();
            using (DeflateStream deflate = new(text, CompressionMode.Compress, leaveOpen: true))
            {
                deflate.Write(Encoding.Unicode.GetBytes("TXT" + "the peer's secret clipboard, " + new string('x', 200) + "{4CFF57F7-BEDD-43d5-AE8F-27A61E886F2F}"));
            }

            foreach (byte[] slice in text.ToArray().Chunk(48))
            {
                Package p = new(PackageType.ClipboardText) { Des = 255 };
                slice.CopyTo(p.Bytes, 16);
                await fake.SendAsync(p);
            }

            PackageType[] others =
            [
                PackageType.ClipboardDataEnd, PackageType.Clipboard, PackageType.ClipboardAsk, PackageType.ClipboardPush,
                PackageType.MachineSwitched, PackageType.ClipboardCapture, PackageType.Mouse, PackageType.Keyboard,
                PackageType.Matrix, PackageType.Heartbeat, PackageType.Awake, PackageType.Hi, PackageType.HideMouse,
            ];
            foreach (PackageType type in others)
            {
                await fake.SendAsync(new Package(type) { Des = Kit.SeamdeskId, Name = "MWB-PEER" });
            }

            await fake.SendAsync(new Package(PackageType.Hello) { Des = 255, Name = "MWB-PEER" });

            // Seamdesk reads on: Hello is answered with a heartbeat. Until then it sent only handshake answers and its first heartbeat.
            List<PackageType> sent = [];
            for (int heartbeats = 0; heartbeats < 2;)
            {
                Package p = (await fake.ReadAsync().WaitAsync(Kit.Patience))!;
                _ = p.Type == PackageType.Heartbeat ? heartbeats++ : 0;
                if (p.Type is not (PackageType.Heartbeat or PackageType.HandshakeAck))
                {
                    sent.Add(p.Type);
                }
            }

            Assert.Empty(sent);

            Assert.True(link.Incoming.TryRead(out Frame? monitors));
            Assert.IsType<Monitors>(monitors);
            Assert.False(link.Incoming.TryRead(out Frame? extra), $"surfaced {extra}");
        }
    }

    [Fact]
    public async Task A_hand_back_returns_the_cursor_here_once_and_hides_the_peers_cursor()
    {
        using var listener = Kit.Listen();
        (MwbNode node, FakeMwb fake, MwbLink link) = await Kit.ConnectedAsync(listener);
        await using (node)
        await using (fake)
        {
            Assert.IsType<Monitors>(await link.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));
            await link.SendAsync(new Handoff("peer", new Point(1000, 0)));
            Assert.Equal(PackageType.Mouse, (await fake.NextAsync()).Type);

            // The peer's MWB: the cursor left its top edge at x = 1000 (MachineStuff.cs MoveUp, ConvertToUniversalValue).
            Package nextMachine = new(PackageType.NextMachine) { Des = Kit.SeamdeskId, X = 1000 * 65535 / 4480, Y = 1438 * 65535 / 1440, WheelDelta = (int)Kit.SeamdeskId };
            await fake.SendAsync(nextMachine);

            Assert.Equal(new Handoff("pc", new Point(1000, 1079)), await link.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));
            Package hide = await fake.NextAsync();
            Assert.Equal((PackageType.HideMouse, FakeMwb.Id), (hide.Type, hide.Des));

            // A second NextMachine (say, after a snap-back crossed it) no longer holds the cursor: nothing happens.
            await fake.SendAsync(new Package(nextMachine.Bytes));
            await fake.SendAsync(new Package(PackageType.Hello) { Des = 255, Name = "MWB-PEER" });
            Assert.Equal(PackageType.Heartbeat, (await fake.ReadAsync().WaitAsync(Kit.Patience))!.Type);
            Assert.False(link.Incoming.TryRead(out Frame? extra), $"surfaced {extra}");
        }
    }

    [Fact]
    public async Task A_crossing_lands_where_the_desk_says_moves_follow_1to1_and_the_cursor_comes_back_only_across_the_shared_edge()
    {
        // A reported "jumpy between screens" (0.7.7): three arrivals and three HideMouse in one second. The peer
        // here handles Seamdesk's packages as MWB's receiver does.
        using var listener = Kit.Listen();
        (MwbNode node, FakeMwb fake, MwbLink link) = await Kit.ConnectedAsync(listener);
        await using (node)
        await using (fake)
        {
            Assert.IsType<Monitors>(await link.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));
            Channel<Point> at = Channel.CreateUnbounded<Point>();
            int relative = 0, hides = 0;
            Task peer = Task.Run(async () =>
            {
                // Its box is Kit.Both, 4480 x 1440, with the PC above it in its matrix.
                Point cursor = default;
                while (await fake.ReadAsync() is { } p)
                {
                    hides += p.Type == PackageType.HideMouse ? 1 : 0;
                    if (p.Type != PackageType.Mouse || p.MouseFlags != 0x200)
                    {
                        continue;
                    }

                    if (Math.Abs(p.X) >= 100000 && Math.Abs(p.Y) >= 100000)
                    {
                        // Receiver.cs:147-170: injected relative (InputSimulation.cs:283-301, here without the pointer's
                        // acceleration), then its own edge test (MachineStuff.cs:239-257, SKIP_PIXELS = 1): on its top
                        // row it hands the cursor to the PC (MoveUp; Common.cs:516-540).
                        relative++;
                        int dx = p.X < 0 ? p.X + 100000 : p.X - 100000, dy = p.Y < 0 ? p.Y + 100000 : p.Y - 100000;
                        cursor = new Point(Math.Clamp(cursor.X + dx, 0, 4479), Math.Clamp(cursor.Y + dy, 0, 1439));
                        if (cursor.Y < 1)
                        {
                            await fake.SendAsync(new Package(PackageType.NextMachine)
                            {
                                Des = Kit.SeamdeskId, X = cursor.X * 65535 / 4480, Y = 1438 * 65535 / 1440, WheelDelta = (int)Kit.SeamdeskId,
                            });
                        }
                    }
                    else
                    {
                        // Receiver.cs:180-184: an absolute position, 0..65535 over its box (InputSimulation.cs:160-171).
                        cursor = new Point((int)((long)p.X * 4480 / 65535), (int)((long)p.Y * 1440 / 65535));
                    }

                    at.Writer.TryWrite(cursor);
                }
            });

            async Task<Point> Reaches(Point target)
            {
                Point last = default;
                while (last != target)
                {
                    last = await at.Reader.ReadAsync().AsTask().WaitAsync(Kit.Patience);
                }

                return last;
            }

            // Pushed down off the PC's bottom row at x = 1000, then along the shared edge, then down into the peer.
            await link.SendAsync(new Handoff("peer", new Point(1000, 0)));
            Assert.Equal(new Point(1000, 0), await Reaches(new Point(1000, 0)));
            for (int i = 0; i < 50; i++)
            {
                await link.SendAsync(new PointerMove(1, 0));
                await Task.Delay(1);
            }

            for (int i = 0; i < 50; i++)
            {
                await link.SendAsync(new PointerMove(0, 1));
                await Task.Delay(1);
            }

            await Reaches(new Point(1050, 50));
            Assert.False(link.Incoming.TryRead(out Frame? bounced), $"the cursor bounced: {bounced}");

            // Back up across the shared edge: once, where the desk continues, and the peer's cursor is let go.
            for (int i = 0; i < 51; i++)
            {
                await link.SendAsync(new PointerMove(0, -1));
            }

            Assert.Equal(new Handoff("pc", new Point(1050, 1077)), await link.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));
            await Reaches(new Point(1050, 0));
            await Task.Delay(100);
            Assert.False(link.Incoming.TryRead(out Frame? extra), $"surfaced {extra}");
            Assert.Equal((0, 1), (relative, hides)); // the peer's acceleration and edge test never apply
        }
    }

    [Fact]
    public async Task Driving_logs_one_timing_line_a_second_an_idle_link_none_and_never_what_was_typed_or_copied()
    {
        FakeTimeProvider time = new();
        Channel<string> log = Channel.CreateUnbounded<string>();
        using var listener = Kit.Listen();
        (MwbNode node, FakeMwb fake, MwbLink link) = await Kit.ConnectedAsync(listener, Kit.Session(time), log: log);
        await using (node)
        await using (fake)
        {
            const string copied = "words the user copied";
            List<string> lines = [];
            async Task IdleAsync(int heartbeats)
            {
                // The peer's Hello is answered with a heartbeat: a write, but no input.
                await fake.SendAsync(new Package(PackageType.Hello) { Des = 255, Name = "MWB-PEER" });
                while (heartbeats > 0)
                {
                    heartbeats -= (await fake.ReadAsync().WaitAsync(Kit.Patience))!.Type == PackageType.Heartbeat ? 1 : 0;
                }

                time.Advance(TimeSpan.FromSeconds(5));
                while (log.Reader.TryRead(out string? line))
                {
                    lines.Add(line);
                }
            }

            await IdleAsync(heartbeats: 2); // the link's first heartbeat, then the answer
            Assert.DoesNotContain(lines, l => l.Contains("driving"));

            // Driving: the arrival, three moves written 1, 2 and 3 ms after their first hook event, a key, the clipboard.
            await link.SendAsync(new Handoff("peer", new Point(100, 1100)));
            for (int i = 1; i <= 3; i++)
            {
                long since = time.GetTimestamp();
                time.Advance(TimeSpan.FromMilliseconds(i));
                link.Moving(since, i);
                await link.SendAsync(new PointerMove(i, -i));
            }

            await link.SendAsync(new Key(0x41, 0x1E, true, false));
            await link.SendAsync(new ClipboardText(copied));
            time.Advance(TimeSpan.FromSeconds(1));
            await IdleAsync(heartbeats: 1);

            string[] driving = [.. lines.Where(l => l.Contains("driving"))];
            Assert.Single(driving);
            Assert.Matches(
                @"^mwb peer: driving, last second: \d+ packages \(4 mouse, 0 waited for another write\), 3 moves from 6 hook events \(at most 3 in one\), " +
                @"hook to written ms min 1\.00 med 2\.00 p95 3\.00 max 3\.00, write ms min 0\.00 med 0\.00 p95 0\.00 max 0\.00, tcp rtt [\d.]+ ms, peer window \d+ KiB$",
                driving[0]);
            Assert.DoesNotContain(lines, l => l.Contains(copied) || l.Contains("VirtualKey"));
        }
    }

    [Fact]
    public async Task An_8_kHz_mouse_reaches_a_peer_slower_than_it_within_milliseconds_at_about_a_package_a_millisecond_every_pixel_kept()
    {
        // A tested laptop kept up with 3,300 packages a second: a fake MWB that injects one in 300 us, in order, behind
        // MWB's 320000-byte receive buffer (SocketStuff.cs:1284).
        using var listener = Kit.Listen();
        listener.Server.ReceiveBufferSize = 32 * 10000;
        (MwbNode node, FakeMwb fake, MwbLink link) = await Kit.ConnectedAsync(listener);
        await using (node)
        await using (fake)
        {
            const int Hz = 8000, Ms = 500, Events = Hz * Ms / 1000, Start = 200;
            await link.SendAsync(new Handoff("peer", new Point(Start, 700)));
            List<(long At, int Sum)> landed = [];
            Task peer = Task.Run(async () =>
            {
                for (int sum = 0; sum < Events;)
                {
                    Package p = await fake.NextAsync();
                    int x = (int)((long)p.X * 4480 / 65535) - Start; // the peer's pixel, as its MWB injects it
                    if (p.Type == PackageType.Mouse && x > 0)
                    {
                        SpinUntil(Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 300 / 1_000_000));
                        sum = x;
                        landed.Add((Stopwatch.GetTimestamp(), sum));
                    }
                }
            });

            // The hook's events, a pixel each, sent one by one as the app's effects loop sends them.
            long[] hookAt = new long[Events + 1];
            long start = Stopwatch.GetTimestamp();
            for (int i = 1; i <= Events; i++)
            {
                SpinUntil(start + (i * Stopwatch.Frequency / Hz));
                hookAt[i] = Stopwatch.GetTimestamp();
                await link.SendAsync(new PointerMove(1, 0));
            }

            await peer.WaitAsync(Kit.Patience);

            // Each event's lag: from the hook until the peer's cursor has come that far.
            List<double> lag = [];
            for (int i = 1, j = 0; i <= Events; i++)
            {
                while (landed[j].Sum < i)
                {
                    j++;
                }

                lag.Add((landed[j].At - hookAt[i]) * 1000.0 / Stopwatch.Frequency);
            }

            lag.Sort();
            Assert.Equal(Events, landed[^1].Sum);
            Assert.InRange(landed.Count, Ms / 2, Ms + 2);
            Assert.True(lag[Events / 2] < 5 && lag[^1] < 50, $"lag ms: median {lag[Events / 2]:F1}, max {lag[^1]:F1}");
        }
    }

    [Fact]
    public async Task A_click_or_wheel_goes_at_once_after_the_moves_held_back_before_it()
    {
        // The clock stands still: the first move and the one the pace lets go early are sent, the third and fourth are held
        // back, and only the click sends them. Positions are absolute, 0..65535 over the peer's 4480 x 1440 box.
        using var listener = Kit.Listen();
        (MwbNode node, FakeMwb fake, MwbLink link) = await Kit.ConnectedAsync(listener, Kit.Session(new FakeTimeProvider()));
        await using (node)
        await using (fake)
        {
            await link.SendAsync(new Handoff("peer", new Point(1000, 500)));
            await link.SendAsync(new PointerMove(1, 0));
            await link.SendAsync(new PointerMove(2, -1));
            await link.SendAsync(new PointerMove(3, -2));
            await link.SendAsync(new PointerMove(4, -3));
            await link.SendAsync(new PointerButton(MouseButton.Left, true));
            await link.SendAsync(new Wheel(120, false));

            List<(int X, int Y, int Flags)> sent = [];
            for (int i = 0; i < 6; i++)
            {
                Package p = await fake.NextAsync();
                sent.Add((p.X, p.Y, p.MouseFlags));
            }

            Assert.Equal(
                [(14650, 22777, 0x200), (14679, 22732, 0x200), (14781, 22504, 0x200), (100000, 100000, 0x201), (100000, 100000, 0x20A)],
                sent.Skip(1)); // after the arrival: (1001, 500), (1003, 499), the two held back as (1010, 494), the click, the wheel
        }
    }

    [Fact]
    public async Task A_peer_that_stops_reading_while_driven_gets_the_cursor_taken_back_with_one_line_and_its_cursor_let_go()
    {
        // A tested laptop: its TCP alive, its MWB reading nothing, the cursor stranded there for 14 minutes.
        Channel<string> log = Channel.CreateUnbounded<string>();
        using var listener = Kit.Listen();
        listener.Server.ReceiveBufferSize = 2048 * 1024; // the window that laptop advertised
        listener.Server.SetRawSocketOption(6, 2, BitConverter.GetBytes(1460)); // TCP_MAXSEG: a LAN's, not loopback's 64 KiB
        (MwbNode node, FakeMwb fake, MwbLink link) = await Kit.ConnectedAsync(listener, log: log);
        await using (node)
        await using (fake)
        {
            Assert.IsType<Monitors>(await link.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));
            await link.SendAsync(new Handoff("peer", new Point(200, 700)));
            Task<Frame> taken = link.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience);
            int moved = 0;
            while (!taken.IsCompleted)
            {
                await link.SendAsync(new PointerMove(1, 0));
                moved++;
                await Task.Delay(1);
            }

            Assert.Equal(new Locked(true), await taken);
            Assert.InRange(moved, 2048, 4096); // 64 KiB of 32-byte packages, and less than as much again

            // Still unread: the peer stays locked, and the line is written once.
            await Task.Delay(500);
            Assert.False(link.Incoming.TryRead(out Frame? extra), $"surfaced {extra}");
            List<string> lines = [];
            while (log.Reader.TryRead(out string? line))
            {
                lines.Add(line);
            }

            Assert.Matches(
                @"^mwb peer: stopped reading: \d+ KiB sent to it are unread \(its TCP window fell from \d+ to \d+ KiB\); the cursor comes back here and won't cross to it until it reads them, or for 5 s$",
                Assert.Single(lines, l => l.StartsWith("mwb peer: stopped reading")));

            // When its MWB reads again: every move, then the HideMouse that lets its cursor go and releases its keys.
            int x = 0;
            Package p;
            while ((p = await fake.NextAsync()).Type != PackageType.HideMouse)
            {
                x = p.Type == PackageType.Mouse ? (int)((long)p.X * 4480 / 65535) - 200 : x;
            }

            Assert.Equal(FakeMwb.Id, p.Des);
            Assert.InRange(x, moved - 2, moved); // the last position before it; a move or two sent after the cursor left is dropped
        }
    }

    [Fact]
    public async Task A_peer_still_unread_5_s_after_it_stalled_may_be_crossed_to_again_and_stalls_again_with_a_line_each()
    {
        // A receiver may keep its window shut after reading, so the lock doesn't wait for it forever. The clock is fake;
        // the window is the real socket's.
        FakeTimeProvider time = new();
        Channel<string> log = Channel.CreateUnbounded<string>();
        using var listener = Kit.Listen();
        listener.Server.ReceiveBufferSize = 2048 * 1024;
        listener.Server.SetRawSocketOption(6, 2, BitConverter.GetBytes(1460));
        (MwbNode node, FakeMwb fake, MwbLink link) = await Kit.ConnectedAsync(listener, Kit.Session(time), log: log);
        await using (node)
        await using (fake)
        {
            Assert.IsType<Monitors>(await link.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));
            await link.SendAsync(new Handoff("peer", new Point(1000, 0)));
            Frame? frame;
            for (int moved = 0; !link.Incoming.TryRead(out frame); moved++)
            {
                Assert.True(moved < 20000, "never stalled");
                await link.SendAsync(new PointerMove(moved % 2 == 0 ? 1 : -1, 0));
                time.Advance(TimeSpan.FromMilliseconds(1));
                if (moved % 64 == 0)
                {
                    await Task.Delay(1); // the peer's ACKs bring its window
                }
            }

            Assert.Equal(new Locked(true), frame);
            time.Advance(TimeSpan.FromMilliseconds(4900));
            Assert.False(link.Incoming.TryRead(out frame), $"surfaced {frame} before 5 s");
            time.Advance(TimeSpan.FromMilliseconds(100));
            Assert.True(link.Incoming.TryRead(out frame));
            Assert.Equal(new Locked(false), frame);

            // Crossed to again, still unread: back within a tick.
            await link.SendAsync(new Handoff("peer", new Point(1000, 0)));
            time.Advance(TimeSpan.FromMilliseconds(100));
            Assert.True(link.Incoming.TryRead(out frame));
            Assert.Equal(new Locked(true), frame);

            List<string> lines = [];
            while (log.Reader.TryRead(out string? line))
            {
                lines.Add(line);
            }

            Assert.Equal(2, lines.Count(l => l.StartsWith("mwb peer: stopped reading")));
            Assert.Matches(
                @"^mwb peer: \d+ KiB still unread after 5 s: the cursor may cross to it again, and comes back if it still doesn't read$",
                Assert.Single(lines, l => l.Contains("still unread")));
        }
    }

    [Fact]
    public async Task A_link_silent_for_two_of_the_peers_heartbeats_and_a_minute_is_closed_and_says_why()
    {
        // 10:17:58: trusted, then silence. The peer's MWB sends a heartbeat at least every 5 minutes (frmScreen.cs:567-571).
        FakeTimeProvider time = new();
        Channel<string> log = Channel.CreateUnbounded<string>();
        using var listener = Kit.Listen();
        (MwbNode node, FakeMwb fake, MwbLink link) = await Kit.ConnectedAsync(listener, Kit.Session(time), log: log);
        await using (node)
        await using (fake)
        {
            // At 10 minutes the peer speaks: MWB answers any Handshake (SocketStuff.cs:1370-1383), and so does Seamdesk.
            time.Advance(TimeSpan.FromMinutes(10));
            Package probe = new(RandomNumberGenerator.GetBytes(Package.Big)) { Type = PackageType.Handshake, Name = "MWB-PEER" };
            await fake.SendAsync(probe);
            for (Package p; (p = (await fake.ReadAsync().WaitAsync(Kit.Patience))!).Type != PackageType.HandshakeAck || p.Machine(0) != ~probe.Machine(0);)
            {
            }

            time.Advance(TimeSpan.FromMinutes(10));
            Assert.False(link.Closed.IsCompleted, $"closed though the peer spoke 10 minutes ago: {(link.Closed.IsCompleted ? link.Closed.Result : "")}");
            time.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal("nothing from peer for 11 minutes, and its Mouse Without Borders sends a heartbeat at least every 5", await link.Closed.WaitAsync(Kit.Patience));
            Assert.Contains("mwb peer: closed: nothing from peer for 11 minutes, and its Mouse Without Borders sends a heartbeat at least every 5",
                await Kit.UntilLogged(log, "mwb peer: closed: nothing from peer for 11 minutes, and its Mouse Without Borders sends a heartbeat at least every 5"));
        }
    }

    private static void SpinUntil(long timestamp)
    {
        while (Stopwatch.GetTimestamp() < timestamp)
        {
        }
    }

    [Fact]
    public async Task Frames_MWB_has_no_message_for_send_nothing_and_an_unknown_frame_throws()
    {
        using var listener = Kit.Listen();
        (MwbNode node, FakeMwb fake, MwbLink link) = await Kit.ConnectedAsync(listener);
        await using (node)
        await using (fake)
        {
            Assert.IsType<Monitors>(await link.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));

            // The cursor sends Locked to every peer, and a Handoff between other machines may reach this one.
            await link.SendAsync(new Locked(true));
            await link.SendAsync(new Handoff("laptop", new Point(0, 0)));
            await Assert.ThrowsAsync<ArgumentException>(() => link.SendAsync(new Unknown()));

            // Nothing was sent for any of them, and the link still answers Hello (its second heartbeat).
            await fake.SendAsync(new Package(PackageType.Hello) { Des = 255, Name = "MWB-PEER" });
            List<PackageType> sent = [];
            for (int heartbeats = 0; heartbeats < 2;)
            {
                Package p = (await fake.ReadAsync().WaitAsync(Kit.Patience))!;
                _ = p.Type == PackageType.Heartbeat ? heartbeats++ : 0;
                if (p.Type is not (PackageType.Heartbeat or PackageType.HandshakeAck))
                {
                    sent.Add(p.Type);
                }
            }

            Assert.Empty(sent);
        }
    }

    private sealed record Unknown : Frame;
}
