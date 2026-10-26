using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Seamdesk.Transport;

/// <summary>
/// One machine's transport: listens on one TCP port (private sources only), shows and enters pairing codes, and runs a
/// <see cref="Supervisor"/> per paired peer. Both sides listen and dial; the first authenticated connection wins.
/// </summary>
public sealed class Node(LocalNode me, int port = Node.DefaultPort) : IAsyncDisposable
{
    public const int DefaultPort = 15200;
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Connections still in their handshake at once, from one source address and in total; more are closed unread.</summary>
    public const int MaxHandshakesPerSource = 4, MaxHandshakes = 16;

    /// <summary>How long a closing accepted connection waits for the dialler to close too (<see cref="GentleStream"/>).</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, Supervisor> supervisors = [];
    private readonly Dictionary<string, CancellationTokenSource> running = [];
    private readonly CancellationTokenSource stop = new();
    private readonly Repeats said = new();
    private readonly Dictionary<IPAddress, int> handshaking = [];
    private TcpListener? listener;
    private PairingCode? code;

    public LocalNode Local => me;

    /// <summary>The port peers listen on. The same everywhere in production; tests on one box give each node its own.</summary>
    public int PeerPort { get; set; } = DefaultPort;

    public int Port => ((IPEndPoint)listener!.LocalEndpoint).Port;

    public Func<double> Random { get; init; } = System.Random.Shared.NextDouble;

    /// <summary>
    /// One line per connection the listener refused, and why, starting "seamdesk ": the dialler sees the reason in its tray,
    /// this side only here. Repeats are counted, not written each time. Never key material.
    /// </summary>
    public Action<string> Log { get; init; } = _ => { };

    /// <summary>Raised from supervisor threads whenever a peer's link state changes.</summary>
    public event Action<Pin, LinkState>? LinkChanged;

    public Supervisor this[Pin peer]
    {
        get
        {
            lock (supervisors)
            {
                return supervisors[peer.Spki];
            }
        }
    }

    public void Start()
    {
        listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        listener.Start();
        me.Pins.Added += Supervise;
        foreach (Pin pin in me.Pins.All)
        {
            Supervise(pin);
        }

        _ = AcceptLoop();
    }

    /// <summary>Retry every peer now (resume, unlock, address change, tray).</summary>
    public void KickAll()
    {
        lock (supervisors)
        {
            foreach (Supervisor s in supervisors.Values)
            {
                s.Kick();
            }
        }
    }

    /// <summary>Unpairs <paramref name="peer"/>: forgets its pin, stops dialling it and closes its connection.</summary>
    public void Unpair(Pin peer)
    {
        me.Pins.Remove(peer.Spki);
        StopSupervising(peer.Spki);
    }

    /// <summary>A fresh code to show; it replaces any earlier one.</summary>
    public PairingCode ShowPairingCode() => code = new PairingCode(me.Time);

