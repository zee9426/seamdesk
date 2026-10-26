// MwbHandshake and Answer are ported from Mouse Without Borders (PowerToys, src/modules/MouseWithoutBorders/App):
// Class/SocketStuff.cs MainTCPRoutine at commit a9e1f0f20^.
// Copyright (c) Microsoft Corporation. The Microsoft Corporation licenses this file to you under the MIT license.
// Changes for Seamdesk: only the handshake steps, driven over a test socket.
// Runs Seamdesk's MWB link against MWB's own serialisation and encryption code (extracted by extract.sh) over loopback,
// both ways and in both directions of control, checks what each side makes of the other, and writes the captured
// streams as test vectors. Built with -p:Mwb=old or -p:Mwb=current (see README.md).
// Usage: dotnet tools/mwb-vectors/bin/Debug/net10.0/MwbVectors.dll <output .json>
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Seamdesk.Core;
using Seamdesk.Mwb;
using Seamdesk.Transport;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;
using Clipboard = MouseWithoutBorders.Core.Clipboard;
using MwbPackageType = MouseWithoutBorders.Core.PackageType;
using Point = Seamdesk.Core.Point;
using Package = MouseWithoutBorders.Core.Package;

internal static class Program
{
    // Made up for the vectors; never a real MWB key.
    private const string Key = "SeamdeskVectors-NotARealKey-0123";
    private const uint MwbId = 0x01020304;
    private const uint SeamdeskId = 0x0A0B0C0D;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // The peer's screens: a 1440p and a 1920x1200 to its right; on Seamdesk's desk it sits below the PC's 1080p.
    private static readonly Rect[] Layout = [new Rect(0, 0, 2560, 1440), new Rect(2560, 0, 1920, 1200)];

#if MWB_CURRENT
    private const string Source = "PowerToys 48c4bad07, src/modules/MouseWithoutBorders/App, via tools/mwb-vectors";
    private const MwbFormat Expected = MwbFormat.Current;
    private const int HeaderSize = 32; // Encryption.cs:134-136: salt and IV in the clear
#else
    private const string Source = "PowerToys a9e1f0f20^, src/modules/MouseWithoutBorders/App, via tools/mwb-vectors";
    private const MwbFormat Expected = MwbFormat.Old;
    private const int HeaderSize = 0;
#endif

    private static readonly BlockingCollection<DATA> FromSeamdesk = [];
    private static readonly BlockingCollection<Frame> FromLink = [];

