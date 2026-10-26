using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Seamdesk.Core;
using Seamdesk.Host;
using Point = Seamdesk.Core.Point;
using Touch = Seamdesk.Host.Touch;
using Rect = Seamdesk.Core.Rect;
using WinPoint = System.Windows.Point;
using WinRect = System.Windows.Rect;

namespace Seamdesk.App;

/// <summary>
/// The desk, drawn to scale: every monitor of every machine, the edges where the cursor crosses, and dragging whole
/// machines with snapping. All the decisions are <see cref="LayoutEditor"/>'s; this only draws and forwards input.
/// </summary>
internal sealed class DeskView : FrameworkElement
{
    // Machine tints (docs/ui.md), light then dark; this PC is always the first.
    private static readonly Color[] Light = [Rgb(0xE8F0FB), Rgb(0xEAF6EC), Rgb(0xFBEFE6), Rgb(0xF1EAFB)];
    private static readonly Color[] Dark = [Rgb(0x22344A), Rgb(0x22392A), Rgb(0x41301F), Rgb(0x33284A)];
    private const double Inset = 24, SnapPixels = 10;

    private readonly LayoutEditor editor;
    private (double Scale, double X, double Y)? frozen;
    private (string Name, Point Offset, WinPoint Mouse)? drag;

    public DeskView(LayoutEditor editor)
    {
        this.editor = editor;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
        ClipToBounds = true;
        System.Windows.Automation.AutomationProperties.SetName(this, "Desk layout. Arrow keys move the selected machine.");
    }

    /// <summary>Raised after a drag step, a nudge or a selection.</summary>
    public event Action? Edited;

    public string? Selected { get; set; }

    /// <summary>How each monitor is named: "Display 2".</summary>
    public Func<Machine, int, string> Label { get; set; } = (_, i) => $"Display {i + 1}";

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        IReadOnlyList<Machine> machines = editor.Machines;
        dc.DrawRectangle(Brushes.Transparent, null, new WinRect(RenderSize));
        if (machines.Count == 0 || machines.All(m => m.Monitors.Count == 0))
        {
            return;
        }

        (double s, double ox, double oy) = Transform();
        bool dark = IsDark();
        HashSet<string> overlapping = LayoutEditor.Overlap(machines) is var (a, b) ? [a, b] : [];
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        WinRect Screen(Rect r) => new(ox + (r.Left * s), oy + (r.Top * s), r.Width * s, r.Height * s);

        for (int mi = 0; mi < machines.Count; mi++)
        {
            Machine m = machines[mi];
            Color tint = (dark ? Dark : Light)[mi % Light.Length];
            bool selected = m.Name == Selected;
            Pen pen = overlapping.Contains(m.Name) ? new Pen(Resource("SystemFillColorCriticalBrush"), 2)
                : selected ? new Pen(Resource("AccentFillColorDefaultBrush"), 2)
                : new Pen(Resource("ControlStrongStrokeColorDefaultBrush"), 1);
            List<Rect> onDesk = [.. LayoutEditor.OnDesk(m)];
            for (int i = 0; i < onDesk.Count; i++)
            {
                WinRect r = Screen(onDesk[i]);
                r.Inflate(-1.5, -1.5); // a hairline gap shows where two monitors meet
                dc.DrawRoundedRectangle(new SolidColorBrush(tint), pen, r, 4, 4);
                DrawLabels(dc, r, m, i, dip);
            }
        }

