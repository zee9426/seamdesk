using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace Seamdesk.Transport.Tests;

/// <summary>Real identities, real nodes on loopback ports.</summary>
internal static class Kit
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static readonly string Dir = Directory.CreateTempSubdirectory("seamdesk-tests-").FullName;

    public static LocalNode Local(string name, TimeProvider? time = null, int protocol = 1, string appVersion = "0.1.0") =>
        new(Identity.LoadOrCreateAsync(new FileIdentityStore(Path.Combine(Dir, $"{Guid.NewGuid():N}.p12")), name, Patience, TimeProvider.System).GetAwaiter().GetResult(),
            new Hello(protocol, appVersion, name), new Pins(), time ?? TimeProvider.System);

    public static Pin PinOf(LocalNode node) => new(node.Identity.Spki, node.Hello.MachineId, "127.0.0.1");

    /// <summary>Starts a node on a free port. Its own dials go to port 1 (closed) unless the test points them somewhere.</summary>
    public static Node Start(LocalNode me, Func<double>? random = null, Action<string>? log = null)
    {
        Node node = new(me, port: 0) { PeerPort = 1, Random = random ?? (() => 0.5), Log = log ?? (_ => { }) };
        node.Start();
        return node;
    }

    public static async Task<SslStream> RawDialAsync(LocalNode me, int port)
    {
        TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return await Tls.DialAsync(client.GetStream(), me.Identity, CancellationToken.None);
    }

    /// <summary>A TCP connection from <paramref name="source"/> (a loopback address) that never starts its TLS handshake.</summary>
    public static async Task<Socket> StallAsync(string source, int port)
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Parse(source), 0));
        await socket.ConnectAsync(IPAddress.Loopback, port);
        return socket;
    }

    /// <summary>Whether the other side closes <paramref name="socket"/> (then disposed) within a second.</summary>
    public static async Task<bool> ClosedSoonAsync(Socket socket)
    {
        using (socket)
        {
            using CancellationTokenSource soon = new(TimeSpan.FromSeconds(1));
            try
            {
                return await socket.ReceiveAsync(new byte[1], SocketFlags.None, soon.Token) == 0;
            }
            catch (SocketException)
            {
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    /// <summary>Every frame until the other side closes.</summary>
    public static async Task<List<Frame>> ReadUntilClosedAsync(Stream stream)
    {
        List<Frame> frames = [];
        try
        {
            while (await FrameCodec.ReadAsync(stream).WaitAsync(Patience) is { } frame)
            {
                frames.Add(frame);
            }
        }
        catch (IOException)
        {
        }

        return frames;
    }

    public static async Task Eventually(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(Patience);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}
