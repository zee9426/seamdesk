using System.Windows;
using System.Windows.Controls;

namespace Seamdesk.App;

/// <summary>The settings window: navigation on the left, one page at a time on the right, as Windows Settings.</summary>
internal sealed class SettingsWindow : Window
{
    public SettingsWindow(ISeamdesk seamdesk)
    {
        Title = "Seamdesk settings";
        Icon = AppIcon.Image;
        (Width, Height, MinWidth, MinHeight) = (1120, 760, 860, 580);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SettingsView view = new(seamdesk);
        Content = view;
        Closing += (_, e) =>
        {
            if (view.Layout?.IsDirty != true)
            {
                return;
            }

            MessageBoxResult answer = MessageBox.Show(this, "Save the changes to your layout?", "Seamdesk", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                view.Layout.Save();
            }

            e.Cancel = answer == MessageBoxResult.Cancel;
        };
    }
}

internal sealed class SettingsView : UserControl
{
    private readonly ISeamdesk seamdesk;
    private readonly ListBox nav = new() { BorderThickness = new Thickness(0), Background = null, Margin = new Thickness(12, 8, 12, 0) };
    private readonly ContentControl page = new() { Focusable = false };
    private readonly Dictionary<string, FrameworkElement> pages = [];

    public SettingsView(ISeamdesk seamdesk, string first = "Machines")
    {
        this.seamdesk = seamdesk;
        this.Root();
        Grid root = new();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(248) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        StackPanel side = new();
        side.Children.Add(Ui.Horizontal(Ui.AppIcon(20).Margin(0, 0, 12, 0), Ui.Text("Seamdesk", "BodyStrongTextBlockStyle")).Margin(24, 20, 0, 12));
        foreach ((string glyph, string name) in new[] { (Ui.Pc, "Machines"), (Ui.Tiles, "Layout"), (Ui.Keyboard, "Shortcuts"), (Ui.Gear, "Behaviour"), (Ui.Info, "About") })
        {
            ListBoxItem item = new ListBoxItem { Tag = name, Content = Ui.Horizontal(Ui.Icon(glyph).Margin(0, 0, 16, 0), Ui.Text(name)) }.Styled("NavItem");
            System.Windows.Automation.AutomationProperties.SetName(item, name);
            nav.Items.Add(item);
        }

        side.Children.Add(nav);
        root.Children.Add(side);

        Border content = new() { Child = page, CornerRadius = new CornerRadius(8, 0, 0, 0), BorderThickness = new Thickness(1, 1, 0, 0) };
        content.Brush(Border.BackgroundProperty, "LayerFillColorDefaultBrush").Brush(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Grid.SetColumn(content, 1);
        root.Children.Add(content);
        Content = root;

        nav.SelectionChanged += (_, _) => page.Content = Page((string)((ListBoxItem)nav.SelectedItem).Tag);
        Show(first);
    }

    public LayoutPage? Layout => pages.GetValueOrDefault("Layout") as LayoutPage;

    public void Show(string name) => nav.SelectedItem = nav.Items.Cast<ListBoxItem>().First(i => (string)i.Tag == name);

    public FrameworkElement Page(string name)
    {
        if (!pages.TryGetValue(name, out FrameworkElement? p))
        {
            pages[name] = p = name switch
            {
                "Machines" => new MachinesPage(seamdesk),
                "Layout" => new LayoutPage(seamdesk),
                "Shortcuts" => new ShortcutsPage(seamdesk),
                "Behaviour" => Ui.Page(Behaviour()),
                _ => Ui.Page(About()),
            };
        }

        return p;
    }

    private FrameworkElement Behaviour() => Ui.PageBody(
        "Behaviour",
        "How the cursor behaves when it reaches the edge of a screen.",
        Ui.Card(Ui.Row(Ui.Monitor, "Pause while a fullscreen app is in front",
            "Games, videos and slideshows keep the cursor on this PC. Your shortcuts still work.",
            Ui.Toggle(seamdesk.Settings.AutoPauseFullscreen, on => seamdesk.Change(s => s.AutoPauseFullscreen = on), "Pause while a fullscreen app is in front"))));

    private FrameworkElement About() => Ui.PageBody(
        "About",
        "Seamdesk shares one mouse and keyboard between your Windows PCs, screen by screen.",
        Ui.Card(Ui.Row(null, "Seamdesk", $"Version {seamdesk.Version}", Ui.AppIcon(32))),
        Ui.Card(Ui.Row(Ui.Code, "Source code", "Open source under the MIT licence.", Ui.Text("github.com/zee9426/seamdesk", "BodyStrongTextBlockStyle"))),
        Ui.Card(Ui.Row(Ui.Shield, "Privacy", "Seamdesk talks only to machines on your own network. It sends nothing anywhere else.", null)),
        Ui.Card(Ui.Row(Ui.Info, "Not affiliated with Microsoft", "Seamdesk is independent. Mouse Without Borders and PowerToys are Microsoft products.", null)));
}
