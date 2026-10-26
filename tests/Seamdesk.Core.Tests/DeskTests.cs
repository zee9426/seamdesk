using Seamdesk.Core;

namespace Seamdesk.Core.Tests;

public class DeskTests
{
    public static TheoryData<string, string, int, int, int, int, string, int, int> FlushCases => new()
    {
        // case, from machine, x, y, dx, dy, expected machine, x, y
        { "1080p down onto laptop 1440p, same desk x, two pixels in", "pc", 2000, 1104, 0, 1, "laptop", 560, 2 },
        { "laptop 1440p up onto 1080p, same desk x", "laptop", 560, 0, 0, -1, "pc", 2000, 1102 },
        { "big downward delta clamps to 1080p bottom, then crosses", "pc", 2000, 1000, 0, 200, "laptop", 560, 2 },
        { "laptop 1440p up where the narrower 1080p does not reach: stays", "laptop", 2000, 0, 0, -1, "laptop", 2000, 0 },
        { "portrait right-upper onto 1080p is a local move", "pc", 1439, 500, 1, 0, "pc", 1440, 500 },
        { "portrait right above the 1080p's top: stays", "pc", 1439, 10, 5, 0, "pc", 1439, 10 },
        { "portrait right-lower onto touching laptop 1440p", "pc", 1439, 1500, 1, 0, "laptop", 2, 395 },
        { "portrait right-lower from inside, through the PC's dead zone", "pc", 1400, 1500, 100, 0, "laptop", 2, 395 },
        { "laptop 1440p left onto portrait", "laptop", 0, 395, -1, 0, "pc", 1437, 1500 },
        { "portrait right below the laptop's bottom: stays", "pc", 1439, 2550, 1, 0, "pc", 1439, 2550 },
        { "portrait bottom, nothing below: stays", "pc", 700, 2559, 0, 1, "pc", 700, 2559 },
        { "diagonal past the 1080p's bottom-right corner still crosses down", "pc", 3355, 1100, 10, 30, "laptop", 1919, 2 },
        { "remote delta is 1:1 despite a 4480x1440 bounding box", "laptop", 500, 500, 100, 100, "laptop", 600, 600 },
        { "dead zone below the laptop panel clamps to the nearest monitor (the panel)", "laptop", 2000, 1300, 700, 0, "laptop", 2700, 1199 },
    };

    [Theory]
    [MemberData(nameof(FlushCases))]
    public void Move_on_example_desk(string @case, string from, int x, int y, int dx, int dy, string to, int ex, int ey)
    {
        _ = @case;
        AssertMove(ExampleDesk.Flush(), from, x, y, dx, dy, to, ex, ey);
    }

    [Theory]
    [InlineData("pc", 2000, 1104, 0, 1)]
    [InlineData("laptop", 560, 0, 0, -1)]
    [InlineData("pc", 1439, 1500, 1, 0)]
    [InlineData("laptop", 0, 395, -1, 0)]
    public void A_pixel_back_after_crossing_does_not_cross_back(string from, int x, int y, int dx, int dy)
    {
        // The hand sliding along a shared edge jitters by a pixel. One pixel back stays on the machine it crossed to, and
        // short of its edge pixel, where the hook already counts a move outwards as a crossing (Cursor.Crossing).
        Desk desk = ExampleDesk.Flush();
        Location there = desk.Move(new Location(desk.Machines.Single(m => m.Name == from), new Point(x, y)), dx, dy);
        Location back = desk.Move(there, -dx, -dy);
        Assert.NotEqual(from, there.Machine.Name);
        Assert.Equal((there.Machine.Name, true), (back.Machine.Name, back.Machine.Contains(back.Point + new Point(-dx, -dy))));
    }

    [Fact]
    public void Portrait_right_lower_stays_when_laptop_does_not_touch()
    {
        AssertMove(ExampleDesk.Gap(), "pc", 1439, 1500, 1, 0, "pc", 1439, 1500);
    }

    private static void AssertMove(Desk desk, string from, int x, int y, int dx, int dy, string to, int ex, int ey)
    {
        Machine start = desk.Machines.Single(m => m.Name == from);
        Location got = desk.Move(new Location(start, new Point(x, y)), dx, dy);
        Assert.Equal((to, new Point(ex, ey)), (got.Machine.Name, got.Point));
    }
}
