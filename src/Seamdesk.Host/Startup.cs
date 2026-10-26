namespace Seamdesk.Host;

/// <summary>
/// The watchdog's view of startup: the step it's on and whether that has gone on too long. A step that waits for the
/// user (a dialog) doesn't count, and the clock starts again after it.
/// </summary>
public sealed class Startup(TimeProvider time, TimeSpan limit)
{
    private readonly Lock gate = new();
    private long since = time.GetTimestamp();
    private string step = "starting";
    private bool waiting;
    private bool up;

    public void Begin(string step, bool waitsForUser = false)
    {
        lock (gate)
        {
            if (waiting)
            {
                since = time.GetTimestamp();
            }

            (this.step, waiting) = (step, waitsForUser);
        }
    }

    /// <summary>The tray is up: startup is over.</summary>
    public void Up()
    {
        lock (gate)
        {
            up = true;
        }
    }

    public bool IsUp
    {
        get
        {
            lock (gate)
            {
                return up;
            }
        }
    }

    /// <summary>The step startup is stuck on, or null if it's up, waiting for the user or still within the limit.</summary>
    public string? Stuck()
    {
        lock (gate)
        {
            return !up && !waiting && time.GetElapsedTime(since) >= limit ? step : null;
        }
    }
}
