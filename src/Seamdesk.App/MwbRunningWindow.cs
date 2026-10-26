using System.Windows;
using System.Windows.Controls;

namespace Seamdesk.App;

/// <summary>
/// Shown at start while Mouse Without Borders runs: why Seamdesk won't start beside it, and the choice. MWB is closed only
/// after the user clicks "Quit Mouse Without Borders and start Seamdesk".
/// </summary>
internal sealed class MwbRunningWindow : Window
{
    private readonly StackPanel body;

    public MwbRunningWindow(string process, Func<bool> quitMwb)
    {
        Title = "Seamdesk";
        Icon = AppIcon.Image;
        SizeToContent = SizeToContent.Height;
        Width = 520;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Ui.UseTheme(Application.Current);

        Button quit = Ui.Button("Quit Mouse Without Borders and start Seamdesk", () => { }, accent: true);
        quit.IsDefault = true;
        Button cancel = Ui.Button("Cancel", () => DialogResult = false);
        cancel.IsCancel = true;
        quit.Click += (_, _) =>
        {
            if (quitMwb())
            {
                DialogResult = true;
            }
            else
            {
                Problem("Mouse Without Borders didn't quit. Quit it from its tray icon, then try again.");
            }
        };

        body = Body(process, quit, cancel);
        base.Content = body;
    }

    /// <summary>The dialog's content, without the window: tools/Shots draws it.</summary>
    public static StackPanel Body(string process, Button quit, Button cancel)
    {
        StackPanel buttons = Ui.Horizontal(quit.Margin(0, 0, 8, 0), cancel);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 24, 0, 0);
        StackPanel body = Ui.Vertical(
            Ui.Horizontal(Ui.Icon(Ui.Info, 24).Brush(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush").Margin(0, 0, 12, 0),
                Ui.Text("Mouse Without Borders is running", "SubtitleTextBlockStyle")),
            Ui.Text("Seamdesk and Mouse Without Borders both take over the mouse and keyboard, so only one of them can run at a time.", "BodyTextBlockStyle").Margin(0, 16, 0, 8),
            Ui.Text("To use Seamdesk, Mouse Without Borders has to quit. You can go back to it any time from Seamdesk's tray menu.", "Secondary"),
            Ui.Text($"Running now: {process}", "Caption").Margin(0, 8, 0, 0),
            buttons);
        body.Margin = new Thickness(24);
        return body.Root();
    }

    private void Problem(string text)
    {
        if (body.Children[^2] is not Border)
        {
            body.Children.Insert(body.Children.Count - 1, Ui.Message(text));
        }
    }
}
