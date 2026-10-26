using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Seamdesk.Core;
using Seamdesk.Transport;

namespace Seamdesk.Mwb.Tests;

/// <summary>
/// The peer's MWB as far as the tests need it: its handshake (Class/SocketStuff.cs MainTCPRoutine, the same at
/// a9e1f0f20^ and 48c4bad07) in either format, on Seamdesk's codec, which the vectors check against MWB's own code.
/// </summary>
internal sealed class FakeMwb(Stream stream, MwbKey key, MwbFormat format = MwbFormat.Old) : IAsyncDisposable
{
    public const uint Id = 0x01020304;

    private readonly Package hello = new(RandomNumberGenerator.GetBytes(Package.Big)) { Type = PackageType.Handshake, Name = "MWB-PEER", Src = 99 };
    private ICryptoTransform encryptor = null!;
    private ICryptoTransform? decryptor;
    private int nextId = 7000;

    public static async Task<FakeMwb> AcceptAsync(TcpListener listener, MwbKey key, MwbFormat format = MwbFormat.Old) =>
        new(new NetworkStream(await listener.AcceptSocketAsync().WaitAsync(Kit.Patience), ownsSocket: true), key, format);

    /// <summary>What MWB writes before it reads: the current format's header, a random block and ten Handshakes.</summary>
    public async Task WriteStartAsync()
    {
        byte[]? header = format == MwbFormat.Current ? RandomNumberGenerator.GetBytes(MwbKey.HeaderSize) : null;
        if (header is not null)
        {
            await stream.WriteAsync(header);
        }

        encryptor = key.Encryptor(header);
        await WriteRawAsync(RandomNumberGenerator.GetBytes(16));
        for (int i = 0; i < 10; i++)
        {
            await WriteRawAsync(key.Seal(hello));
        }
    }

    /// <summary>
    /// <see cref="WriteStartAsync"/>, then <paramref name="before"/>; then reads, answering Handshakes if told to, until
    /// Seamdesk's answer.
    /// </summary>
    public async Task HandshakeAsync(bool answer = true, IEnumerable<Package>? before = null)
    {
        await WriteStartAsync();
        foreach (Package p in before ?? [])
        {
            await SendAsync(p);
        }

        while (await ReadAsync() is { } p)
        {
            if (p.Type == PackageType.Handshake && answer)
            {
                Package ack = new(p.Bytes) { Type = PackageType.HandshakeAck, Src = Id, Name = "MWB-PEER" };
                for (int i = 0; i < 4; i++)
                {
                    ack.SetMachine(i, ~p.Machine(i));
                }

                await WriteRawAsync(key.Seal(ack));
            }
            else if (p.Type == PackageType.HandshakeAck)
            {
                Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(~hello.Machine(i), p.Machine(i)));
                return;
            }
        }
    }

    public Task SendAsync(Package p)
    {
        p.Id = ++nextId;
        if (p.Src == 0)
        {
            p.Src = Id;
        }

        return WriteRawAsync(key.Seal(p));
    }

    /// <summary>The next package, Invalid if it fails the magic number or checksum, or null once Seamdesk closes.</summary>
    public async Task<Package?> ReadAsync()
    {
        byte[] first = await ReadRawAsync(Package.Small);
        if (first.Length == 0)
        {
            return null;
        }

        if (!key.Open(first))
        {
            return new Package(PackageType.Invalid);
        }

        Package p = new(first);
        if (p.IsBig)
        {
            (await ReadRawAsync(Package.Small)).CopyTo(p.Bytes, Package.Small);
        }

        return p;
    }

    /// <summary>The next package other than handshake answers and Seamdesk's heartbeats.</summary>
    public async Task<Package> NextAsync()
    {
        while (await ReadAsync().WaitAsync(Kit.Patience) is { } p)
        {
            if (p.Type is not (PackageType.HandshakeAck or PackageType.Heartbeat))
            {
                return p;
            }
        }

        throw new EndOfStreamException("Seamdesk closed the link");
    }

    public ValueTask DisposeAsync() => stream.DisposeAsync();

    private async Task WriteRawAsync(byte[] plain)
    {
        byte[] cipher = new byte[plain.Length];
        encryptor.TransformBlock(plain, 0, plain.Length, cipher, 0);
        await stream.WriteAsync(cipher);
    }

    private async Task<byte[]> ReadRawAsync(int count)
    {
        if (decryptor is null)
        {
            // Seamdesk's start: the current format's header, then its random block.
            byte[]? header = format == MwbFormat.Current ? new byte[MwbKey.HeaderSize] : null;
            if (header is not null && await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false) < header.Length)
            {
                return [];
            }

            decryptor = key.Decryptor(header);
            await ReadRawAsync(16);
        }

        byte[] cipher = new byte[count];
        if (await stream.ReadAtLeastAsync(cipher, count, throwOnEndOfStream: false) < count)
        {
            return [];
        }

        byte[] plain = new byte[count];
        decryptor.TransformBlock(cipher, 0, count, plain, 0);
        return plain;
    }
}

