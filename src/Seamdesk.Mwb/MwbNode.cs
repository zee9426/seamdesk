using System.Net;
using System.Net.Sockets;
using Seamdesk.Transport;

namespace Seamdesk.Mwb;

/// <summary>One MWB peer: its configured host (name or address) and what Seamdesk needs to talk to it.</summary>
public sealed record MwbPeer(string Host, MwbSession Session);

/// <summary>
/// The MWB peers ("legacy security": MWB's shared key, no pinning). Each has a <see cref="Supervisor{TLink}"/> that dials
/// only its configured host on MWB's port. The listener accepts a connection only from an address that host resolves to,
/// and only for a peer allowed to control this machine, so that peer's MWB can also dial in.
/// </summary>
public sealed class MwbNode(IReadOnlyList<MwbPeer> peers, int port = MwbNode.DefaultPort) : IAsyncDisposable
{
    /// <summary>MWB listens for packages on its port + 1, and its default port is 15100 (Class/SocketStuff.cs:479, :1169).</summary>
    public const int DefaultPort = 15101;

    private readonly Dictionary<string, Supervisor<MwbLink>> supervisors = [];
    private readonly CancellationTokenSource stop = new();
    private TcpListener? listener;

    /// <summary>The port peers listen on. Tests on one box point it elsewhere.</summary>
    public int PeerPort { get; set; } = DefaultPort;

    public int Port => ((IPEndPoint)listener!.LocalEndpoint).Port;

    public Func<double> Random { get; init; } = System.Random.Shared.NextDouble;

    /// <summary>
    /// Every step of every link, one line each starting "mwb ": dials, accepted and refused connections, the handshake, and
    /// why a link closed; while Seamdesk drives a peer, a line a second on the send path's timing, and a peer that stopped
    /// reading. Never key material or what a package carries.
    /// </summary>
    public Action<string> Log { get; init; } = _ => { };

    private readonly Repeats said = new();

    /// <summary>Raised from supervisor threads whenever a peer's link state changes.</summary>
    public event Action<string, LinkState>? LinkChanged;

    /// <summary>The peer's supervisor, or null if <paramref name="peer"/> is not an MWB peer.</summary>
    public Supervisor<MwbLink>? this[string peer] => supervisors.GetValueOrDefault(peer);

    public void Start()
    {
        foreach (MwbPeer peer in peers)
        {
            Supervisor<MwbLink> supervisor = new(ct => DialAsync(peer, ct), peer.Session.Time, Random);
            supervisor.StateChanged += state => LinkChanged?.Invoke(peer.Session.Peer, state);
            supervisors[peer.Session.Peer] = supervisor;
            _ = supervisor.RunAsync(stop.Token);
        }

        listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        listener.Start();
        _ = AcceptLoop();
    }

    public void KickAll()
    {
        foreach (Supervisor<MwbLink> s in supervisors.Values)
        {
            s.Kick();
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener?.Stop();
        foreach (Supervisor<MwbLink> s in supervisors.Values)
        {
            if (s.Current is { } link)
            {
                await link.DisposeAsync();
            }
        }
    }

    private async Task<MwbLink> DialAsync(MwbPeer peer, CancellationToken ct)
    {
        string name = peer.Session.Peer;
        Exception? last = null;
        try
        {
            foreach (IPAddress address in await ResolveAsync(peer.Host, ct))
            {
                Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(address, PeerPort, ct);
                    Note($"mwb {name}: dialled {address}:{PeerPort}: connected");
                    return await HandshakeAsync(socket, peer, dialer: true);
                }
                catch (SocketException e)
                {
                    Note($"mwb {name}: dialled {address}:{PeerPort}: {e.SocketErrorCode} ({e.Message})");
                    socket.Dispose();
                    last = e;
                }
            }
        }
        catch (RefusedException e)
        {
            Note($"mwb {name}: refused: {e.Message}");
            throw;
        }
        catch (SocketException e)
        {
            Note($"mwb {name}: can't resolve {peer.Host}: {e.SocketErrorCode}");
            throw;
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    private async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener!.AcceptSocketAsync(stop.Token);
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = HandleAsync(socket);
        }
    }

    private async Task HandleAsync(Socket socket)
    {
        IPAddress? from = null;
        MwbPeer? peer = null;
        try
        {
            from = Unmapped(((IPEndPoint)socket.RemoteEndPoint!).Address);
            if (AddressFilter.IsAllowed(from))
            {
                foreach (MwbPeer candidate in peers)
                {
                    if ((await ResolveAsync(candidate.Host, stop.Token)).Any(a => Unmapped(a).Equals(from)))
                    {
                        peer = candidate;
                        break;
                    }
                }
            }

            // Not a configured peer allowed to control this machine: closed before a single byte. That peer's MWB logs
            // "Unable to read data from the transport connection" and dials again; Seamdesk's own link to it is not touched.
            string? refusal = peer is null ? "not the address of any Mouse Without Borders machine in Seamdesk's settings"
                : !peer.Session.AllowControl ? "incoming control is off for this machine (\"Let this machine control Seamdesk\")" : null;
            if (refusal is not null)
            {
                Note($"{Who(peer)}: refused a connection from {from}: {refusal}");
                socket.Dispose();
                return;
            }

            Note($"mwb {peer!.Session.Peer}: accepted a connection from {from}");
            bool kept = supervisors[peer.Session.Peer].Offer(await HandshakeAsync(socket, peer, dialer: false));
            Note($"mwb {peer.Session.Peer}: the link it dialled is {(kept ? "in use" : "dropped: Seamdesk's own link to it is kept")}");
        }
        catch (Exception e)
        {
            Note($"{Who(peer)}: refused a connection from {from}: {e.Message}");
            socket.Dispose();
        }
    }

    private async Task<MwbLink> HandshakeAsync(Socket socket, MwbPeer peer, bool dialer)
    {
        socket.NoDelay = true; // as MWB (SocketStuff.cs MainTCPRoutine)

        // MWB sends nothing while idle (a heartbeat every minute or five), so the OS's keepalive notices a peer that vanished.
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 1);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        using CancellationTokenSource deadline = new(Node.HandshakeTimeout, peer.Session.Time);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, deadline.Token);
        string name = peer.Session.Peer;
        MwbLink link = await MwbLink.EstablishAsync(
            new NetworkStream(socket, ownsSocket: true), peer.Session, dialer, timeout.Token, line => Note($"mwb {name}: {line}"), line => Log($"mwb {name}: {line}"));
        said.Clear(); // a link came up: what fails next is news again
        return link;
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) =>
        IPAddress.TryParse(host, out IPAddress? literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);

    private static IPAddress Unmapped(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    private static string Who(MwbPeer? peer) => peer is null ? "mwb" : $"mwb {peer.Session.Peer}";

    /// <summary>Logs a line, counting repeats (<see cref="Repeats"/>) until a link comes up.</summary>
    private void Note(string line)
    {
        if (said.Next(line) is { } text)
        {
            Log(text);
        }
    }
}
