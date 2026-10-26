using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Seamdesk.App;

/// <summary>Small factories for the settings window's building blocks, styled from Theme.xaml and Fluent's brushes.</summary>
internal static class Ui
{
    // Segoe Fluent Icons code points.
    public const string Monitor = "\uE7F4", Pc = "\uE977", Tiles = "\uF0E2", Keyboard = "\uE765", Gear = "\uE713", Info = "\uE946",
        Add = "\uE710", Chain = "\uE71B", Shield = "\uE83D", Warning = "\uE7BA", Paste = "\uE77F", Key = "\uE8D7", Delete = "\uE74D",
        Undo = "\uE7A7", Save = "\uE74E", Check = "\uE73E", Code = "\uE943", Laptop = "\uE7F8", Refresh = "\uE72C", Back = "\uE72B";

    public static readonly Uri ThemeUri = new("pack://application:,,,/Seamdesk;component/Theme.xaml");

    /// <summary>Fluent (following the system light or dark setting) plus Theme.xaml, once per application.</summary>
    public static void UseTheme(Application app)
    {
        if (!app.Resources.MergedDictionaries.Any(d => d.Source == ThemeUri))
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = ThemeUri });
        }
    }

    /// <summary>
    /// A root that no Window styles (the settings view, the tray menu, a dialog's body) takes Fluent's control text size
    /// and colour, not the system message font's and black.
    /// </summary>
    public static T Root<T>(this T e) where T : FrameworkElement
    {
        e.SetResourceReference(Control.FontSizeProperty, "ControlContentThemeFontSize");
        e.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        return e;
    }

    public static T Styled<T>(this T e, string style) where T : FrameworkElement
    {
        e.SetResourceReference(FrameworkElement.StyleProperty, style);
        return e;
    }

    public static T Brush<T>(this T e, DependencyProperty property, string brush) where T : DependencyObject
    {
        if (e is FrameworkElement fe)
        {
            fe.SetResourceReference(property, brush);
        }
        else if (e is FrameworkContentElement fce)
        {
            fce.SetResourceReference(property, brush);
        }

        return e;
    }

    public static T Margin<T>(this T e, double left, double top, double right, double bottom) where T : FrameworkElement
    {
        e.Margin = new Thickness(left, top, right, bottom);
        return e;
    }

    public static TextBlock Text(string text, string style = "BodyTextBlockStyle") =>
        new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }.Styled(style);

    public static TextBlock Icon(string glyph, double size = 16) => new TextBlock { Text = glyph, FontSize = size }.Styled("Icon");

    public static Border Card(UIElement child) => new Border { Child = child }.Styled("Card");

    /// <summary>A Settings-style row: icon, title, one line of explanation, and the control on the right.</summary>
    public static Grid Row(string? glyph, string title, string? description, UIElement? control) => Row(glyph, Text(title), description, control);

    public static Grid Row(string? glyph, UIElement title, string? description, UIElement? control) =>
        Row(glyph, title, description is null ? null : Text(description, "Caption"), control);

    public static Grid Row(string? glyph, UIElement title, UIElement? description, UIElement? control)
    {
        Grid g = new();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (glyph is not null)
        {
            g.Children.Add(Icon(glyph, 20).Margin(0, 0, 16, 0));
        }

        StackPanel words = new() { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(title);
        if (description is not null)
        {
            words.Children.Add(description);
        }

        Grid.SetColumn(words, 1);
        g.Children.Add(words);
        if (control is FrameworkElement c)
        {
            c.VerticalAlignment = VerticalAlignment.Center;
            c.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(c, 2);
            g.Children.Add(c);
        }

        return g;
    }

    /// <summary>A row under a divider inside a card.</summary>
    public static Border SubRow(UIElement child) => new Border { Child = child }.Styled("CardRow");

    public static Button Button(string text, Action click, bool accent = false, string? glyph = null)
    {
        object content = text;
        if (glyph is not null)
        {
            StackPanel p = new() { Orientation = Orientation.Horizontal };
            p.Children.Add(Icon(glyph, 14).Margin(0, 0, 8, 0));
            p.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            content = p;
        }

        Button b = new() { Content = content, MinWidth = 96, Padding = new Thickness(12, 5, 12, 6) };
        if (accent)
        {
            b.Styled("AccentButtonStyle");
        }
        else if (content is StackPanel custom)
        {
            // Fluent dims a disabled button's own text, but not icon-and-label content like this.
            b.IsEnabledChanged += (_, _) => custom.Opacity = b.IsEnabled ? 1 : 0.4;
        }

        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A link-style action inside text.</summary>
    public static TextBlock Link(string text, Action click)
    {
        Hyperlink link = new(new Run(text));
        link.Click += (_, _) => click();
        return new TextBlock(link) { VerticalAlignment = VerticalAlignment.Center }.Styled("BodyTextBlockStyle");
    }

    public static StackPanel Horizontal(params UIElement[] children)
    {
        StackPanel p = new() { Orientation = Orientation.Horizontal };
        foreach (UIElement c in children)
        {
            p.Children.Add(c);
        }

        return p;
    }

    public static StackPanel Vertical(params UIElement[] children)
    {
        StackPanel p = new();
        foreach (UIElement c in children)
        {
            p.Children.Add(c);
        }

        return p;
    }

    public static Ellipse Dot(Health health) => new Ellipse().Styled("StatusDot").Brush(Shape.FillProperty, health switch
    {
        Health.Good => "SystemFillColorSuccessBrush",
        Health.Waiting => "SystemFillColorCautionBrush",
        Health.Problem => "SystemFillColorCriticalBrush",
        _ => "TextFillColorTertiaryBrush",
    });

    /// <summary>"● Connected", never colour alone.</summary>
    public static StackPanel Status(PeerStatus s) => Horizontal(Dot(s.Health), Text(s.Text, "Caption"));

    /// <summary>An inline message with its icon: "critical", "caution" or "success".</summary>
    public static Border Message(string text, string kind = "critical")
    {
        string glyph = kind == "success" ? Check : Warning;
        string brush = kind switch { "success" => "SystemFillColorSuccessBrush", "caution" => "SystemFillColorCautionBrush", _ => "SystemFillColorCriticalBrush" };
        TextBlock words = Text(text);
        Grid g = new();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(Icon(glyph, 14).Brush(TextBlock.ForegroundProperty, brush).Margin(0, 2, 10, 0));
        Grid.SetColumn(words, 1);
        g.Children.Add(words);
        ((TextBlock)g.Children[0]).VerticalAlignment = VerticalAlignment.Top;
        return new Border { Child = g, Tag = kind }.Styled("Message");
    }

    /// <summary>"Ctrl" "Alt" "Home" as keycaps.</summary>
    public static StackPanel Keys(string shortcut)
    {
        StackPanel p = new() { Orientation = Orientation.Horizontal };
        foreach (string key in shortcut.Split('+'))
        {
            p.Children.Add(new Border { Child = new TextBlock { Text = key, HorizontalAlignment = HorizontalAlignment.Center }.Styled("BodyStrongTextBlockStyle") }.Styled("Keycap"));
        }

        return p;
    }

    public static CheckBox Toggle(bool on, Action<bool> changed, string name)
    {
        CheckBox c = new CheckBox { IsChecked = on }.Styled("ToggleSwitch");
        System.Windows.Automation.AutomationProperties.SetName(c, name);
        c.Click += (_, _) => changed(c.IsChecked == true);
        return c;
    }

    /// <summary>A labelled field: the label above, as Fluent forms do.</summary>
    public static StackPanel Field(string label, Control input, double width = 280)
    {
        input.Width = width;
        input.HorizontalAlignment = HorizontalAlignment.Left;
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        return Vertical(Text(label, "Caption").Margin(0, 0, 0, 4), input).Margin(0, 0, 16, 8);
    }

    public static Image AppIcon(double size) => new() { Source = Seamdesk.App.AppIcon.Image, Width = size, Height = size };

    public static void OnEnter(this Control c, Action action) =>
        c.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                action();
                e.Handled = true;
            }
        };

    public static ScrollViewer Page(UIElement content) =>
        new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };

    public static FrameworkElement PageBody(string title, string intro, params UIElement[] children)
    {
        StackPanel body = new() { MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.SetResourceReference(FrameworkElement.MarginProperty, "PageMargin");
        body.Children.Add(Text(title, "PageTitle"));
        body.Children.Add(Text(intro, "PageIntro"));
        foreach (UIElement c in children)
        {
            body.Children.Add(c);
        }

        return body;
    }

}
