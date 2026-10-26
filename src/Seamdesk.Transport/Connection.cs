using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

namespace Seamdesk.Transport;

/// <summary>What this machine says about itself on every connection.</summary>
public sealed record LocalNode(Identity Identity, Hello Hello, Pins Pins, TimeProvider Time);

public sealed class RefusedException(string reason, string? theirVersion = null, bool versionMismatch = false) : Exception(reason)
{
    public string? TheirVersion { get; } = theirVersion;

    public bool VersionMismatch { get; } = versionMismatch;
}

/// <summary>TLS 1.3 only, both sides present a certificate. Pins are checked after the handshake so a refusal can say why.</summary>
public static class Tls
{
    public static async Task<SslStream> DialAsync(Stream network, Identity me, CancellationToken ct)
    {
        SslStream ssl = new(network, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "seamdesk",
                    EnabledSslProtocols = SslProtocols.Tls13,
                    LocalCertificateSelectionCallback = (_, _, _, _, _) => me.Certificate,
                    RemoteCertificateValidationCallback = AnyCertificate,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                ct);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync();
            throw;
        }
    }

    public static async Task<SslStream> AcceptAsync(Stream network, Identity me, CancellationToken ct)
    {
        SslStream ssl = new(network, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = me.Certificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = AnyCertificate,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                ct);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync();
            throw;
        }
    }

    // Self-signed by design: trust comes from the pin (or from pairing), checked by the caller right after this.
    private static bool AnyCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        certificate is not null;
}