    /// <summary>The user typed the code shown on <paramref name="host"/>. Returns the new pin.</summary>
    public async Task<Pin> PairAsync(string host, string pairingCode, CancellationToken ct = default)
    {
        using CancellationTokenSource deadline = new(HandshakeTimeout, me.Time);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token, deadline.Token);
        (Stream network, IPAddress address) = await ConnectAsync([host], timeout.Token);
        return await Pairing.DialAsync(await Tls.DialAsync(network, me.Identity, timeout.Token), me, pairingCode, address.ToString(), timeout.Token);
    }

    /// <summary>Dials a paired peer: its name is re-resolved on every attempt, then the address stored at pairing.</summary>
    public async Task<Connection> DialAsync(Pin peer, CancellationToken ct)
    {
        using CancellationTokenSource deadline = new(HandshakeTimeout, me.Time);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token, deadline.Token);
        (Stream network, _) = await ConnectAsync([peer.Name, peer.Address], timeout.Token);
        return await Connection.EstablishAsync(await Tls.DialAsync(network, me.Identity, timeout.Token), me, dialer: true, peer, timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        me.Pins.Added -= Supervise;
        await stop.CancelAsync();
        listener?.Stop();
        lock (supervisors)
        {
            foreach (Supervisor s in supervisors.Values)
            {
                s.Current?.DisposeAsync();
            }
        }
    }

    private void Supervise(Pin pin)
    {
        // A pin the new one replaced (the same machine paired again) is no longer dialled, nor its link kept.
        List<string> replaced;
        lock (supervisors)
        {
            replaced = [.. supervisors.Keys.Where(spki => me.Pins.Find(spki) is null)];
        }

        replaced.ForEach(StopSupervising);
        lock (supervisors)
        {
            if (supervisors.ContainsKey(pin.Spki))
            {
                return;
            }

            Supervisor supervisor = new(ct => DialAsync(pin, ct), me.Time, Random);
            supervisor.StateChanged += state => LinkChanged?.Invoke(pin, state);
            supervisors[pin.Spki] = supervisor;
            CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            running[pin.Spki] = cancel;
            _ = supervisor.RunAsync(cancel.Token);
        }
    }

    private void StopSupervising(string spki)
    {
        Supervisor? supervisor;
        CancellationTokenSource? cancel;
        lock (supervisors)
        {
            supervisors.Remove(spki, out supervisor);
            running.Remove(spki, out cancel);
        }

        cancel?.Cancel();
        supervisor?.Current?.DisposeAsync();
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
        IPAddress address = ((IPEndPoint)socket.RemoteEndPoint!).Address;
        address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (!AddressFilter.IsAllowed(address))
        {
            Refused(address, "not a private network address");
            socket.Dispose();
            return;
        }

        if (!Admit(address))
        {
            Refused(address, "too many connections still in their handshake");
            socket.Dispose();
            return;
        }

        // The handshake's slot is freed once the peer is authenticated, or once its socket is closed.
        int freed = 0;
        void Free()
        {
            if (Interlocked.Exchange(ref freed, 1) == 0)
            {
                lock (handshaking)
                {
                    if (--handshaking[address] == 0)
                    {
                        handshaking.Remove(address);
                    }
                }
            }
        }

        socket.NoDelay = true;
        GentleStream network = new(socket, me.Time, Free);
        try
        {
            using CancellationTokenSource deadline = new(HandshakeTimeout, me.Time);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, deadline.Token);
            var ssl = await Tls.AcceptAsync(network, me.Identity, timeout.Token);
            if (me.Pins.Find(Identity.SpkiOf(ssl.RemoteCertificate!)) is null && code is { } active)
            {
                Pin paired = await Pairing.RespondAsync(ssl, me, active, address.ToString(), timeout.Token);
                Log($"seamdesk: paired with {paired.Name} at {address}");
                return;
            }

            Connection connection = await Connection.EstablishAsync(ssl, me, dialer: false, expected: null, timeout.Token);
            Supervisor? supervisor;
            lock (supervisors)
            {
                supervisor = supervisors.GetValueOrDefault(connection.Peer.Spki);
            }

            if (supervisor is null)
            {
                await connection.DisposeAsync();
                return;
            }

            Free();
            supervisor.Offer(connection);
        }
        catch (Exception e)
        {
            Refused(address, e.Message);
            await network.DisposeAsync();
        }
    }

    private bool Admit(IPAddress address)
    {
        lock (handshaking)
        {
            int from = handshaking.GetValueOrDefault(address);
            if (from >= MaxHandshakesPerSource || handshaking.Values.Sum() >= MaxHandshakes)
            {
                return false;
            }

            handshaking[address] = from + 1;
            return true;
        }
    }

    private void Refused(IPAddress address, string reason)
    {
        if (said.Next($"seamdesk: refused a connection from {address}: {reason}") is { } line)
        {
            Log(line);
        }
    }

    private async Task<(Stream, IPAddress)> ConnectAsync(string[] hosts, CancellationToken ct)
    {
        Exception? last = null;
        foreach (string host in hosts)
        {
            IPAddress[] addresses;
            try
            {
                addresses = IPAddress.TryParse(host, out IPAddress? literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
            }
            catch (SocketException e)
            {
                last = e;
                continue;
            }

            foreach (IPAddress address in addresses)
            {
                Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(address, PeerPort, ct);
                    return (new NetworkStream(socket, ownsSocket: true), address);
                }
                catch (SocketException e)
                {
                    socket.Dispose();
                    last = e;
                }
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }
}

/// <summary>
/// An accepted socket's stream that closes gently: FIN first, then what the dialler still sends is read and dropped until
/// it closes too, for at most <see cref="Node.Linger"/>. Closing with bytes unread sends RST, and Windows then drops what
/// was written last, a Refuse's reason, before the dialler reads it. <paramref name="closed"/> runs once the socket is.
/// </summary>
internal sealed class GentleStream(Socket accepted, TimeProvider time, Action closed) : NetworkStream(accepted, ownsSocket: false)
{
    private int disposed;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            _ = LingerAsync();
        }
    }

    private async Task LingerAsync()
    {
        try
        {
            Socket.Shutdown(SocketShutdown.Send);
            using CancellationTokenSource linger = new(Node.Linger, time);
            byte[] buffer = new byte[4096];
            while (await Socket.ReceiveAsync(buffer, SocketFlags.None, linger.Token) > 0)
            {
            }
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Gone, reset or slow: close anyway.
        }
        finally
        {
            Socket.Dispose();
            closed();
        }
    }
}

/// <summary>
/// Counts repeated log lines: <see cref="Next"/> gives a line the 1st, 10th, 100th and 1000th time, then every 10000th,
/// with the count, and null otherwise. A peer redials every second or so, and seamdesk.log keeps only its last few megabytes.
/// Past <see cref="Most"/> distinct lines the counts start over, so a flood of them (one per source address) can't grow
/// it without bound.
/// </summary>
public sealed class Repeats
{
    public const int Most = 1000;

    private readonly ConcurrentDictionary<string, int> said = [];

    public string? Next(string line)
    {
        if (said.Count >= Most && !said.ContainsKey(line))
        {
            said.Clear();
        }

        int n = said.AddOrUpdate(line, 1, (_, c) => c + 1);
        return n is 1 or 10 or 100 or 1000 || n % 10000 == 0 ? (n == 1 ? line : $"{line} ({n} times)") : null;
    }

    /// <summary>What fails next is news again.</summary>
    public void Clear() => said.Clear();
}
