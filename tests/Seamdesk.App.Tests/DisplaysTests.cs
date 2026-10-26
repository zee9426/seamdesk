using System.Runtime.InteropServices;
using Seamdesk.App;
using Seamdesk.Core;
using Seamdesk.Host;

namespace Seamdesk.App.Tests;

public class DisplaysTests
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>
    /// Enumeration against an independent API: the monitors' bounding box is the virtual screen (SM_*VIRTUALSCREEN) and
    /// their count is SM_CMONITORS, with exactly one primary at (0, 0). Over ssh this sees session 0's single display.
    /// </summary>
    [Fact]
    public void Monitors_cover_the_virtual_screen_with_one_primary_at_the_origin()
    {
        Screens s = Displays.Read();

        Assert.Equal(GetSystemMetrics(80), s.Monitors.Count);
        Assert.Equal(new Rect(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79)), s.Virtual);
        Display primary = Assert.Single(s.Monitors, m => m.Primary);
        Assert.Equal((0, 0), (primary.Left, primary.Top));
    }
}