/// <summary>
/// An authenticated connection to a paired peer, in state Ready. Only Ready connections exist as objects, so input and
/// layout frames can only be sent in Ready. Both sides ping every 2 s; 6 s without any frame closes it.
/// </summary>
public sealed class Connection : ILink
{
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2);
    public const int MissedPings = 3;

    private readonly SslStream ssl;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource<string> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<Frame> incoming = Channel.CreateUnbounded<Frame>();
    private long lastReceived;

    private Connection(SslStream ssl, TimeProvider time, Pin peer, Hello peerHello, string dialedBy)
    {
        this.ssl = ssl;
        this.time = time;
        Peer = peer;
        PeerHello = peerHello;
        DialedBy = dialedBy;
        lastReceived = time.GetTimestamp();
        _ = ReadLoop();
        _ = PingLoop();
    }

    public Pin Peer { get; }

    public Hello PeerHello { get; }

    /// <summary>Machine id of the side that dialled. Used to pick one connection when both directions come up.</summary>
    public string DialedBy { get; }

    /// <summary>Input, layout and other application frames from the peer. Pings are not surfaced.</summary>
    public ChannelReader<Frame> Incoming => incoming.Reader;

    /// <summary>Completes with the reason once the connection is closed, from either side.</summary>
    public Task<string> Closed => closed.Task;

    /// <summary>
    /// Runs Tls -> PinChecked -> HelloOk -> Ready on a finished TLS handshake. <paramref name="expectedSpki"/> is the pin
    /// the dialer meant to reach (null when accepting). Throws <see cref="RefusedException"/> or
    /// <see cref="ProtocolException"/>, having closed the stream.
    /// </summary>
    public static async Task<Connection> EstablishAsync(SslStream ssl, LocalNode me, bool dialer, Pin? expected, CancellationToken ct)
    {
        try
        {
            // Tls -> PinChecked
            // The dialer accepts only the peer it meant to reach; the listener accepts any pinned peer.
            string spki = Identity.SpkiOf(ssl.RemoteCertificate!);
            Pin? pin = expected is null ? me.Pins.Find(spki) : spki == expected.Spki ? expected : null;
            if (pin is null)
            {
                string reason = expected is not null
                    ? $"{expected.Name}'s identity changed: pair again"
                    : "not paired with this machine (unknown certificate): pair again";
                await RefuseAsync(ssl, me, reason, ct);
                throw new RefusedException(reason);
            }

            // PinChecked -> HelloOk
            Hello hello = await ExchangeHelloAsync(ssl, me, ct);

            // HelloOk -> Ready: each side says it accepted the other's Hello.
            await FrameCodec.WriteAsync(ssl, new ReadyFrame(), ct);
            await ExpectAsync<ReadyFrame>(ssl, ct);
            return new Connection(ssl, me.Time, pin, hello, dialer ? me.Hello.MachineId : hello.MachineId);
        }
        catch
        {
            await ssl.DisposeAsync();
            throw;
        }
    }

    /// <summary>Sends our Hello and checks theirs. A protocol mismatch is refused with both versions in plain words.</summary>
    internal static async Task<Hello> ExchangeHelloAsync(SslStream ssl, LocalNode me, CancellationToken ct)
    {
        await FrameCodec.WriteAsync(ssl, me.Hello, ct);
        Hello hello = await ExpectAsync<Hello>(ssl, ct);
        if (hello.Protocol != me.Hello.Protocol)
        {
            string reason = $"{hello.MachineId} runs Seamdesk {hello.AppVersion} (protocol {hello.Protocol}), " +
                $"{me.Hello.MachineId} runs Seamdesk {me.Hello.AppVersion} (protocol {me.Hello.Protocol}): update one of them";
            await RefuseAsync(ssl, me, reason, ct);
            throw new RefusedException(reason, hello.AppVersion, versionMismatch: true);
        }

        return hello;
    }

    /// <summary>Reads the next frame, which must be a <typeparamref name="T"/>. A Refuse throws with its reason; anything else closes.</summary>
    internal static async Task<T> ExpectAsync<T>(SslStream ssl, CancellationToken ct)
        where T : Frame
    {
        return await FrameCodec.ReadAsync(ssl, ct) switch
        {
            T frame => frame,
            Refuse refuse => throw new RefusedException(refuse.Reason, refuse.TheirVersion),
            null => throw new IOException("the peer closed the connection"),
            Frame other => throw new ProtocolException($"{other.GetType().Name} received while waiting for {typeof(T).Name}"),
        };
    }

    internal static async Task RefuseAsync(SslStream ssl, LocalNode me, string reason, CancellationToken ct)
    {
        try
        {
            await FrameCodec.WriteAsync(ssl, new Refuse(reason, me.Hello.AppVersion), ct);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The peer is gone already; the local reason still stands.
        }
    }

    public async Task SendAsync(Frame frame, CancellationToken ct = default)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            await FrameCodec.WriteAsync(ssl, frame, ct);
        }
        catch (Exception e) when (e is not ArgumentException) // a frame the codec refuses leaves the link as it was
        {
            Close(e.Message);
            throw;
        }
        finally
        {
            writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Close("closed locally");
        return ValueTask.CompletedTask;
    }

    private void Close(string reason)
    {
        if (closed.TrySetResult(reason))
        {
            stop.Cancel();
            incoming.Writer.TryComplete();
            ssl.Dispose();
        }
    }

    private async Task ReadLoop()
    {
        try
        {
            while (true)
            {
                Frame? frame = await FrameCodec.ReadAsync(ssl, stop.Token);
                Volatile.Write(ref lastReceived, time.GetTimestamp());
                switch (frame)
                {
                    case null:
                        Close("closed by the peer");
                        return;
                    case Ping:
                        break;
                    case Refuse refuse:
                        Close($"refused by the peer: {refuse.Reason}");
                        return;
                    case Hello or ReadyFrame or Pake:
                        Close($"{frame.GetType().Name} received after Ready");
                        return;
                    default:
                        incoming.Writer.TryWrite(frame);
                        break;
                }
            }
        }
        catch (Exception e)
        {
            Close(e.Message);
        }
    }

    private async Task PingLoop()
    {
        try
        {
            using PeriodicTimer timer = new(PingInterval, time);
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                if (time.GetElapsedTime(Volatile.Read(ref lastReceived)) >= PingInterval * MissedPings)
                {
                    Close($"no reply for {(PingInterval * MissedPings).TotalSeconds:0} s");
                    return;
                }

                await SendAsync(new Ping(), stop.Token);
            }
        }
        catch (Exception e)
        {
            Close(e.Message);
        }
    }
}
