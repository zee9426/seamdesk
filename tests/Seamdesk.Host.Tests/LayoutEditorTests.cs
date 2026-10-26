using Seamdesk.Core;
using Seamdesk.Core.Tests;

namespace Seamdesk.Host.Tests;

public class LayoutEditorTests
{
    /// <summary>
    /// The example desk's crossings, hand-computed. PC: portrait (0,0) 1440x2560 and 1080p at (1440,25), bottom at desk
    /// y 1105. Laptop at desk (x, 1105): its 1440p 2560x1440, its panel 1920x1200 to the right.
    /// Flush (x 1440): the portrait's right edge meets the 1440p for y 1105..2544 (1440 px; above that the 1080p is the
    /// PC's own), and the 1080p's whole bottom (x 1440..3359) meets the 1440p. Gap (x 1500): the portrait touches
    /// nothing, and the 1080p's bottom meets the 1440p only from x 1500.
    /// </summary>
    [Fact]
    public void Touches_are_where_Desk_Cross_moves_the_cursor_once_per_pair()
    {
        Assert.Equal(
            [new Touch("pc", 0, Direction.Right, "laptop", 0, 1105, 1440), new Touch("pc", 1, Direction.Down, "laptop", 0, 1440, 1920)],
            LayoutEditor.FindTouches(ExampleDesk.Flush()));
        Assert.Equal([new Touch("pc", 1, Direction.Down, "laptop", 0, 1500, 1860)], LayoutEditor.FindTouches(ExampleDesk.Gap()));
    }

    /// <summary>
    /// Dragging the laptop by its offset, snap 20. Near (1450, 1100): x snaps -10 onto the portrait's right edge (and
    /// the 1080p's left), y snaps +5 onto the 1080p's bottom, not +20 onto the portrait's bottom. Far (1600, 1300): no
    /// edge within 20, so it stays. Monitors far apart on the other axis don't pull: the 1080p is level with the laptop
    /// only within 20 px of y 1105.
    /// </summary>
    [Theory]
    [InlineData(1450, 1100, 1440, 1105)]
    [InlineData(1440, 1125, 1440, 1120)] // two edges in range: the nearer wins (bottoms aligned, -5), not the 1080p's bottom (-20)
    [InlineData(1600, 1300, 1600, 1300)]
    [InlineData(3370, 1300, 3370, 1300)] // left edge 10 px right of the 1080p's right edge, but 195 px below it
    [InlineData(3370, 1110, 3360, 1105)] // near the 1080p's bottom-right corner: snaps onto it on both axes
    public void Dragging_snaps_to_the_nearest_edge_within_range(int x, int y, int snappedX, int snappedY)
    {
        LayoutEditor editor = new(ExampleDesk.Flush().Machines);

        editor.MoveTo("laptop", new Point(x, y), snap: 20);

        Assert.Equal(new Point(snappedX, snappedY), editor.Machines[1].Offset);
        Assert.Equal(new Point(0, 0), editor.Machines[0].Offset);
    }

    [Fact]
    public void Overlapping_machines_block_saving_and_revert_restores_the_saved_desk()
    {
        LayoutEditor editor = new(ExampleDesk.Flush().Machines);
        Assert.False(editor.IsDirty);

        editor.Nudge("laptop", -1, 0); // its 1440p now covers the portrait's last column

        Assert.True(editor.IsDirty);
        Assert.Equal("pc and laptop overlap. Move one so their screens only touch.", editor.Problem);

        editor.Revert();

        Assert.False(editor.IsDirty);
        Assert.Null(editor.Problem);
        Assert.Equal(new Point(1440, 1105), editor.Machines[1].Offset);
    }
}
