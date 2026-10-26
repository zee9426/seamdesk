namespace Seamdesk.Mwb;

/// <summary>
/// The timing lines of the send path while Seamdesk drives an MWB peer: a Mouse package opens a one-second window, and when
/// it ends one line goes to the log. Heartbeats and handshake answers open none, so an idle link logs nothing. Counts and
/// times only, never what a package carries.
/// </summary>
internal sealed class SendTiming(TimeProvider time, Action<string> log, Func<string> tcp)
{
    private readonly Lock gate = new();
    private readonly List<double> delays = [];
    private readonly List<double> writes = [];
    private ITimer? window;
    private int mouse;
    private int waited;
    private int moves;
    private int events;
    private int mostEvents;

    /// <summary>A package written in <paramref name="ms"/>, after waiting for another write if <paramref name="queued"/>.</summary>
    public void Wrote(PackageType type, double ms, bool queued)
    {
        lock (gate)
        {
            if (window is null && type != PackageType.Mouse)
            {
                return;
            }

            Open();
            writes.Add(ms);
            mouse += type == PackageType.Mouse ? 1 : 0;
            waited += queued ? 1 : 0;
        }
    }

    /// <summary>A move summing <paramref name="count"/> hook events, the first at timestamp <paramref name="since"/>, is written.</summary>
    public void Moved(long since, int count)
    {
        lock (gate)
        {
            Open();
            delays.Add(time.GetElapsedTime(since).TotalMilliseconds);
            moves++;
            events += count;
            mostEvents = Math.Max(mostEvents, count);
        }
    }

    private void Open() => window ??= time.CreateTimer(_ => Flush(), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

    private void Flush()
    {
        string line;
        lock (gate)
        {
            line = $"driving, last second: {writes.Count} packages ({mouse} mouse, {waited} waited for another write), " +
                $"{moves} moves from {events} hook events (at most {mostEvents} in one), hook to written ms {Spread(delays)}, write ms {Spread(writes)}";
            window?.Dispose();
            window = null;
            delays.Clear();
            writes.Clear();
            mouse = waited = moves = events = mostEvents = 0;
        }

        log(line + tcp());
    }

    private static string Spread(List<double> ms)
    {
        if (ms.Count == 0)
        {
            return "none";
        }

        ms.Sort();
        return $"min {ms[0]:F2} med {ms[ms.Count / 2]:F2} p95 {ms[(int)(ms.Count * 0.95)]:F2} max {ms[^1]:F2}";
    }
}
