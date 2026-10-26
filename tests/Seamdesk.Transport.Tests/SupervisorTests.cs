using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Seamdesk.Transport.Tests;

/// <summary>A peer that never answers. Fake time; every state is checked in order.</summary>
public class SupervisorTests
{
    private readonly FakeTimeProvider time = new();
    private readonly Channel<LinkState> states = Channel.CreateUnbounded<LinkState>();

    [Fact]
    public async Task Backoff_doubles_from_1s_to_30s_with_jitter_and_never_stops()
    {
        // Jitter factors alternate 0.8 and 1.1: delays are 1, 2, 4, 8, 16, 30, 30, ... seconds times those.
        double[] randoms = [0.0, 0.75];
        int draws = 0;
        double[] expected = [0.8, 2.2, 3.2, 8.8, 12.8, 33, 24, 33, 24, 33, 24, 33];
        Start(_ => throw new IOException("unreachable"), () => randoms[draws++ % 2]);

        await Next(new LinkState(LinkStatus.Connecting));
        foreach (double seconds in expected)
        {
            await Next(new LinkState(LinkStatus.Waiting, TimeSpan.FromSeconds(seconds), "unreachable"));
            time.Advance(TimeSpan.FromSeconds(seconds));
            await Next(new LinkState(LinkStatus.Connecting, Reason: "unreachable"));
        }
    }

    [Fact]
    public async Task Kick_retries_at_once_and_restarts_the_backoff()
    {
        Start(_ => throw new IOException("unreachable"), () => 0.5);
        await Next(new LinkState(LinkStatus.Connecting));
        foreach (int seconds in new[] { 1, 2 })
        {
            await Next(new LinkState(LinkStatus.Waiting, TimeSpan.FromSeconds(seconds), "unreachable"));
            time.Advance(TimeSpan.FromSeconds(seconds));
            await Next(new LinkState(LinkStatus.Connecting, Reason: "unreachable"));
        }

        await Next(new LinkState(LinkStatus.Waiting, TimeSpan.FromSeconds(4), "unreachable"));
        supervisor!.Kick();
        await Next(new LinkState(LinkStatus.Connecting, Reason: "unreachable"));
        await Next(new LinkState(LinkStatus.Waiting, TimeSpan.FromSeconds(1), "unreachable"));
    }

    [Fact]
    public async Task A_peer_that_refused_our_version_is_retried_every_5_minutes()
    {
        string reason = "alpha runs Seamdesk 0.1.0 (protocol 1), bravo runs Seamdesk 0.2.0 (protocol 2): update one of them";
        Start(_ => throw new RefusedException(reason, "0.1.0", versionMismatch: true), () => 0.5);
        await Next(new LinkState(LinkStatus.Connecting));
        for (int i = 0; i < 3; i++)
        {
            await Next(new LinkState(LinkStatus.Waiting, TimeSpan.FromMinutes(5), reason));
            time.Advance(TimeSpan.FromMinutes(5));
            await Next(new LinkState(LinkStatus.Connecting, Reason: reason));
        }
    }

    private Supervisor? supervisor;

    private void Start(Func<CancellationToken, Task<Connection>> dial, Func<double> random)
    {
        supervisor = new Supervisor(dial, time, random);
        supervisor.StateChanged += s => states.Writer.TryWrite(s);
        _ = supervisor.RunAsync(CancellationToken.None);
    }

    private async Task Next(LinkState expected) =>
        Assert.Equal(expected, await states.Reader.ReadAsync().AsTask().WaitAsync(Kit.Patience));
}