    private static int Main(string[] args)
    {
        try
        {
            Run(args[0]).GetAwaiter().GetResult();
            Console.WriteLine($"ok: wrote {args[0]}");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"FAILED: {e}");
            return 1;
        }
    }

    private static async Task Run(string output)
    {
        // MWB's side, set up as SocketStuff's constructor does (Class/SocketStuff.cs:221-227).
        Encryption.MyKey = Key;
        Encryption.InitEncryption();
        Encryption.MagicNumber = Encryption.Get24BitHash(Encryption.MyKey);
        Common.MachineName = "MWB-PEER";
        Common.MachineIdValue = MwbId;
        Package.PackageID = 5000;

        (Socket mwbSocket, Socket relayA) = Pair();
        (Socket seamdeskSocket, Socket relayB) = Pair();
        MemoryStream mwbToSeamdesk = new(), seamdeskToMwb = new();
        Task pumps = Task.WhenAll(Pump(relayA, relayB, mwbToSeamdesk), Pump(relayB, relayA, seamdeskToMwb));

        TcpSk tcp = new(true, mwbSocket, SocketStatus.Connecting, "SEAMDESK-PC");
        Common.Tcp = tcp;
        Common.Sk = new SocketStuff();

        Desk desk = new([new Machine("pc", new Point(0, 0), [new Rect(0, 0, 1920, 1080)]), new Machine("peer", new Point(0, 1080), Layout)]);
        // Auto, so the harness also checks that Seamdesk finds MWB's format; the peer may control Seamdesk and send its clipboard.
        MwbSession session = new("peer", new MwbKey(Key), "SEAMDESK-PC", SeamdeskId, "pc", new MwbScreens([Layout]), () => desk, TimeProvider.System)
        {
            AllowControl = true,
            TakeClipboard = true,
        };
        Task<MwbLink> seamdeskSide = MwbLink.EstablishAsync(new NetworkStream(seamdeskSocket, ownsSocket: true), session, dialer: true, CancellationToken.None);

        await Task.Run(() => MwbHandshake(tcp)).WaitAsync(Patience);
        MwbLink link = await seamdeskSide.WaitAsync(Patience);
        Check(link.PeerId == MwbId && link.PeerName == "MWB-PEER", $"Seamdesk trusted id {link.PeerId:X} name {link.PeerName}");
        Check(link.Format == Expected, $"Seamdesk found the {link.Format} format");
        _ = Task.Run(() => MwbReceive(tcp));
        _ = Task.Run(async () =>
        {
            await foreach (Frame f in link.Incoming.ReadAllAsync())
            {
                FromLink.Add(f);
            }

            FromLink.CompleteAdding();
        });
        Check(NextFrame() is Monitors, "Seamdesk surfaced the peer's layout first");

        DATA heartbeat = Next();
        Check(heartbeat.Type == MwbPackageType.Heartbeat && heartbeat.Des == ID.ALL && heartbeat.MachineName == "SEAMDESK-PC" && (uint)heartbeat.Src == SeamdeskId, "Seamdesk's first heartbeat");

        // Seamdesk -> MWB: everything Seamdesk sends while driving, each read back by MWB's own receive code.
        List<object> inputs = [];
        (string Name, Frame Frame, Action<DATA> Expect)[] cases =
        [
            ("Handoff peer 1000 0", new Handoff("peer", new Point(1000, 0)), d => Mouse(d, 0x200, 14635, 22, 0)),
            ("PointerMove 5 3", new PointerMove(5, 3), d => Mouse(d, 0x200, 14708, 159, 0)), // (1005, 3), absolute as MWB's controller sends it
            ("PointerMove -7 0", new PointerMove(-7, 0), d => Mouse(d, 0x200, 14606, 159, 0)), // (998, 3)
            ("PointerButton Left down", new PointerButton(MouseButton.Left, true), d => Mouse(d, 0x201, 100000, 100000, 0)),
            ("PointerButton Left up", new PointerButton(MouseButton.Left, false), d => Mouse(d, 0x202, 100000, 100000, 0)),
            ("PointerButton Right down", new PointerButton(MouseButton.Right, true), d => Mouse(d, 0x204, 100000, 100000, 0)),
            ("PointerButton Right up", new PointerButton(MouseButton.Right, false), d => Mouse(d, 0x205, 100000, 100000, 0)),
            ("PointerButton Middle down", new PointerButton(MouseButton.Middle, true), d => Mouse(d, 0x207, 100000, 100000, 0)),
            ("PointerButton Middle up", new PointerButton(MouseButton.Middle, false), d => Mouse(d, 0x208, 100000, 100000, 0)),
            ("PointerButton X1 down", new PointerButton(MouseButton.X1, true), d => Mouse(d, 0x20B, 100000, 100000, 1)),
            ("PointerButton X2 up", new PointerButton(MouseButton.X2, false), d => Mouse(d, 0x20C, 100000, 100000, 2)),
            ("Wheel 120 vertical", new Wheel(120, false), d => Mouse(d, 0x20A, 100000, 100000, 120)),
            ("Wheel -240 horizontal", new Wheel(-240, true), d => Mouse(d, 0x20E, 100000, 100000, -240)),
            ("Key 0x41 down", new Key(0x41, 0x1E, true, false), d => Keyboard(d, 0x41, 0)),
            ("Key 0x41 up", new Key(0x41, 0x1E, false, false), d => Keyboard(d, 0x41, 0x80)),
            ("Key 0x25 down extended", new Key(0x25, 0x4B, true, true), d => Keyboard(d, 0x25, 0x01)),
            ("Key 0x25 up extended", new Key(0x25, 0x4B, false, true), d => Keyboard(d, 0x25, 0x81)),
        ];
        foreach ((string name, Frame frame, Action<DATA> expect) in cases)
        {
            await link.SendAsync(frame);
            DATA d = Next();
            Check((uint)d.Src == SeamdeskId, $"{name}: source");
            expect(d);
            inputs.Add(new { frame = name, package = Convert.ToHexString(d.Bytes) });
        }

        string text = "Seamdesk → MWB clipboard: ünïcödé, tabs\tand\r\nlines. " + string.Concat(Enumerable.Repeat("0123456789", 30));
        await link.SendAsync(new ClipboardText(text));
        await Eventually(() => Clipboard.Received is not null);
        Check(Clipboard.Received.Data.Count == 1 && (string)Clipboard.Received.Data[DataFormats.UnicodeText] == text, "MWB's clipboard decoder read Seamdesk's text");

        // MWB -> Seamdesk: what the peer's MWB sends a controller.
        Common.SendHello();
        DATA reply = Next();
        Check(reply.Type == MwbPackageType.Heartbeat && reply.MachineName == "SEAMDESK-PC", "Seamdesk answers Hello with a heartbeat");

        Common.SendHeartBeat();
        Common.SendPackage((ID)SeamdeskId, MwbPackageType.ClipboardAsk);

        // As CheckClipboardEx sends it: text and RTF, deflated, with the spare zeros of the buffer (Clipboard.cs ms.GetBuffer()).
        byte[] theirs = Deflate("RTF{\\rtf1 the peer's}" + "{4CFF57F7-BEDD-43d5-AE8F-27A61E886F2F}" + "TXT" + "the peer's clipboard" + "{4CFF57F7-BEDD-43d5-AE8F-27A61E886F2F}");
        typeof(Clipboard).GetMethod("SendClipboardDataUsingTCP", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [theirs, false]);
        Check(NextFrame() is ClipboardText { Text: "the peer's clipboard" }, "Seamdesk took the text from MWB's own clipboard sender");

        // The cursor leaves the peer's top edge at x = 1000 (MachineStuff.cs MoveUp, relative mode).
        MyRectangle box = new() { Left = 0, Top = 0, Right = 4480, Bottom = 1440 };
        System.Drawing.Point back = (System.Drawing.Point)typeof(MachineStuff).GetMethod("ConvertToUniversalValue", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [new System.Drawing.Point(1000, box.Bottom - 2), box])!;
        Common.SendNextMachine((ID)SeamdeskId, (ID)SeamdeskId, back);
        DATA hide = Next();
        Check(hide.Type == MwbPackageType.HideMouse && (uint)hide.Des == MwbId, $"Seamdesk hides the peer's cursor after the hand-back, got {hide.Type}");
        Check(FromSeamdesk.Count == 0, "Seamdesk sent nothing for the peer's heartbeat, clipboard or ClipboardAsk");
        Check(NextFrame() is Handoff { Machine: "pc", Point: { X: 1000, Y: 1079 } }, "Seamdesk surfaced the hand-back");

        await link.SendAsync(new Handoff("peer", new Point(10, 10)));
        Next();
        await link.SendAsync(new Handoff("pc", new Point(5, 5))); // snap-back
        Check(Next().Type == MwbPackageType.HideMouse, "snap-back hides the peer's cursor");

        // MWB drives Seamdesk. Packages built as MWB's controller builds them (Event.cs:97-116, :263-268, :214), sent by its
        // own SkSend and TcpSend. Seamdesk's box is 1920 x 1080; the peer's 4480 x 1440.
        List<object> driven = [];
        (string Name, DATA Package, string[] Frames)[] control =
        [
            ("arrival 16384 65413", MwbMouse(0x200, 16384, 65413, 0), ["Handoff pc 480 1077"]),
            ("relative move 5 -3", MwbMouse(0x200, 100005, -100003, 0), ["PointerMove 5 -3"]),
            ("absolute move 32768 32768", MwbMouse(0x200, 32768, 32768, 0), ["PointerMove 480 -537"]),
            ("left down", MwbMouse(0x201, 100000, 100000, 0), ["PointerButton Left down"]),
            ("left up", MwbMouse(0x202, 100000, 100000, 0), ["PointerButton Left up"]),
            ("right down", MwbMouse(0x204, 100000, 100000, 0), ["PointerButton Right down"]),
            ("right up", MwbMouse(0x205, 100000, 100000, 0), ["PointerButton Right up"]),
            ("middle down", MwbMouse(0x207, 100000, 100000, 0), ["PointerButton Middle down"]),
            ("middle up", MwbMouse(0x208, 100000, 100000, 0), ["PointerButton Middle up"]),
            ("x1 down", MwbMouse(0x20B, 100000, 100000, 1), ["PointerButton X1 down"]),
            ("x2 up", MwbMouse(0x20C, 100000, 100000, 2), ["PointerButton X2 up"]),
            ("wheel 120", MwbMouse(0x20A, 100000, 100000, 120), ["Wheel 120 vertical"]),
            ("hwheel -240", MwbMouse(0x20E, 100000, 100000, -240), ["Wheel -240 horizontal"]),
            ("absolute click 0 0", MwbMouse(0x201, 0, 0, 0), ["PointerMove -960 -540", "PointerButton Left down"]),
            ("key 0x41 down", MwbKey(0x41, 0), ["Key 0x41 down"]),
            ("key 0x41 up", MwbKey(0x41, 0x80), ["Key 0x41 up"]),
            ("key 0x25 down extended", MwbKey(0x25, 0x01), ["Key 0x25 down extended"]),
            ("key 0x25 up extended", MwbKey(0x25, 0x81), ["Key 0x25 up extended"]),
        ];
        foreach ((string name, DATA package, string[] frames) in control)
        {
            Common.SkSend(package, null, false);
            string[] got = [.. frames.Select(_ => Describe(NextFrame()))];
            Check(got.SequenceEqual(frames), $"{name}: Seamdesk surfaced {string.Join(", ", got)}");
            driven.Add(new { name, package = Convert.ToHexString(package.Bytes), frames });
        }

        // Seamdesk's desk moves the cursor onto the peer at (1000, 0): MWB's own decoder reads the NextMachine.
        await link.SendAsync(new Handoff("peer", new Point(1000, 0)));
        DATA handBack = Next();
        Check(handBack.Type == MwbPackageType.NextMachine && (uint)handBack.Des == MwbId && handBack.Md.WheelDelta == (int)MwbId
            && handBack.Md.X == 14635 && handBack.Md.Y == 22, $"Seamdesk's hand-back: {handBack.Type} des {handBack.Des} next {handBack.Md.WheelDelta} at {handBack.Md.X},{handBack.Md.Y}");

        // MWB answers by hiding Seamdesk's cursor (Event.cs:211-215), which no longer matters; later it drives Seamdesk again
        // and moves away itself.
        Common.SkSend(MwbHide(), null, false);
        Common.SkSend(MwbMouse(0x200, 0, 0, 0), null, false);
        Check(Describe(NextFrame()) == "Handoff pc 0 0", "the second arrival");
        Common.SkSend(MwbHide(), null, false);
        Check(Describe(NextFrame()) == "Handoff peer 0 0", "HideMouse after MWB moved away");

        Common.SendByeBye();
        Check(await link.Closed.WaitAsync(Patience) == "peer said goodbye", "ByeBye closes Seamdesk's link");
        Check(!FromLink.TryTake(out Frame extra, Patience) && FromLink.IsCompleted, $"Seamdesk surfaced nothing else, got {extra}");
        mwbSocket.Dispose();
        await pumps.WaitAsync(Patience);

        string json = JsonSerializer.Serialize(
            new
            {
                source = Source,
                format = Expected.ToString(),
                key = Key,
#if MWB_CURRENT
                // Per stream: PBKDF2 of the key with each stream's own salt (Encryption.cs GenLegalKey(salt)).
                mwbToSeamdeskAesKey = Convert.ToHexString(GenLegalKey(mwbToSeamdesk.ToArray())),
                seamdeskToMwbAesKey = Convert.ToHexString(GenLegalKey(seamdeskToMwb.ToArray())),
#else
                aesKey = Convert.ToHexString((byte[])typeof(Encryption).GetMethod("GenLegalKey", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!),
                iv = Convert.ToHexString((byte[])typeof(Encryption).GetMethod("GenLegalIV", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!),
#endif
                magic = Encryption.MagicNumber,
                mwbToSeamdesk = new { cipher = Convert.ToHexString(mwbToSeamdesk.ToArray()), plain = Convert.ToHexString(MwbDecrypt(mwbToSeamdesk.ToArray())) },
                seamdeskToMwb = new { cipher = Convert.ToHexString(seamdeskToMwb.ToArray()), plain = Convert.ToHexString(MwbDecrypt(seamdeskToMwb.ToArray())) },
                inputs,
                driven,
                handBack = Convert.ToHexString(handBack.Bytes),
            },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(output, json + "\n");
    }

    /// <summary>
    /// MWB's handshake, ported line for line from MainTCPRoutine (Class/SocketStuff.cs:1269-1295, :1371-1385,
    /// :1409-1442 at a9e1f0f20^), which is too entangled to extract. TcpSend and TcpReceiveData are MWB's own.
    /// </summary>
    private static void MwbHandshake(TcpSk tcp)
    {
        DATA d = new(RandomNumberGenerator.GetBytes(Package.PACKAGE_SIZE_EX));
        d.MachineName = Common.MachineName;
        d.Type = MwbPackageType.Handshake;
        for (int i = 0; i < 10; i++)
        {
            _ = Common.Sk.TcpSend(tcp, d);
        }

        d.Machine1 = ~d.Machine1;
        d.Machine2 = ~d.Machine2;
        d.Machine3 = ~d.Machine3;
        d.Machine4 = ~d.Machine4;
        while (true)
        {
            DATA package = SocketStuff.TcpReceiveData(tcp, out int _);
            ID remoteID = package.Src;
            if (package.Type == MwbPackageType.Handshake)
            {
                Answer(tcp, package);
            }
            else if (package.Type == MwbPackageType.HandshakeAck)
            {
                Check(package.Machine1 == d.Machine1 && package.Machine2 == d.Machine2 && package.Machine3 == d.Machine3 && package.Machine4 == d.Machine4, "Seamdesk's handshake answer");
                Check((uint)remoteID == SeamdeskId && package.MachineName == "SEAMDESK-PC", "Seamdesk's id and name");
                return;
            }
            else
            {
                throw new InvalidOperationException($"MWB got {package.Type} before trusting Seamdesk");
            }
        }
    }

    /// <summary>MWB's trusted receive loop, reduced to what a peer does with a controller's packages.</summary>
    private static void MwbReceive(TcpSk tcp)
    {
        while (true)
        {
            DATA package = SocketStuff.TcpReceiveData(tcp, out int _);
            switch (package.Type)
            {
                case MwbPackageType.Error:
                    return; // closed
                case MwbPackageType.Invalid:
                    throw new InvalidOperationException("MWB rejected a package from Seamdesk");
                case MwbPackageType.Handshake:
                    Answer(tcp, package);
                    break;
                case MwbPackageType.HandshakeAck:
                    break; // "Skipping the rest of the Handshake packages."
                case MwbPackageType.ClipboardText:
                    Clipboard.ReceiveClipboardDataUsingTCP(package, false, tcp); // as Receiver.cs does
                    break;
                default:
                    FromSeamdesk.Add(package);
                    break;
            }
        }
    }

    // SocketStuff.cs:1371-1385.
    private static void Answer(TcpSk tcp, DATA package)
    {
        package.Type = MwbPackageType.HandshakeAck;
        package.Src = ID.NONE;
        package.MachineName = Common.MachineName;
        package.Machine1 = ~package.Machine1;
        package.Machine2 = ~package.Machine2;
        package.Machine3 = ~package.Machine3;
        package.Machine4 = ~package.Machine4;
        _ = Common.Sk.TcpSend(tcp, package);
    }

    private static DATA Next() =>
        FromSeamdesk.TryTake(out DATA d, Patience) ? d : throw new TimeoutException("MWB received nothing from Seamdesk");

    private static void Mouse(DATA d, int flags, int x, int y, int wheel) =>
        Check(d.Type == MwbPackageType.Mouse && (uint)d.Des == MwbId && d.Md.dwFlags == flags && d.Md.X == x && d.Md.Y == y && d.Md.WheelDelta == wheel,
            $"mouse: got flags {d.Md.dwFlags:X} x {d.Md.X} y {d.Md.Y} wheel {d.Md.WheelDelta}");

    private static void Keyboard(DATA d, int vk, int flags) =>
        Check(d.Type == MwbPackageType.Keyboard && (uint)d.Des == MwbId && d.Kd.wVk == vk && d.Kd.dwFlags == flags, $"key: got vk {d.Kd.wVk:X} flags {d.Kd.dwFlags:X}");

    /// <summary>The whole captured stream through MWB's own decrypting stream (which reads the current format's header itself).</summary>
    private static byte[] MwbDecrypt(byte[] cipher)
    {
        Stream plain = Encryption.GetDecryptedStream(new MemoryStream(cipher));
        byte[] all = new byte[cipher.Length - HeaderSize];
        Check(plain.ReadEx(all, 0, all.Length) == all.Length, "MWB decrypts the whole stream");
        return all;
    }

#if MWB_CURRENT
    private static byte[] GenLegalKey(byte[] stream) =>
        (byte[])typeof(Encryption).GetMethod("GenLegalKey", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [stream[..16]])!;
#endif

    /// <summary>A mouse package as MWB's controller builds one (Event.cs:97-114).</summary>
    private static DATA MwbMouse(int flags, int x, int y, int wheel)
    {
        DATA d = new() { Des = (ID)SeamdeskId, Type = MwbPackageType.Mouse };
        d.Md.dwFlags = flags;
        d.Md.WheelDelta = wheel;
        d.Md.X = x;
        d.Md.Y = y;
        return d;
    }

    /// <summary>A key package as MWB's controller builds one (Event.cs:263-266), with the hook's LLKHF flags.</summary>
    private static DATA MwbKey(int vk, int flags) =>
        new() { Des = (ID)SeamdeskId, Type = MwbPackageType.Keyboard, Kd = new KEYBDDATA { wVk = vk, dwFlags = flags }, DateTime = Common.GetTick() };

    /// <summary>Common.SendPackage(des, HideMouse), as Event.cs:214 sends it to the machine it leaves.</summary>
    private static DATA MwbHide() => new() { Des = (ID)SeamdeskId, Type = MwbPackageType.HideMouse, MachineName = Common.MachineName };

    /// <summary>The harness's names for frames, as VectorsTests parses them: "Handoff pc 480 1077", "Key 0x25 up extended".</summary>
    private static string Describe(Frame f) => f switch
    {
        Handoff h => $"Handoff {h.Machine} {h.Point.X} {h.Point.Y}",
        PointerMove m => $"PointerMove {m.Dx} {m.Dy}",
        PointerButton b => $"PointerButton {b.Button} {(b.Down ? "down" : "up")}",
        Wheel w => $"Wheel {w.Delta} {(w.Horizontal ? "horizontal" : "vertical")}",
        Key k => $"Key 0x{k.VirtualKey:X2} {(k.Down ? "down" : "up")}{(k.Extended ? " extended" : "")}",
        _ => f.ToString(),
    };

    private static Frame NextFrame() =>
        FromLink.TryTake(out Frame f, Patience) ? f : throw new TimeoutException("Seamdesk surfaced nothing");

    /// <summary>MemoryStream.GetBuffer, as CheckClipboardEx sends it: the deflated bytes and the buffer's spare zeros.</summary>
    private static byte[] Deflate(string text)
    {
        MemoryStream ms = new();
        using (DeflateStream s = new(ms, CompressionMode.Compress, true))
        {
            s.Write(Encoding.Unicode.GetBytes(text));
        }

        return ms.GetBuffer();
    }

    private static (Socket, Socket) Pair()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        Socket a = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        a.Connect(listener.LocalEndpoint);
        return (a, listener.AcceptSocket());
    }

    /// <summary>Copies one direction of the relay, keeping every byte.</summary>
    private static async Task Pump(Socket from, Socket to, MemoryStream record)
    {
        byte[] buffer = new byte[4096];
        try
        {
            int n;
            while ((n = await from.ReceiveAsync(buffer)) > 0)
            {
                record.Write(buffer, 0, n);
                await to.SendAsync(buffer.AsMemory(0, n));
            }

            to.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
            // one end closed hard; everything before it is recorded
        }
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(Patience);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static void Check(bool ok, string what)
    {
        if (!ok)
        {
            throw new InvalidOperationException($"check failed: {what}");
        }
    }
}
