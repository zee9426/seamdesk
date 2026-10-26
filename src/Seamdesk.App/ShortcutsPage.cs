using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Seamdesk.Host;
using Modifiers = Seamdesk.Host.Modifiers;

namespace Seamdesk.App;

/// <summary>The two hotkeys, each with a capture field: press the new shortcut, see at once if it clashes.</summary>
internal sealed class ShortcutsPage : UserControl
{
    private readonly ISeamdesk seamdesk;
    private readonly ContentControl snapBack = new() { Focusable = false };
    private readonly ContentControl stay = new() { Focusable = false };

    public ShortcutsPage(ISeamdesk seamdesk)
    {
        this.seamdesk = seamdesk;
        Content = Ui.Page(Ui.PageBody(
            "Shortcuts",
            "They work on every machine while this PC's keyboard is in use, wherever the cursor is.",
            snapBack,
            stay));
        Show(true);
        Show(false);
        Unloaded += (_, _) => Done(); // closed while listening: the hotkeys come back
    }

    private Hotkey Current(bool isSnapBack)
    {
        string? text = isSnapBack ? seamdesk.Settings.SnapBackHotkey : seamdesk.Settings.StayHotkey;
        try
        {
            return text is null ? (isSnapBack ? Hotkey.DefaultSnapBack : Hotkey.DefaultStay) : Hotkey.Parse(text);
        }
        catch (FormatException)
        {
            return isSnapBack ? Hotkey.DefaultSnapBack : Hotkey.DefaultStay;
        }
    }

    private static (string Title, string Description) Words(bool isSnapBack) => isSnapBack
        ? ("Snap back to this machine", "Brings the cursor home from any machine.")
        : ("Stay on this machine", "Turns crossing off until you press it again.");

    /// <summary>The resting state: the shortcut as keycaps, a note if it is a poor choice, and Change.</summary>
    public void Show(bool isSnapBack, string? note = null, bool warning = false)
    {
        Hotkey current = Current(isSnapBack);
        (string title, string description) = Words(isSnapBack);
        Hotkey also = isSnapBack ? Hotkey.AlsoSnapBack : Hotkey.AlsoStay;
        StackPanel controls = Ui.Horizontal(
            Ui.Keys(current.ToString()), new TextBlock { Text = "or", VerticalAlignment = VerticalAlignment.Center }.Margin(8, 0, 8, 0), Ui.Keys(also.ToString()).Margin(0, 0, 12, 0), Ui.Button("Change", () => Capture(isSnapBack)));
        if (current != (isSnapBack ? Hotkey.DefaultSnapBack : Hotkey.DefaultStay))
        {
            controls.Children.Add(Ui.Link("Reset", () => Use(isSnapBack, isSnapBack ? Hotkey.DefaultSnapBack : Hotkey.DefaultStay)).Margin(12, 0, 0, 0));
        }

        StackPanel card = Ui.Vertical(Ui.Row(Ui.Keyboard, title, description, controls));
        if (note is not null)
        {
            card.Children.Add(Ui.Message(note, warning ? "caution" : "critical"));
        }

        (isSnapBack ? snapBack : stay).Content = Ui.Card(card);
    }

    /// <summary>Listening: the next key with at least one modifier becomes the candidate. Esc cancels.</summary>
    public void Capture(bool isSnapBack, Hotkey? pressed = null)
    {
        seamdesk.PauseHotkeys(true);
        (string title, _) = Words(isSnapBack);
        Border field = new()
        {
            MinWidth = 220,
            MinHeight = 34,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1, 1, 1, 2),
            Padding = new Thickness(10, 4, 10, 4),
            Focusable = true,
            Child = pressed is { } p ? Ui.Keys(p.ToString()) : Ui.Text("Press a shortcut…", "Secondary"),
        };
        field.Brush(Border.BackgroundProperty, "TextControlBackgroundFocused").Brush(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
        System.Windows.Automation.AutomationProperties.SetName(field, $"New shortcut for {title}");
        field.PreviewKeyDown += (_, e) =>
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            e.Handled = true;
            if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                Done();
                Show(isSnapBack);
                return;
            }

            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                return;
            }

            Modifiers mods = (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? Modifiers.Ctrl : 0)
                | (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? Modifiers.Alt : 0)
                | (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Modifiers.Shift : 0)
                | (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows) ? Modifiers.Win : 0);
            Try(isSnapBack, mods, (ushort)KeyInterop.VirtualKeyFromKey(key));
        };
        StackPanel card = Ui.Vertical(Ui.Row(Ui.Keyboard, title, "Hold Ctrl, Alt, Shift or Win and press a key. Esc cancels.",
            Ui.Horizontal(field, Ui.Button("Cancel", () =>
            {
                Done();
                Show(isSnapBack);
            }).Margin(8, 0, 0, 0))));
        (isSnapBack ? snapBack : stay).Content = Ui.Card(card);
        field.Loaded += (_, _) => field.Focus();
    }

    /// <summary>A key pressed in the capture field: refused with the reason, or used (with a warning if apps use it too).</summary>
    public void Try(bool isSnapBack, Modifiers mods, ushort vk)
    {
        if (Hotkey.From(mods, vk) is not { } candidate)
        {
            Capture(isSnapBack);
            ShowRefusal(isSnapBack, mods == Modifiers.None ? "Use at least one of Ctrl, Alt, Shift or Win, so ordinary typing still works." : "Seamdesk can't use that key. Try a letter, a number, an F key, or Home, End, Insert, Page Up or Page Down.");
            return;
        }

        string? problem = candidate.Problem(Current(!isSnapBack), out bool blocked);
        if (blocked)
        {
            Capture(isSnapBack, candidate);
            ShowRefusal(isSnapBack, problem + " Try another.");
            return;
        }

        Use(isSnapBack, candidate);
        Show(isSnapBack, problem, warning: true);
    }

    private void ShowRefusal(bool isSnapBack, string why) =>
        ((StackPanel)((Border)(isSnapBack ? snapBack : stay).Content).Child).Children.Add(Ui.Message(why));

    private void Use(bool isSnapBack, Hotkey hotkey)
    {
        Done();
        seamdesk.Change(s =>
        {
            if (isSnapBack)
            {
                s.SnapBackHotkey = hotkey.ToString();
            }
            else
            {
                s.StayHotkey = hotkey.ToString();
            }
        });
        Show(isSnapBack);
    }

    private void Done() => seamdesk.PauseHotkeys(false);
}
