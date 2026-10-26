using Seamdesk.Core;

namespace Seamdesk.Mwb.Tests;

public class ScreensTests
{
    /// <summary>
    /// NextMachine coordinates as the peer's MWB computes them (MachineStuff.cs ConvertToUniversalValue, JUMP_PIXELS 2):
    /// leaving the top edge at x sends (x * 65535 / width, (height - 2) * 65535 / height) of its outer box.
    /// Saved layouts: 0 = 1440p + 1920x1200 panel (box 4480x1440, current), 1 = the panel alone (1920x1200).
    /// </summary>
    [Theory]
    [InlineData("1440p, x 1000: lands just above, on the PC", 1000 * 65535 / 4480, 1438 * 65535 / 1440, 1000, 1079, null, false)]
    [InlineData("1440p, x 0: the corner pixel", 0, 1438 * 65535 / 1440, 0, 1079, null, false)]
    [InlineData("panel, x 3000: nothing above it on the desk", 3000 * 65535 / 4480, 1438 * 65535 / 1440, null, null, null, false)]
    [InlineData("lid open, panel only, x 500: layout 1 fits", 500 * 65535 / 1920, 1198 * 65535 / 1200, 500, 1079, 1, false)]
    [InlineData("a box no saved layout fits", 30000, 65000, null, null, null, true)]
    public void A_hand_back_lands_where_the_desk_says_on_the_saved_layout_that_fits(string _, int x, int y, int? toX, int? toY, int? layout, bool unmatched)
    {
        MwbScreens screens = new([Kit.Both, Kit.PanelOnly]);

        HandBack back = screens.HandBack(Kit.Desk(Kit.Both), "pc", "peer", x, y);

        Assert.Equal(toX is null ? null : new Point(toX.Value, toY!.Value), back.To);
        Assert.Equal(layout is null ? null : (layout == 0 ? Kit.Both : Kit.PanelOnly), back.NewLayout);
        Assert.Equal(unmatched, back.Unmatched);
        Assert.Equal(layout == 1 ? Kit.PanelOnly : Kit.Both, screens.Current);
    }
}
