using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Seamdesk.Host;
using Forms = System.Windows.Forms;

namespace Seamdesk.App;

/// <summary>
/// The tray icon and its menu: each peer's status, "Stay on this machine", "Reconnect now", Settings, "Go back to Mouse
/// Without Borders" (if installed) and Quit. The menu is WPF's, so it wears the Fluent theme. Call it on the UI thread.
/// </summary>
internal sealed class Tray : IDisposable
{
    private readonly Forms.NotifyIcon icon = new() { Visible = true };
    private readonly ContextMenu menu;
    private readonly ISeamdesk seamdesk;

    public Tray(ISeamdesk seamdesk, Action reconnect, Action settings, Action quit, Action? backToMwb)
    {
        this.seamdesk = seamdesk;
        icon.Icon = AppIcon.Tray(Forms.SystemInformation.SmallIconSize.Width);
        menu = new ContextMenu { Placement = PlacementMode.MousePoint }.Root();
        menu.Opened += (_, _) => Fill(menu, seamdesk, reconnect, settings, quit, backToMwb);
        icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right)
            {
                menu.IsOpen = true;
                // A menu opened from the tray closes on an outside click only if its window is in the foreground.
                if (PresentationSource.FromVisual(menu) is HwndSource source)
                {
                    SetForegroundWindow(source.Handle);
                }
            }
        };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                settings();
            }
        };
        seamdesk.Changed += Refresh;
        Refresh();
    }

    /// <summary>The menu's items, from the app's state now. tools/Shots draws the same.</summary>
    public static void Fill(ContextMenu menu, ISeamdesk seamdesk, Action reconnect, Action settings, Action quit, Action? backToMwb)
    {
        menu.Items.Clear();
        Settings now = seamdesk.Settings;
        List<string> peers = [.. now.LinkedPeerNames];
        foreach (string peer in peers)
        {
            PeerStatus s = seamdesk.Status(peer);
            menu.Items.Add(new MenuItem
            {
                Header = Ui.Horizontal(Ui.Text(peer).Margin(0, 0, 12, 0), Ui.Status(s)),
                Icon = Ui.Icon(now.MwbPeers.Any(p => p.Name == peer) ? Ui.Laptop : Ui.Pc),
                IsHitTestVisible = false,
                Focusable = false,
            });
        }

        if (peers.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No machines yet", IsEnabled = false });
        }

        menu.Items.Add(new Separator());
        string stayKeys = $"{now.StayHotkey ?? Seamdesk.Host.Hotkey.DefaultStay.ToString()} or {Seamdesk.Host.Hotkey.AlsoStay}";
        menu.Items.Add(Item("Stay on this machine", seamdesk.ToggleStay, check: seamdesk.Staying, gesture: stayKeys));
        menu.Items.Add(Item("Reconnect now", reconnect, Ui.Refresh));
        menu.Items.Add(Item("Settings", settings, Ui.Gear));
        menu.Items.Add(new Separator());
        if (backToMwb is not null)
        {
            menu.Items.Add(Item("Go back to Mouse Without Borders", backToMwb, Ui.Back));
        }

        menu.Items.Add(Item("Quit Seamdesk", quit));
    }

    public void Notice(string text) => icon.ShowBalloonTip(5000, "Seamdesk", text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }

    private static MenuItem Item(string text, Action click, string? glyph = null, bool? check = null, string? gesture = null)
    {
        MenuItem item = new() { Header = text, InputGestureText = gesture ?? "" };
        if (check is { } on)
        {
            item.IsCheckable = true;
            item.IsChecked = on;
        }
        else if (glyph is not null)
        {
            item.Icon = Ui.Icon(glyph);
        }

        item.Click += (_, _) => click();
        return item;
    }

    private void Refresh()
    {
        IEnumerable<string> lines = seamdesk.Settings.LinkedPeerNames.Select(n => $"{n}: {seamdesk.Status(n).Text}");
        string tip = string.Join(Environment.NewLine, lines.Prepend("Seamdesk").Append(seamdesk.Staying ? "Staying on this machine" : ""));
        icon.Text = tip.TrimEnd().Length > 127 ? tip.TrimEnd()[..127] : tip.TrimEnd(); // NotifyIcon's limit
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);
}
