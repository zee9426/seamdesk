// Renders every window and state of Seamdesk's UI to PNGs, at 100 % and 150 %, light and dark, with RenderTargetBitmap.
// Offscreen only: no window is ever shown, so it runs over ssh in session 0 and never touches anyone's desktop.
//   dotnet tools/Shots/bin/Debug/net10.0-windows/Shots.dll <output folder>
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Seamdesk.App;
using Seamdesk.Core;
using Seamdesk.Host;
using Seamdesk.Transport;
using Point = Seamdesk.Core.Point;
using Rect = Seamdesk.Core.Rect;

namespace Seamdesk.Shots;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        string dir = Directory.CreateDirectory(args.FirstOrDefault() ?? "screenshots").FullName;
        Application app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        foreach ((string theme, ThemeMode mode) in new[] { ("light", ThemeMode.Light), ("dark", ThemeMode.Dark) })
        {
            app.ThemeMode = mode;
            Blue(app, theme == "dark");
            Ui.UseTheme(app);
            foreach ((string name, string title, Size size, Func<FrameworkElement> scene) in Scenes())
            {
                foreach (double scale in new[] { 1.0, 1.5 })
                {
                    string file = Path.Combine(dir, $"{name}-{theme}-{scale * 100:0}.png");
                    Render(Window(title, scene()), size, scale, file);
                    Console.WriteLine(file);
                }
            }
        }

        return 0;
    }

    private static IEnumerable<(string, string, Size, Func<FrameworkElement>)> Scenes()
    {
        Size settings = new(1120, 760), dialog = new(520, 300), menu = new(380, 330);
        SettingsView View(string page, Fake? seamdesk = null) => new(seamdesk ?? new Fake(), page);
        FrameworkElement Machines(Action<MachinesPage> state)
        {
            SettingsView v = View("Machines");
            state((MachinesPage)v.Page("Machines"));
            return v;
        }

        FrameworkElement Shortcuts(Action<ShortcutsPage> state)
        {
            SettingsView v = View("Shortcuts");
            state((ShortcutsPage)v.Page("Shortcuts"));
            return v;
        }

        yield return ("machines", "Seamdesk settings", settings, () => View("Machines"));
        yield return ("pairing-show-code", "Seamdesk settings", settings, () => Machines(p => p.ShowCode()));
        yield return ("pairing-enter-code", "Seamdesk settings", settings, () => Machines(p => p.EnterCode("STUDY-PC", "4821 0937")));
        yield return ("pairing-wrong-code", "Seamdesk settings", settings, () => Machines(p => p.EnterCode("STUDY-PC", "4821 0973", "That code isn't right. Check it and try again; a code allows 3 tries.")));
        yield return ("mwb-peer", "Seamdesk settings", settings, () => Machines(p => ((ScrollViewer)p.Content).ScrollToVerticalOffset(480)));
        yield return ("mwb-add", "Seamdesk settings", settings, () => Machines(p => p.AddMwb("LAPTOP", "LAPTOP-EXAMPLE")));
        yield return ("layout", "Seamdesk settings", settings, () =>
        {
            SettingsView v = View("Layout");
            ((LayoutPage)v.Page("Layout")).Select("LAPTOP");
            return v;
        });
        yield return ("layout-overlap", "Seamdesk settings", settings, () => View("Layout", new Fake(laptopAt: new Point(1400, 1105))));
        yield return ("shortcuts", "Seamdesk settings", settings, () => View("Shortcuts"));
        yield return ("shortcuts-capture", "Seamdesk settings", settings, () => Shortcuts(p =>
        {
            p.Capture(isSnapBack: true);
            p.Try(true, Seamdesk.Host.Modifiers.Win, 0x4C); // Win+L: Windows keeps it
        }));
        yield return ("shortcuts-warning", "Seamdesk settings", settings, () => Shortcuts(p => p.Try(false, Seamdesk.Host.Modifiers.Ctrl | Seamdesk.Host.Modifiers.Shift, 0x53)));
        yield return ("behaviour", "Seamdesk settings", settings, () => View("Behaviour"));
        yield return ("about", "Seamdesk settings", settings, () => View("About"));
        yield return ("tray-menu", "", menu, () =>
        {
            // A ContextMenu lives only in its own popup. Over ssh that opens on session 0's non-interactive desktop,
            // which nobody sees; the menu is drawn from there and closed.
            ContextMenu m = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute }.Root();
            Tray.Fill(m, new Fake(), () => { }, () => { }, () => { }, () => { });
            m.IsOpen = true;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            return m;
        });
        yield return ("mwb-running", "Seamdesk", dialog, () => MwbRunningWindow.Body("MouseWithoutBorders",
            Ui.Button("Quit Mouse Without Borders and start Seamdesk", () => { }, accent: true), Ui.Button("Cancel", () => { })));
    }

    /// <summary>
    /// Session 0 reports an orange accent, which reads like an error next to Seamdesk's red overlap warning. Draw with
    /// Windows 11's default blue accent, as most people see it, through the brushes Fluent derives from it.
    /// </summary>
    private static void Blue(Application app, bool dark)
    {
        Color fill = dark ? Rgb(0x60CDFF) : Rgb(0x005FB8), text = dark ? Rgb(0x99EBFF) : Rgb(0x003E92);
        foreach (string key in new[] { "AccentFillColorDefaultBrush", "AccentButtonBackground", "AccentButtonBorderBrush", "AccentControlElevationBorderBrush" })
        {
            app.Resources[key] = new SolidColorBrush(fill);
        }

        app.Resources["AccentFillColorSecondaryBrush"] = new SolidColorBrush(fill) { Opacity = 0.9 };
        app.Resources["AccentButtonBackgroundPointerOver"] = new SolidColorBrush(fill) { Opacity = 0.9 };
        app.Resources["AccentFillColorTertiaryBrush"] = new SolidColorBrush(fill) { Opacity = 0.8 };
        app.Resources["AccentButtonBackgroundPressed"] = new SolidColorBrush(fill) { Opacity = 0.8 };
        foreach (string key in new[] { "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush", "HyperlinkForeground", "HyperlinkButtonForeground", "TextControlFocusedBorderBrush" })
        {
            app.Resources[key] = new SolidColorBrush(text);
        }

        foreach (string key in new[] { "TextOnAccentFillColorPrimaryBrush", "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed" })
        {
            app.Resources[key] = new SolidColorBrush(dark ? Colors.Black : Colors.White);
        }

        static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// <summary>A plain Windows 11 frame around the content: title bar with the icon and caption buttons. Empty title: none.</summary>
    private static FrameworkElement Window(string title, FrameworkElement content)
    {
        if (title.Length == 0)
        {
            return content;
        }

        Border frame = new Border { BorderThickness = new Thickness(1) }.Root();
        frame.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        DockPanel dock = new();
        Grid bar = new() { Height = 32 };
        bar.Children.Add(Ui.Horizontal(Ui.AppIcon(16).Margin(12, 0, 10, 0), Ui.Text(title, "CaptionTextBlockStyle")).Margin(0, 8, 0, 0));
        StackPanel caption = Ui.Horizontal(
            Ui.Icon("\uE921", 10).Margin(17, 0, 17, 0), Ui.Icon("\uE922", 10).Margin(17, 0, 17, 0), Ui.Icon("\uE8BB", 10).Margin(17, 0, 17, 0));
        caption.HorizontalAlignment = HorizontalAlignment.Right;
        bar.Children.Add(caption);
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(content);
        frame.Child = dock;
        return frame;
    }

    private static void Render(FrameworkElement e, Size size, double scale, string file)
    {
        if (e is ContextMenu open)
        {
            size = new Size(open.ActualWidth, open.ActualHeight);
        }
        else
        {
            VisualTreeHelper.SetRootDpi(e, new DpiScale(scale, scale));
            e.Measure(size);
            e.Arrange(new System.Windows.Rect(size));
            e.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); // let bindings and layout settle
            e.UpdateLayout();
        }

        RenderTargetBitmap bitmap = new((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        if (e is ContextMenu)
        {
            // Inside its popup the menu sits at an offset; a brush draws just its content.
            DrawingVisual visual = new();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new VisualBrush(e), null, new System.Windows.Rect(size));
            }

            bitmap.Render(visual);
        }
        else
        {
            bitmap.Render(e);
        }
        PngBitmapEncoder png = new();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream f = File.Create(file);
        png.Save(f);
        if (e is ContextMenu m)
        {
            m.IsOpen = false;
        }
    }

    /// <summary>
    /// The example desk ("portrait + offset 1080p + laptop below"): this PC has a portrait 1440x2560 and a 1080p at
    /// (1440, 25); LAPTOP runs Mouse Without Borders, with two saved layouts (its 1440p alone, and with its 1920x1200
    /// panel to the right), the second in use, placed under the 1080p. STUDY-PC runs Seamdesk and is offline.
    /// </summary>
    private sealed class Fake(Point? laptopAt = null) : ISeamdesk
    {
        public string Me => "DESKTOP";

        public string Version => typeof(ISeamdesk).Assembly.GetName().Version!.ToString(3);

        public IReadOnlyList<Rect> MyMonitors { get; } = [new Rect(0, 0, 1440, 2560), new Rect(1440, 25, 1920, 1080)];

        public Settings Settings { get; private set; } = new()
        {
            Peers = [new Pin("00", "STUDY-PC", "192.168.1.31")],
            Offsets = { ["DESKTOP"] = new Point(0, 0), ["LAPTOP"] = laptopAt ?? new Point(1440, 1105) },
            Clipboard = { ["STUDY-PC"] = ClipboardMode.BothWays },
            MwbPeers =
            [
                new MwbPeerSettings
                {
                    Name = "LAPTOP", Host = "LAPTOP-EXAMPLE", LegacySecurity = true, Clipboard = ClipboardMode.ToPeer, Detected = MwbVersion.Current,
                    Layouts = [[new Rect(0, 0, 2560, 1440)], [new Rect(0, 0, 2560, 1440), new Rect(2560, 0, 1920, 1200)]],
                },
            ],
        };

        public event Action? Changed;

        public bool Staying => false;

        public PeerStatus Status(string peer) => peer == "LAPTOP"
            ? new PeerStatus("Connected", Health.Good)
            : new PeerStatus("Reconnecting…", Health.Problem, "Couldn't reach STUDY-PC: no answer on the network.");

        public int ActiveLayout(string mwbPeer) => 1;

        public void ToggleStay() => Changed?.Invoke();

        public void Apply(Settings changed) => Settings = changed;

        public PairingCode ShowPairingCode() => new(TimeProvider.System);

        public Task<string> PairAsync(string host, string code) => Task.FromResult(host);

        public void Remove(string peer)
        {
        }

        public bool HasMwbKey(string peer) => true;

        public string? SetMwbKey(string peer, string key) => null;

        public void PauseHotkeys(bool paused)
        {
        }
    }
}
