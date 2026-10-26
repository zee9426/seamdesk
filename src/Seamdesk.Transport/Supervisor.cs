namespace Seamdesk.Transport;

public enum LinkStatus { Connecting, Connected, Waiting }

/// <summary>What the tray shows for one peer. <c>Reason</c> is why the last connection or attempt ended.</summary>
public sealed record LinkState(LinkStatus Status, TimeSpan RetryIn = default, string? Reason = null);

/// <summary>
/// A connected peer as the app sees it: send a frame, watch the link, retry now. A Seamdesk peer is a
/// <see cref="Supervisor"/>; a peer speaking another protocol can sit behind the same three members.
/// </summary>
public interface IPeer
{
    event Action<LinkState>? StateChanged;

    void Kick();

    /// <summary>False when the peer is not connected; the frame is dropped (crossing into a down peer does nothing).</summary>
    Task<bool> SendAsync(Frame frame, CancellationToken ct = default);
}

/// <summary>An authenticated link to one peer: a Seamdesk <see cref="Connection"/>, or a link in another protocol.</summary>
public interface ILink : IAsyncDisposable
{
    /// <summary>Machine id of the side that dialled. Used to pick one link when both directions come up.</summary>
    string DialedBy { get; }

    /// <summary>Completes with the reason once the link is closed, from either side.</summary>
    Task<string> Closed { get; }

    Task SendAsync(Frame frame, CancellationToken ct = default);
}

/// <summary>A <see cref="Supervisor{TLink}"/> of Seamdesk connections.</summary>
public sealed class Supervisor(Func<CancellationToken, Task<Connection>> dial, TimeProvider time, Func<double> random)
    : Supervisor<Connection>(dial, time, random);

/// <summary>
/// Keeps one peer connected, forever. Dials at start and on <see cref="Kick"/>; after a failure or a lost connection it
/// retries after 1 s, doubling to 30 s, with ±20 % jitter. A peer that refused our version is retried every 5 minutes.
/// It never disarms itself.
/// </summary>
public class Supervisor<TLink>(Func<CancellationToken, Task<TLink>> dial, TimeProvider time, Func<double> random) : IPeer
    where TLink : class, ILink
{
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan VersionRefusedRetry = TimeSpan.FromMinutes(5);

    private readonly Lock gate = new();
    private TLink? current;
    private TaskCompletionSource wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool kicked;

    public event Action<LinkState>? StateChanged;

    public TLink? Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    /// <summary>Retry now and restart the backoff (resume, unlock, address change, "Reconnect now").</summary>
    public void Kick()
    {
        lock (gate)
        {
            kicked = true;
            Wake();
        }
    }

    public async Task<bool> SendAsync(Frame frame, CancellationToken ct = default)
    {
        if (Current is not { } connection)
        {
            return false;
        }

        try
        {
            await connection.SendAsync(frame, ct);
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return false; // the connection closed itself; RunAsync takes it from here
        }
    }

    /// <summary>
    /// A Ready connection, dialled by us or accepted from the peer. The first one wins. When both directions come up
    /// together, both machines keep the one dialled by the lower machine id; a newer connection from the same dialer
    /// replaces the old one (the dialer has given up on it). Returns false if <paramref name="connection"/> was dropped.
    /// </summary>
    public bool Offer(TLink connection)
    {
        TLink? loser;
        lock (gate)
        {
            if (current is null || current.Closed.IsCompleted
                || connection.DialedBy == current.DialedBy || string.CompareOrdinal(connection.DialedBy, current.DialedBy) < 0)
            {
                (loser, current) = (current, connection);
            }
            else
            {
                loser = connection;
            }

            Wake();
        }

        loser?.DisposeAsync();
        return loser != connection;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        int failures = 0;
        bool versionRefused = false;
        string? reason = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task woken = WakeTask();
            if (TakeKick())
            {
                failures = 0;
            }

            if (Current is { } connection)
            {
                StateChanged?.Invoke(new LinkState(LinkStatus.Connected));
                await Task.WhenAny(connection.Closed, woken).WaitAsync(ct);
                if (Drop(connection))
                {
                    reason = await connection.Closed;
                    failures = 1;
                }

                continue;
            }

            if (failures > 0)
            {
                TimeSpan delay = versionRefused ? VersionRefusedRetry : Backoff(failures);
                using CancellationTokenSource cancelDelay = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task delayed = Task.Delay(delay, time, cancelDelay.Token);
                StateChanged?.Invoke(new LinkState(LinkStatus.Waiting, delay, reason));
                await Task.WhenAny(delayed, woken).WaitAsync(ct);
                cancelDelay.Cancel();
                if (!delayed.IsCompletedSuccessfully)
                {
                    continue; // woken by Kick or an inbound connection: decide again
                }
            }

            StateChanged?.Invoke(new LinkState(LinkStatus.Connecting, Reason: reason));
            try
            {
                Offer(await dial(ct));
                failures = 0;
                versionRefused = false;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                failures++;
                reason = e.Message;
                versionRefused = e is RefusedException { VersionMismatch: true };
            }
        }
    }

    /// <summary>1 s, 2 s, 4 s ... capped at 30 s, times a jitter factor in [0.8, 1.2).</summary>
    private TimeSpan Backoff(int failures) =>
        TimeSpan.FromTicks(Math.Min(FirstRetry.Ticks << Math.Min(failures - 1, 5), MaxRetry.Ticks)) * (0.8 + (0.4 * random()));

    private bool Drop(TLink connection)
    {
        lock (gate)
        {
            if (current != connection || !connection.Closed.IsCompleted)
            {
                return false;
            }

            current = null;
            return true;
        }
    }

    private Task WakeTask()
    {
        lock (gate)
        {
            return wake.Task;
        }
    }

    private bool TakeKick()
    {
        lock (gate)
        {
            (bool was, kicked) = (kicked, false);
            return was;
        }
    }

    // Callers hold the gate. The old task completes and a fresh one is armed for the next wait.
    private void Wake()
    {
        TaskCompletionSource old = wake;
        wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        old.TrySetResult();
    }
}
