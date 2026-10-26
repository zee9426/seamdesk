using Seamdesk.Core;

namespace Seamdesk.Core.Tests;

/// <summary>
/// Example desk: portrait + offset 1080p + laptop below. PC: portrait 1440x2560 at (0,0) and a 1080p at (1440,25), as
/// Windows reports them. Laptop: a 1440p at (0,0) plus its own 1920x1200 panel to the right, and on the desk the 1440p
/// sits directly below the PC's 1080p (desk y = 25 + 1080 = 1105).
/// </summary>
public static class ExampleDesk
{
    public static Desk Create(int laptopDeskX) => new(
    [
        new Machine("pc", new Point(0, 0), [new Rect(0, 0, 1440, 2560), new Rect(1440, 25, 1920, 1080)]),
        new Machine("laptop", new Point(laptopDeskX, 1105), [new Rect(0, 0, 2560, 1440), new Rect(2560, 0, 1920, 1200)]),
    ]);

    /// <summary>Laptop's 1440p flush against the portrait's right edge, so they touch for desk y 1105..2544.</summary>
    public static Desk Flush() => Create(1440);

    /// <summary>Laptop's 1440p 60 px right of the portrait: still under the 1080p, but not touching the portrait.</summary>
    public static Desk Gap() => Create(1500);
}