        IReadOnlyList<Touch> touches = editor.Touches;
        Brush accent = Resource("AccentFillColorDefaultBrush");
        for (int t = 0; t < touches.Count; t++)
        {
            Touch touch = touches[t];
            Machine from = machines.First(m => m.Name == touch.From);
            Rect edge = LayoutEditor.OnDesk(from).ElementAt(touch.FromMonitor);
            WinRect bar = touch.Edge switch
            {
                Direction.Right => new(ox + (edge.Right * s) - 2, oy + (touch.Start * s), 4, touch.Length * s),
                Direction.Left => new(ox + (edge.Left * s) - 2, oy + (touch.Start * s), 4, touch.Length * s),
                Direction.Down => new(ox + (touch.Start * s), oy + (edge.Bottom * s) - 2, touch.Length * s, 4),
                _ => new(ox + (touch.Start * s), oy + (edge.Top * s) - 2, touch.Length * s, 4),
            };
            dc.DrawRoundedRectangle(accent, null, bar, 2, 2);
            WinPoint mid = new(bar.X + (bar.Width / 2), bar.Y + (bar.Height / 2));
            dc.DrawEllipse(accent, new Pen(Resource("SolidBackgroundFillColorBaseBrush"), 2), mid, 10, 10);
            FormattedText n = Format((t + 1).ToString(CultureInfo.CurrentCulture), 11, FontWeights.SemiBold, Resource("TextOnAccentFillColorPrimaryBrush"), dip);
            dc.DrawText(n, new WinPoint(mid.X - (n.Width / 2), mid.Y - (n.Height / 2)));
        }
    }

    private void DrawLabels(DrawingContext dc, WinRect r, Machine m, int i, double dip)
    {
        if (r.Width < 48 || r.Height < 36)
        {
            return;
        }

        double width = r.Width - 20;
        double y = r.Top + 10;
        foreach ((string text, double size, FontWeight weight, string brush) in new[]
        {
            (m.Name, 13.0, FontWeights.SemiBold, "TextFillColorPrimaryBrush"),
            (Label(m, i), 12.0, FontWeights.Normal, "TextFillColorPrimaryBrush"),
            ($"{m.Monitors[i].Width} × {m.Monitors[i].Height}", 12.0, FontWeights.Normal, "TextFillColorSecondaryBrush"),
        })
        {
            FormattedText f = Format(text, size, weight, Resource(brush), dip);
            f.MaxTextWidth = width;
            f.MaxLineCount = 1;
            f.Trimming = TextTrimming.CharacterEllipsis;
            if (y + f.Height > r.Bottom - 6)
            {
                return;
            }

            dc.DrawText(f, new WinPoint(r.Left + 10, y));
            y += f.Height + 1;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        WinPoint at = e.GetPosition(this);
        (double s, double ox, double oy) = Transform();
        Point desk = new((int)Math.Floor((at.X - ox) / s), (int)Math.Floor((at.Y - oy) / s));
        Machine? hit = editor.Machines.LastOrDefault(m => LayoutEditor.OnDesk(m).Any(r => r.Contains(desk)));
        Selected = hit?.Name ?? Selected;
        if (hit is not null)
        {
            frozen = (s, ox, oy); // the scale holds still while dragging, or the desk would slide under the pointer
            drag = (hit.Name, hit.Offset, at);
            CaptureMouse();
        }

        Edited?.Invoke();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (drag is not { } d || frozen is not { } f)
        {
            return;
        }

        Vector moved = e.GetPosition(this) - d.Mouse;
        editor.MoveTo(d.Name, d.Offset + new Point((int)Math.Round(moved.X / f.Scale), (int)Math.Round(moved.Y / f.Scale)), (int)Math.Ceiling(SnapPixels / f.Scale));
        Edited?.Invoke();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        drag = null;
        frozen = null;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 10;
        (int dx, int dy) = e.Key switch
        {
            Key.Left => (-step, 0),
            Key.Right => (step, 0),
            Key.Up => (0, -step),
            Key.Down => (0, step),
            _ => (0, 0),
        };
        if (Selected is null || (dx, dy) == (0, 0))
        {
            return;
        }

        editor.Nudge(Selected, dx, dy);
        Edited?.Invoke();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        Selected ??= editor.Machines.FirstOrDefault()?.Name;
        Edited?.Invoke();
        InvalidateVisual();
    }

    /// <summary>One scale for the whole desk: it fits with a margin, centred, plus room around it to drag into.</summary>
    private (double Scale, double X, double Y) Transform()
    {
        if (frozen is { } f)
        {
            return f;
        }

        Rect b = LayoutEditor.Bounds(editor.Machines.Where(m => m.Monitors.Count > 0));
        double room = Math.Max(b.Width, b.Height) * 0.04;
        double s = Math.Min((ActualWidth - (2 * Inset)) / (b.Width + (2 * room)), (ActualHeight - (2 * Inset)) / (b.Height + (2 * room)));
        s = Math.Max(s, 0.001);
        return (s, (ActualWidth / 2) - ((b.Left + (b.Width / 2.0)) * s), (ActualHeight / 2) - ((b.Top + (b.Height / 2.0)) * s));
    }

    private bool IsDark() => Resource("TextFillColorPrimaryBrush") is SolidColorBrush { Color: var c } && c.R > 128;

    private Brush Resource(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private static FormattedText Format(string text, double size, FontWeight weight, Brush brush, double dip) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), size, brush, dip);

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
