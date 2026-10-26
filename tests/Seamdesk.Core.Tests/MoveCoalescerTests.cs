using Seamdesk.Core;

namespace Seamdesk.Core.Tests;

public class MoveCoalescerTests
{
    [Fact]
    public void One_tick_of_8kHz_moves_becomes_one_summed_move()
    {
        var coalescer = new MoveCoalescer();
        for (int i = 0; i < 8; i++)
        {
            coalescer.Add(1, -2);
        }

        Assert.Equal((8, -16), coalescer.Take());
        Assert.Null(coalescer.Take());
    }

    [Fact]
    public async Task Deltas_added_while_the_sender_takes_are_never_lost()
    {
        var coalescer = new MoveCoalescer();
        const int perThread = 500_000;
        int sumX = 0, sumY = 0;
        var done = false;

        var taker = Task.Run(() =>
        {
            while (!Volatile.Read(ref done))
            {
                if (coalescer.Take() is var (x, y))
                {
                    sumX += x;
                    sumY += y;
                }
            }
        });
        Parallel.For(0, 2, _ =>
        {
            for (int i = 0; i < perThread; i++)
            {
                coalescer.Add(1, -1);
            }
        });
        Volatile.Write(ref done, true);
        await taker;
        if (coalescer.Take() is var (rx, ry))
        {
            sumX += rx;
            sumY += ry;
        }

        Assert.Equal((2 * perThread, -2 * perThread), (sumX, sumY));
    }
}
