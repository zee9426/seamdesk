using Seamdesk.Core;
using Seamdesk.Host;

namespace Seamdesk.Host.Tests;

public class ScreensTests
{
    [Fact]
    public void GetMonitorInfo_edges_become_desk_rects_and_the_cursor_parks_on_the_primary()
    {
        // The example PC as GetMonitorInfo lists it, 1080p first: edges are left, top, right, bottom (exclusive).
        Screens pc = new([new Display(1440, 25, 3360, 1105, false), new Display(0, 0, 1440, 2560, true)]);

        Machine m = pc.ToMachine("pc", new Point(0, 0));
        Assert.Equal([new Rect(1440, 25, 1920, 1080), new Rect(0, 0, 1440, 2560)], m.Monitors);
        Assert.Equal(new Rect(0, 0, 3360, 2560), pc.Virtual);
        Assert.Equal(new Point(720, 1280), pc.Park);
    }

    public static TheoryData<string, Display[]> Desktops => new()
    {
        { "example PC, 3360x2560 from (0,0)", [new(0, 0, 1440, 2560, true), new(1440, 25, 3360, 1105, false)] },
        { "a 1080p left of the primary: the virtual desktop starts at x = -1920", [new(-1920, 300, 0, 1380, false), new(0, 0, 2560, 1440, true)] },
    };

    /// <summary>
    /// MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK maps 0..65535 over the virtual desktop. Whether Windows divides by
    /// 65536 or 65535 and floors, every pixel must come back as itself, or 1:1 moves drift by a pixel.
    /// </summary>
    [Theory]
    [MemberData(nameof(Desktops))]
    public void Normalized_coordinates_land_on_every_pixel(string @case, Display[] monitors)
    {
        _ = @case;
        Screens s = new(monitors);
        Rect v = s.Virtual;
        for (int x = v.Left; x < v.Right; x++)
        {
            int n = s.Normalize(new Point(x, v.Top)).X;
            Assert.InRange(n, 0, 65535);
            Assert.Equal(x, v.Left + (int)((long)n * v.Width / 65536));
            Assert.Equal(x, v.Left + (int)((long)n * v.Width / 65535));
        }

        for (int y = v.Top; y < v.Bottom; y++)
        {
            int n = s.Normalize(new Point(v.Left, y)).Y;
            Assert.Equal(y, v.Top + (int)((long)n * v.Height / 65536));
            Assert.Equal(y, v.Top + (int)((long)n * v.Height / 65535));
        }
    }
}
