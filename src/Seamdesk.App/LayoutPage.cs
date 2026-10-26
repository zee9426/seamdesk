using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Seamdesk.Core;
using Seamdesk.Host;
using System.Windows.Documents;
using Point = Seamdesk.Core.Point;
using Rect = Seamdesk.Core.Rect;

namespace Seamdesk.App;

/// <summary>
/// The layout editor: drag whole machines on the desk, see live where the cursor will cross, enter an MWB peer's
/// screens, then Save (applied at once, no restart) or Revert.
/// </summary>
internal sealed class LayoutPage : UserControl
{
    private readonly ISeamdesk seamdesk;
    private readonly StackPanel side = new();
    private readonly StackPanel crossings = new();
    private readonly StackPanel problems = new();
    private readonly Button save;
    private readonly Button revert;
    private readonly Border canvas = new();
    private LayoutEditor editor = null!;
    private DeskView desk = null!;
    private Dictionary<string, List<List<Rect>>> layouts = [];
    private Dictionary<string, int> showing = [];
    private List<string> waiting = [];
    private bool placedNew;

    public LayoutPage(ISeamdesk seamdesk)
    {
        this.seamdesk = seamdesk;
        save = Ui.Button("Save", Save, accent: true, glyph: Ui.Save);
        revert = Ui.Button("Revert", Load, glyph: Ui.Undo);

        Grid root = new();
        root.Margin = new Thickness(28, 24, 28, 24); // a little tighter than other pages: the desk wants the room
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 280 });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(272) });

        StackPanel head = Ui.Vertical(
            Ui.Text("Layout", "PageTitle"),
            Ui.Text("Drag each machine to where it sits on your desk. The cursor crosses wherever two machines' screens touch.", "PageIntro").Margin(0, 0, 0, 16));
        Grid.SetColumnSpan(head, 2);
        root.Children.Add(head);

        canvas.Styled("Card");
        canvas.Padding = new Thickness(0);
        Grid.SetRow(canvas, 1);
        root.Children.Add(canvas);

        ScrollViewer sideScroll = new() { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Margin = new Thickness(16, 0, 0, 0) };
        Grid.SetRow(sideScroll, 1);
        Grid.SetColumn(sideScroll, 1);
        root.Children.Add(sideScroll);

        Grid foot = new() { Margin = new Thickness(0, 16, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition());
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.Children.Add(Ui.Vertical(Ui.Text("Where the cursor crosses", "BodyStrongTextBlockStyle").Margin(0, 0, 0, 4), crossings, problems));
        StackPanel buttons = Ui.Horizontal(revert.Margin(0, 0, 8, 0), save);
        buttons.VerticalAlignment = VerticalAlignment.Bottom;
        buttons.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(buttons, 1);
        foot.Children.Add(buttons);
        Grid.SetRow(foot, 2);
        Grid.SetColumnSpan(foot, 2);
        root.Children.Add(foot);

        Content = root;
        Loaded += (_, _) => seamdesk.Changed += OnChanged;
        Unloaded += (_, _) => seamdesk.Changed -= OnChanged;
        Load();
    }

    /// <summary>A peer reported new screens, or pairing added one: show it, unless that would throw away the user's edits.</summary>
    private void OnChanged()
    {
        if (!IsDirty)
        {
            Load();
        }
    }

    public bool IsDirty => editor.IsDirty || placedNew || JsonSerializer.Serialize(layouts) != JsonSerializer.Serialize(Saved());

    /// <summary>Select a machine, as a click on it does.</summary>
    public void Select(string name)
    {
        desk.Selected = name;
        Update();
    }

    /// <summary>Starts again from the settings in force.</summary>
    public void Load()
    {
        Settings s = seamdesk.Settings;
        layouts = Saved();
        showing = s.MwbPeers.ToDictionary(p => p.Name, p => Math.Max(0, seamdesk.ActiveLayout(p.Name)));
        ConfiguredDesk configured = ConfiguredDesk.Saved(s, seamdesk.Me, seamdesk.MyMonitors, name => showing[name]);
        waiting = [.. configured.Waiting];
        placedNew = configured.PlacedNew;

        string? selected = desk?.Selected ?? seamdesk.Me;
        editor = new LayoutEditor(configured.Machines);
        desk = new DeskView(editor) { Selected = configured.Machines.Any(m => m.Name == selected) ? selected : null, Label = (m, i) => IsMwb(m.Name) ? $"Screen {i + 1}" : $"Display {i + 1}" };
        desk.Edited += Update;
        canvas.Child = desk;
        Update();
    }

    public void Save()
    {
        if (editor.Problem is not null || LayoutProblem() is not null)
        {
            return;
        }

        seamdesk.Change(s =>
        {
            foreach (Machine m in editor.Machines)
            {
                s.Offsets[m.Name] = m.Offset;
            }

            foreach (MwbPeerSettings p in s.MwbPeers)
            {
                p.Layouts = layouts[p.Name];
            }
        });
        Load();
    }

    private Dictionary<string, List<List<Rect>>> Saved() =>
        seamdesk.Settings.MwbPeers.ToDictionary(p => p.Name, p => p.Layouts.Select(l => l.ToList()).ToList());

    private bool IsMwb(string name) => layouts.ContainsKey(name);

    /// <summary>Everything that follows an edit: the crossings in words, problems, buttons and the side panel.</summary>
    private void Update()
    {
        desk.Refresh();
        crossings.Children.Clear();
        IReadOnlyList<Touch> touches = editor.Touches;
        for (int i = 0; i < touches.Count; i++)
        {
            crossings.Children.Add(Crossing(i + 1, touches[i]));
        }

        if (touches.Count == 0)
        {
            crossings.Children.Add(Ui.Text("Nowhere yet: drag machines so their screens touch.", "Secondary"));
        }

        problems.Children.Clear();
        foreach (Machine lone in editor.Machines.Where(m => editor.Machines.Count > 1 && !touches.Any(t => t.From == m.Name || t.To == m.Name)))
        {
            problems.Children.Add(Ui.Message($"{lone.Name} doesn't touch any other machine, so the cursor can't reach it.", "caution"));
        }

        foreach (string name in waiting)
        {
            problems.Children.Add(Ui.Message(IsMwb(name)
                ? $"{name} has no saved screens. Remove it on the Machines page and add it again."
                : $"{name} will appear here once it has connected.", "caution"));
        }

        if ((editor.Problem ?? LayoutProblem()) is { } problem)
        {
            problems.Children.Add(Ui.Message(problem));
        }

        save.IsEnabled = IsDirty && editor.Problem is null && LayoutProblem() is null;
        revert.IsEnabled = IsDirty;
        Side();
    }

    private Grid Crossing(int n, Touch t)
    {
        string edge = t.Edge switch { Direction.Left => "left", Direction.Right => "right", Direction.Up => "top", _ => "bottom" };
        Machine from = editor.Machines.First(m => m.Name == t.From), to = editor.Machines.First(m => m.Name == t.To);
        Border badge = new()
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock { Text = n.ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, FontWeight = FontWeights.SemiBold }
                .Brush(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush"),
        };
        badge.Brush(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
        TextBlock words = Ui.Text(
            $"{t.From} {desk.Label(from, t.FromMonitor)} ({Size(from.Monitors[t.FromMonitor])}), {edge} edge, meets {t.To} {desk.Label(to, t.ToMonitor)} for {t.Length:N0} px");
        Grid row = new() { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(badge);
        Grid.SetColumn(words, 1);
        row.Children.Add(words);
        return row;
    }

    private static string Size(Rect r) => $"{r.Width} × {r.Height}";

    // --- The side panel: the selected machine, and for an MWB peer its saved screen layouts

    private void Side()
    {
        side.Children.Clear();
        ComboBox pick = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (Machine m in editor.Machines)
        {
            pick.Items.Add(m.Name);
        }

        pick.SelectedItem = desk.Selected;
        pick.SelectionChanged += (_, _) =>
        {
            if (pick.SelectedItem is string chosen)
            {
                Select(chosen);
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(pick, "Machine");
        side.Children.Add(Ui.Field("Machine", pick, double.NaN));

        if (desk.Selected is not { } name || editor.Machines.FirstOrDefault(m => m.Name == name) is not { } machine)
        {
            side.Children.Add(Ui.Text("Click a machine to move it. With the desk focused, the arrow keys move it 10 px, or 1 px with Shift.", "Secondary"));
            return;
        }

        side.Children.Add(Ui.Text($"At {machine.Offset.X:N0}, {machine.Offset.Y:N0} on the desk. Drag it, or use the arrow keys (Shift for 1 px).", "Secondary").Margin(0, 0, 0, 16));
        if (!IsMwb(name))
        {
            string where = name == seamdesk.Me ? "this PC's" : $"{name}'s";
            side.Children.Add(Ui.Text($"These displays are arranged as in {where} Windows display settings. Change them there.", "Secondary"));
            return;
        }

        MwbScreens(name);
    }

    private void MwbScreens(string name)
    {
        List<List<Rect>> saved = layouts[name];
        int index = showing[name];
        int active = seamdesk.ActiveLayout(name);
        side.Children.Add(Ui.Text($"{name}'s screens", "BodyStrongTextBlockStyle").Margin(0, 0, 0, 4));
        side.Children.Add(Ui.Text(
            $"Mouse Without Borders doesn't tell Seamdesk about {name}'s screens. Enter them as {name}'s display settings show them. Save a layout for each setup, like lid open and lid closed: Seamdesk uses the one that fits.",
            "Caption").Margin(0, 0, 0, 12));

        ComboBox which = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        for (int i = 0; i < saved.Count; i++)
        {
            Rect box = Seamdesk.Mwb.MwbScreens.Box(saved[i]);
            which.Items.Add($"Layout {i + 1} · {box.Width} × {box.Height}" + (i == active ? " · in use now" : ""));
        }

        which.SelectedIndex = index;
        which.SelectionChanged += (_, _) => Show(name, which.SelectedIndex);
        StackPanel layoutButtons = Ui.Horizontal(
            Ui.Button("New layout", () =>
            {
                saved.Add([new Rect(0, 0, 1920, 1080)]);
                Show(name, saved.Count - 1);
            }, glyph: Ui.Add).Margin(0, 0, 8, 0));
        if (saved.Count > 1)
        {
            layoutButtons.Children.Add(Ui.Button("Remove", () =>
            {
                saved.RemoveAt(index);
                Show(name, Math.Min(index, saved.Count - 1));
            }, glyph: Ui.Delete));
        }

        side.Children.Add(Ui.Field("Layout", which, double.NaN));
        side.Children.Add(layoutButtons.Margin(0, 0, 0, 12));

        List<Rect> screens = saved[index];
        for (int i = 0; i < screens.Count; i++)
        {
            side.Children.Add(ScreenRow(name, screens, i));
        }

        side.Children.Add(Ui.Button("Add a screen", () =>
        {
            Rect box = Seamdesk.Mwb.MwbScreens.Box(screens);
            screens.Add(new Rect(box.Right, box.Top, 1920, 1080));
            Show(name, index);
        }, glyph: Ui.Add).Margin(0, 4, 0, 0));
    }

    /// <summary>One screen: width × height at x, y in the peer's own display coordinates.</summary>
    private Border ScreenRow(string name, List<Rect> screens, int i)
    {
        Rect r = screens[i];
        TextBox Box(int value, string label)
        {
            TextBox t = new() { Text = value.ToString(), MaxLength = 6 };
            Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            System.Windows.Automation.AutomationProperties.SetName(t, $"Screen {i + 1} {label}");
            return t;
        }

        TextBox w = Box(r.Width, "width"), h = Box(r.Height, "height"), x = Box(r.Left, "x"), y = Box(r.Top, "y");
        void Commit()
        {
            if (int.TryParse(w.Text, out int wv) && int.TryParse(h.Text, out int hv) && int.TryParse(x.Text, out int xv) && int.TryParse(y.Text, out int yv)
                && wv is >= 320 and <= 16384 && hv is >= 240 and <= 16384 && new Rect(xv, yv, wv, hv) != screens[i])
            {
                screens[i] = new Rect(xv, yv, wv, hv);
                Show(name, showing[name]);
            }
        }

        foreach (TextBox t in new[] { w, h, x, y })
        {
            t.LostFocus += (_, _) => Commit();
            t.OnEnter(Commit);
        }

        Grid head = Ui.Row(null, $"Screen {i + 1}", null, screens.Count > 1
            ? Ui.Link("Remove", () =>
            {
                screens.RemoveAt(i);
                Show(name, showing[name]);
            })
            : null);
        Grid fields = new() { Margin = new Thickness(0, 6, 0, 0) };
        foreach (double width in new[] { 64.0, 64, 24, 64 })
        {
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        }

        fields.RowDefinitions.Add(new RowDefinition());
        fields.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        fields.RowDefinitions.Add(new RowDefinition());
        void Put(UIElement e, int row, int column)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, column);
            fields.Children.Add(e);
        }

        Put(Ui.Text("Size", "Secondary").Margin(0, 6, 0, 0), 0, 0);
        Put(w, 0, 1);
        Put(Ui.Text("×", "Secondary").Margin(8, 6, 0, 0), 0, 2);
        Put(h, 0, 3);
        Put(Ui.Text("Position", "Secondary").Margin(0, 6, 0, 0), 2, 0);
        Put(x, 2, 1);
        Put(Ui.Text(",", "Secondary").Margin(8, 6, 0, 0), 2, 2);
        Put(y, 2, 3);
        return Ui.Card(Ui.Vertical(head, fields));
    }

    private void Show(string name, int index)
    {
        showing[name] = index;
        editor.SetMonitors(name, layouts[name][index]);
        desk.Selected = name;
        Update();
    }

    /// <summary>A problem with an MWB peer's entered screens: two screens of one layout overlap.</summary>
    private string? LayoutProblem()
    {
        foreach ((string name, List<List<Rect>> saved) in layouts)
        {
            for (int l = 0; l < saved.Count; l++)
            {
                List<Machine> each = [.. saved[l].Select((r, i) => new Machine($"Screen {i + 1}", default, [r]))];
                if (LayoutEditor.Overlap(each) is var (a, b))
                {
                    return $"{a} and {b} of {name}'s layout {l + 1} overlap. Change their position so they only touch.";
                }
            }

            for (int l = 0; l < saved.Count; l++)
            {
                for (int k = l + 1; k < saved.Count; k++)
                {
                    Rect one = Seamdesk.Mwb.MwbScreens.Box(saved[l]), two = Seamdesk.Mwb.MwbScreens.Box(saved[k]);
                    if ((one.Width, one.Height) == (two.Width, two.Height))
                    {
                        return $"{name}'s layouts {l + 1} and {k + 1} are the same size overall, so Seamdesk can't tell which one is in use. Remove one.";
                    }
                }
            }
        }

        return null;
    }
}
