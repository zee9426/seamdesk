namespace Seamdesk.Core;

/// <summary>
/// Sums pointer deltas from the input hook (up to 8 kHz) so the sender emits at most one move per tick.
/// The host must call <see cref="Take"/> before sending a button or wheel event, so ordering is kept.
/// </summary>
public sealed class MoveCoalescer
{
    private readonly Lock gate = new();
    private int dx;
    private int dy;

    public void Add(int deltaX, int deltaY)
    {
        lock (gate)
        {
            dx += deltaX;
            dy += deltaY;
        }
    }

    /// <summary>The summed delta since the last call, or null when the pointer has not moved.</summary>
    public (int Dx, int Dy)? Take()
    {
        lock (gate)
        {
            if (dx == 0 && dy == 0)
            {
                return null;
            }

            var sum = (dx, dy);
            dx = dy = 0;
            return sum;
        }
    }
}