/// <summary>An MWB node on loopback that dials a <see cref="FakeMwb"/>, and the example peer's screens.</summary>
internal static class Kit
{
    public const uint SeamdeskId = 0x0A0B0C0D;

    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    public static readonly MwbKey Key = new("SeamdeskTests-NotARealKey-0123"); // made up; never a real MWB key

    /// <summary>The peer's 1440p and a 1920x1200 panel to its right (saved layout 0); or the panel alone (layout 1).</summary>
    public static readonly Rect[] Both = [new Rect(0, 0, 2560, 1440), new Rect(2560, 0, 1920, 1200)];
    public static readonly Rect[] PanelOnly = [new Rect(0, 0, 1920, 1200)];

    /// <summary>The PC's 1080p, with the peer's origin straight below it.</summary>
    public static Desk Desk(IReadOnlyList<Rect> peer) =>
        new([new Machine("pc", new Point(0, 0), [new Rect(0, 0, 1920, 1080)]), new Machine("peer", new Point(0, 1080), peer)]);

    public static MwbSession Session(TimeProvider? time = null, MwbKey? key = null) =>
        new("peer", key ?? Key, "SEAMDESK-PC", SeamdeskId, "pc", new MwbScreens([Both, PanelOnly]), () => Desk(Both), time ?? TimeProvider.System);

    /// <summary>A peer that may control this machine.</summary>
    public static MwbSession Controller => Session() with { AllowControl = true };

    /// <summary>
    /// A node whose only peer is <paramref name="host"/>; its dials go to <paramref name="peerPort"/>. Its log lines go to
    /// <paramref name="log"/>, if any.
    /// </summary>
    public static (MwbNode Node, Channel<LinkState> States) Start(MwbSession session, string host, int peerPort, Channel<string>? log = null)
    {
        MwbNode node = new([new MwbPeer(host, session)], port: 0) { PeerPort = peerPort, Random = () => 0.5, Log = line => log?.Writer.TryWrite(line) };
        Channel<LinkState> states = Channel.CreateUnbounded<LinkState>();
        node.LinkChanged += (_, state) => states.Writer.TryWrite(state);
        node.Start();
        return (node, states);
    }

    public static TcpListener Listen()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    public static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>Waits for the log line <paramref name="line"/>; returns every line logged until then.</summary>
    public static async Task<List<string>> UntilLogged(Channel<string> log, string line)
    {
        List<string> lines = [];
        while (lines.LastOrDefault() != line)
        {
            lines.Add(await log.Reader.ReadAsync().AsTask().WaitAsync(Patience));
        }

        return lines;
    }

    public static async Task<LinkState> Until(Channel<LinkState> states, LinkStatus status)
    {
        while (true)
        {
            LinkState state = await states.Reader.ReadAsync().AsTask().WaitAsync(Patience);
            if (state.Status == status)
            {
                return state;
            }
        }
    }

    /// <summary>A node connected to a fake peer that completed MWB's handshake.</summary>
    public static async Task<(MwbNode Node, FakeMwb Fake, MwbLink Link)> ConnectedAsync(
        TcpListener listener, MwbSession? session = null, MwbFormat format = MwbFormat.Old, IEnumerable<Package>? before = null, Channel<string>? log = null)
    {
        (MwbNode node, Channel<LinkState> states) = Start(session ?? Session(), "127.0.0.1", Port(listener), log);
        FakeMwb fake = await FakeMwb.AcceptAsync(listener, Key, format);
        await fake.HandshakeAsync(before: before);
        await Until(states, LinkStatus.Connected);
        return (node, fake, node["peer"]!.Current!);
    }
}
