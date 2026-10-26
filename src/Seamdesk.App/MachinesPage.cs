using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Seamdesk.Core;
using Seamdesk.Host;
using Seamdesk.Transport;
using System.Windows.Documents;
using Rect = Seamdesk.Core.Rect;

namespace Seamdesk.App;

/// <summary>This PC, every peer with its live status and options, and adding one: by pairing code, or as an MWB peer.</summary>
internal sealed class MachinesPage : UserControl
{
    private const string LegacyExplained = "Uses Mouse Without Borders' shared key, so it's only as safe as Mouse Without Borders.";

    private readonly ISeamdesk seamdesk;
    private readonly StackPanel peers = new();
    private readonly Border adding = new();
    private readonly Dictionary<string, (ContentControl Slot, TextBlock Detail, string Usual)> statuses = [];
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private string shape = "";

    public MachinesPage(ISeamdesk seamdesk)
    {
        this.seamdesk = seamdesk;
        Content = Ui.Page(Ui.PageBody(
            "Machines",
            "The machines that share this PC's mouse and keyboard. Each one runs Seamdesk, or Mouse Without Borders with legacy security.",
            Ui.Text("This PC", "Section").Margin(0, 0, 0, 8),
            Ui.Card(Ui.Row(Ui.Pc, seamdesk.Me, Describe(seamdesk.MyMonitors), Badge("This PC", accent: true))),
            peers,
            Ui.Text("Add a machine", "Section"),
            Ui.Card(Ui.Vertical(
                Ui.Row(Ui.Chain, "A machine running Seamdesk", "Show a code here and type it there, or the other way round.",
                    Ui.Horizontal(Ui.Button("Show a code", ShowCode).Margin(0, 0, 8, 0), Ui.Button("Enter a code", () => EnterCode()))),
                adding)),
            Ui.Card(Ui.Row(Ui.Laptop, "A machine running Mouse Without Borders", "For a machine that can't run Seamdesk yet. " + LegacyExplained,
                Ui.Button("Add", () => AddMwb()))),
            mwbForm));
        Loaded += (_, _) => seamdesk.Changed += Refresh;
        Unloaded += (_, _) =>
        {
            seamdesk.Changed -= Refresh;
            tick.Stop();
        };
        Refresh();
    }

    private readonly Border mwbForm = new();

    /// <summary>"2 displays · 1440 × 2560, 1920 × 1080".</summary>
    public static string Describe(IReadOnlyList<Rect> monitors) =>
        (monitors.Count == 1 ? "1 display · " : $"{monitors.Count} displays · ") + string.Join(", ", monitors.Select(m => $"{m.Width} × {m.Height}"));

    public static Border Badge(string text, bool accent = false) =>
        new Border { Child = Ui.Text(text, "CaptionTextBlockStyle").Brush(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush") }
            .Styled(accent ? "AccentBadge" : "CautionBadge");

    /// <summary>Statuses change often: only they update in place. Cards are rebuilt only when the peers themselves change.</summary>
    private void Refresh()
    {
        Settings s = seamdesk.Settings;
        string now = string.Join("|", s.Peers.Select(p => p.Name)) + "#" + string.Join("|", s.MwbPeers.Select(p => $"{p.Name}:{p.LegacySecurity}:{seamdesk.HasMwbKey(p.Name)}"));
        if (now != shape)
        {
            shape = now;
            Build(s);
        }

        foreach ((string name, (ContentControl slot, TextBlock detail, string usual)) in statuses)
        {
            PeerStatus status = StatusOf(name);
            slot.Content = Ui.Status(status);
            detail.Text = status.Detail ?? usual;
        }
    }

    private PeerStatus StatusOf(string name)
    {
        if (seamdesk.Settings.MwbPeers.FirstOrDefault(p => p.Name == name) is { } mwb)
        {
            if (!mwb.LegacySecurity)
            {
                return new PeerStatus("Off", Health.Off);
            }

            if (!seamdesk.HasMwbKey(name))
            {
                return new PeerStatus("Needs its security key", Health.Problem);
            }
        }

        return seamdesk.Status(name);
    }

    private void Build(Settings s)
    {
        peers.Children.Clear();
        statuses.Clear();
        if (s.Peers.Count > 0)
        {
            peers.Children.Add(Ui.Text("Paired machines", "Section"));
        }

        foreach (Pin pin in s.Peers)
        {
            peers.Children.Add(SeamdeskPeer(pin.Name));
        }

        if (s.MwbPeers.Count > 0)
        {
            peers.Children.Add(Ui.Text("Mouse Without Borders machines", "Section"));
        }

        foreach (MwbPeerSettings p in s.MwbPeers)
        {
            peers.Children.Add(MwbPeer(p));
        }
    }

    /// <summary>A peer's title row: its status on the right, and under its name the reason for a problem, if any.</summary>
    private Grid Header(string glyph, UIElement title, string name, string usual)
    {
        ContentControl slot = new() { Focusable = false };
        TextBlock detail = Ui.Text(usual, "Caption");
        statuses[name] = (slot, detail, usual);
        return Ui.Row(glyph, title, detail, slot);
    }

    private Border SeamdeskPeer(string name)
    {
        ComboBox clipboard = new() { Width = 200 };
        clipboard.Items.Add("Off");
        clipboard.Items.Add($"This PC to {name}");
        clipboard.Items.Add("Both ways");
        clipboard.SelectedIndex = (int)seamdesk.Settings.Clipboard.GetValueOrDefault(name);
        clipboard.SelectionChanged += (_, _) => seamdesk.Change(s => s.Clipboard[name] = (ClipboardMode)clipboard.SelectedIndex);
        System.Windows.Automation.AutomationProperties.SetName(clipboard, "Copy and paste");

        return Ui.Card(Ui.Vertical(
            Header(Ui.Pc, Ui.Text(name), name, "Runs Seamdesk"),
            Ui.SubRow(Ui.Row(Ui.Paste, "Copy and paste", "Text you copy goes along when the cursor crosses. Both machines must allow a direction.", clipboard)),
            Ui.SubRow(Confirmable("Unpair", $"{name} will need a new code to connect again.", $"Unpair {name}", () => seamdesk.Remove(name)))));
    }

    private Border MwbPeer(MwbPeerSettings p)
    {
        string name = p.Name;
        TextBox host = new() { Text = p.Host, Width = 200 };
        void SaveHost()
        {
            if (host.Text.Trim() is { Length: > 0 } h && h != seamdesk.Settings.MwbPeers.First(x => x.Name == name).Host)
            {
                seamdesk.Change(s => s.MwbPeers.First(x => x.Name == name).Host = h);
            }
        }

        host.LostFocus += (_, _) => SaveHost();
        host.OnEnter(SaveHost);
        System.Windows.Automation.AutomationProperties.SetName(host, "Computer name or IP address");

        ComboBox clipboard = new() { Width = 200 };
        clipboard.Items.Add("Off");
        clipboard.Items.Add($"This PC to {name}");
        clipboard.Items.Add("Both ways");
        clipboard.SelectedIndex = (int)p.Clipboard;
        clipboard.SelectionChanged += (_, _) => seamdesk.Change(s => s.MwbPeers.First(x => x.Name == name).Clipboard = (ClipboardMode)clipboard.SelectedIndex);
        System.Windows.Automation.AutomationProperties.SetName(clipboard, "Copy and paste");

        ComboBox version = new() { Width = 200 };
        version.Items.Add("Automatic");
        version.Items.Add("Before 0.101");
        version.Items.Add("0.101 or later");
        version.SelectedIndex = (int)p.Version;
        version.SelectionChanged += (_, _) => seamdesk.Change(s => s.MwbPeers.First(x => x.Name == name).Version = (MwbVersion)version.SelectedIndex);
        System.Windows.Automation.AutomationProperties.SetName(version, "Mouse Without Borders version");

        StackPanel title = Ui.Horizontal(Ui.Text(name), Badge("Legacy security").Margin(8, 0, 0, 0));
        Grid top = Header(Ui.Laptop, title, name, "Runs Mouse Without Borders");

        return Ui.Card(Ui.Vertical(
            top,
            Ui.SubRow(Ui.Row(Ui.Shield, "Legacy security", LegacyExplained + $" Seamdesk connects to {name} only at its address.",
                Ui.Toggle(p.LegacySecurity, on => seamdesk.Change(s => s.MwbPeers.First(x => x.Name == name).LegacySecurity = on), "Legacy security"))),
            Ui.SubRow(KeyRow(name)),
            Ui.SubRow(Ui.Row(Ui.Chain, "Computer name or IP address", "Seamdesk dials only this address, and answers no other.", host)),
            Ui.SubRow(Ui.Row(Ui.Info, "Mouse Without Borders version", VersionExplained(p), version)),
            Ui.SubRow(Ui.Row(Ui.Keyboard, $"Let {name} control this PC", $"{name}'s mouse and keyboard can cross into this PC. Off: this PC only controls {name}.",
                Ui.Toggle(p.AllowControl, on => seamdesk.Change(s => s.MwbPeers.First(x => x.Name == name).AllowControl = on), $"Let {name} control this PC"))),
            Ui.SubRow(Ui.Row(Ui.Paste, "Copy and paste", $"Text you copy goes along when the cursor crosses. Both ways also takes text copied on {name}.", clipboard)),
            Ui.SubRow(Confirmable("Remove", $"Forget {name} and its security key.", $"Remove {name}", () => seamdesk.Remove(name)))));
    }

    /// <summary>Which version Seamdesk speaks, and for Automatic, the one it found and that it won't drop back on its own.</summary>
    private static string VersionExplained(MwbPeerSettings p) => (p.Version, p.Detected) switch
    {
        (MwbVersion.Auto, MwbVersion.Current) => "In use: 0.101 or later, found automatically. Seamdesk won't drop back on its own.",
        (MwbVersion.Auto, MwbVersion.Old) => "In use: before 0.101, found automatically. 0.101 and later are safer.",
        (MwbVersion.Auto, _) => $"Seamdesk uses whichever version {p.Name} runs, and shows it here.",
        _ => $"Seamdesk speaks only this version. If {p.Name} runs another, connecting fails and says why.",
    };

    /// <summary>The key is never shown again: "Saved" and "Change key", or a password box until one is saved.</summary>
    private FrameworkElement KeyRow(string name, bool changing = false)
    {
        ContentControl slot = new() { Focusable = false };
        void Show(bool edit, string? error = null)
        {
            if (!edit && seamdesk.HasMwbKey(name))
            {
                slot.Content = Ui.Row(Ui.Key, "Security key", "Saved, encrypted for your Windows account. It is never shown again.",
                    Ui.Button("Change key", () => Show(true)));
                return;
            }

            PasswordBox box = new() { Width = 200 };
            System.Windows.Automation.AutomationProperties.SetName(box, "Security key");
            void Save()
            {
                if (seamdesk.SetMwbKey(name, box.Password) is { } problem)
                {
                    Show(true, problem);
                }
                else
                {
                    Show(false);
                }
            }

            box.OnEnter(Save);
            StackPanel buttons = Ui.Horizontal(box, Ui.Button("Save", Save, accent: true).Margin(8, 0, 0, 0));
            if (seamdesk.HasMwbKey(name))
            {
                buttons.Children.Add(Ui.Button("Cancel", () => Show(false)).Margin(8, 0, 0, 0));
            }

            StackPanel rows = Ui.Vertical(Ui.Row(Ui.Key, "Security key", $"Type the key shown in {name}'s Mouse Without Borders settings.", buttons));
            if (error is not null)
            {
                rows.Children.Add(Ui.Message(error));
            }

            slot.Content = rows;
            if (edit)
            {
                box.Loaded += (_, _) => box.Focus();
            }
        }

        Show(changing);
        return slot;
    }

    /// <summary>A destructive action that asks once, inline: "Unpair" becomes "Unpair laptop?" with Cancel.</summary>
    private static FrameworkElement Confirmable(string action, string description, string confirm, Action act)
    {
        ContentControl slot = new() { Focusable = false };
        void Ask(bool asking) => slot.Content = Ui.Row(Ui.Delete, asking ? confirm + "?" : action, description, asking
            ? Ui.Horizontal(Ui.Button(action, act, accent: true).Margin(0, 0, 8, 0), Ui.Button("Cancel", () => Ask(false)))
            : Ui.Button(action, () => Ask(true)));
        Ask(false);
        return slot;
    }

    // --- Pairing

    /// <summary>Shows a fresh code until it is used or expires (2 minutes).</summary>
    public void ShowCode()
    {
        PairingCode code = seamdesk.ShowPairingCode();
        HashSet<string> before = [.. seamdesk.Settings.Peers.Select(p => p.Name)];
        DateTime until = DateTime.Now + PairingCode.Lifetime;
        TextBlock left = Ui.Text("", "Caption");
        TextBlock digits = new TextBlock { Text = $"{code.Code[..4]} {code.Code[4..]}", FontSize = 40, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) }
            .Brush(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        Typography.SetNumeralAlignment(digits, FontNumeralAlignment.Tabular);
        System.Windows.Automation.AutomationProperties.SetName(digits, "Pairing code " + code.Code);
        Reveal(adding);
        adding.Child = Ui.SubRow(Ui.Vertical(
            Ui.Text("On the other machine, open Seamdesk's settings, choose Enter a code and type:", "Secondary"),
            digits,
            left,
            Ui.Button("Cancel", Close).Margin(0, 12, 0, 4)));

        tick.Tick -= OnTick;
        tick.Tick += OnTick;
        OnTick(null, EventArgs.Empty);
        tick.Start();

        void OnTick(object? sender, EventArgs e)
        {
            if (seamdesk.Settings.Peers.Select(p => p.Name).FirstOrDefault(n => !before.Contains(n)) is { } paired)
            {
                tick.Stop();
                adding.Child = Ui.SubRow(Ui.Vertical(
                    Ui.Message($"Paired with {paired}. Place it on the Layout page so the cursor knows where it is.", "success"),
                    Ui.Button("Done", Close).Margin(0, 12, 0, 4)));
                return;
            }

            TimeSpan remaining = until - DateTime.Now;
            if (remaining <= TimeSpan.Zero)
            {
                tick.Stop();
                adding.Child = Ui.SubRow(Ui.Vertical(
                    Ui.Message("That code has expired.", "caution"),
                    Ui.Horizontal(Ui.Button("Show a new code", ShowCode, accent: true).Margin(0, 0, 8, 0), Ui.Button("Cancel", Close)).Margin(0, 12, 0, 4)));
                return;
            }

            left.Text = $"It works once, for {remaining:m\\:ss} more.";
        }
    }

    /// <summary>Type the code another machine shows.</summary>
    public void EnterCode(string machine = "", string typed = "", string? error = null, bool busy = false)
    {
        tick.Stop();
        TextBox host = new() { Text = machine };
        TextBox code = new() { Text = typed, MaxLength = 9 };
        Typography.SetNumeralAlignment(code, FontNumeralAlignment.Tabular);
        Button pair = null!;
        async void Pair()
        {
            string digits = new([.. code.Text.Where(char.IsAsciiDigit)]);
            if (host.Text.Trim().Length == 0 || digits.Length != 8)
            {
                EnterCode(host.Text, code.Text, "Type the other machine's name and the 8-digit code it shows.");
                return;
            }

            EnterCode(host.Text, code.Text, busy: true);
            try
            {
                string name = await seamdesk.PairAsync(host.Text.Trim(), digits);
                adding.Child = Ui.SubRow(Ui.Vertical(
                    Ui.Message($"Paired with {name}. Place it on the Layout page so the cursor knows where it is.", "success"),
                    Ui.Button("Done", Close).Margin(0, 12, 0, 4)));
            }
            catch (Exception e)
            {
                EnterCode(host.Text, code.Text, PairingProblem(e));
            }
        }

        pair = Ui.Button(busy ? "Pairing…" : "Pair", Pair, accent: true);
        pair.IsEnabled = !busy;
        code.OnEnter(Pair);
        StackPanel form = Ui.Vertical(
            Ui.Text("On the other machine, choose Show a code. Then type here:", "Secondary").Margin(0, 0, 0, 12),
            Ui.Horizontal(Ui.Field("Other machine's name or IP address", host, 260), Ui.Field("Code", code, 140)),
            Ui.Horizontal(pair.Margin(0, 0, 8, 0), Ui.Button("Cancel", Close)));
        if (error is not null)
        {
            form.Children.Add(Ui.Message(error));
        }

        adding.Child = Ui.SubRow(form);
        Reveal(adding);
    }

    private static string PairingProblem(Exception e) => e switch
    {
        RefusedException r when r.Message.StartsWith("wrong pairing code", StringComparison.Ordinal) => "That code isn't right. Check it and try again; a code allows 3 tries.",
        RefusedException r when r.Message.StartsWith("no pairing code", StringComparison.Ordinal) => "That code has expired or been used. Show a new code on the other machine.",
        RefusedException r => char.ToUpperInvariant(r.Message[0]) + r.Message[1..] + ".",
        System.Net.Sockets.SocketException or OperationCanceledException => "Couldn't reach that machine. Check that Seamdesk is running there and that both are on the same network.",
        _ => $"Pairing didn't work: {e.Message}",
    };

    private void Close()
    {
        tick.Stop();
        adding.Child = null;
    }

    // --- Mouse Without Borders peers

    public void AddMwb(string name = "", string host = "", string? error = null)
    {
        TextBox nameBox = new() { Text = name };
        TextBox hostBox = new() { Text = host };
        PasswordBox key = new();
        void Add()
        {
            string n = nameBox.Text.Trim(), h = hostBox.Text.Trim();
            if (n.Length == 0 || h.Length == 0)
            {
                AddMwb(n, h, "Give the machine a name and its computer name or IP address.");
                return;
            }

            if (n.Equals(seamdesk.Me, StringComparison.OrdinalIgnoreCase) || seamdesk.Settings.PeerNames.Contains(n, StringComparer.OrdinalIgnoreCase))
            {
                AddMwb(n, h, $"There's already a machine called {n}.");
                return;
            }

            if (seamdesk.SetMwbKey(n, key.Password) is { } problem)
            {
                AddMwb(n, h, problem);
                return;
            }

            mwbForm.Child = null;
            seamdesk.Change(s => s.MwbPeers.Add(new MwbPeerSettings { Name = n, Host = h, LegacySecurity = true, Layouts = [[new Rect(0, 0, 1920, 1080)]] }));
        }

        key.OnEnter(Add);
        StackPanel form = Ui.Vertical(
            Ui.Text("Its screens start as one 1920 × 1080 display; set them on the Layout page.", "Secondary").Margin(0, 0, 0, 12),
            Ui.Horizontal(Ui.Field("Name", nameBox, 180), Ui.Field("Computer name or IP address", hostBox, 220), Ui.Field("Security key", key, 220)),
            Ui.Horizontal(Ui.Button("Add", Add, accent: true).Margin(0, 0, 8, 0), Ui.Button("Cancel", () => mwbForm.Child = null)));
        if (error is not null)
        {
            form.Children.Add(Ui.Message(error));
        }

        mwbForm.Child = Ui.Card(form);
        Reveal(mwbForm);
    }

    /// <summary>A form that just opened may be below the fold: scroll it into view once it is laid out.</summary>
    private void Reveal(FrameworkElement e) =>
        Dispatcher.BeginInvoke(() => e.BringIntoView(new System.Windows.Rect(0, 0, e.ActualWidth, e.ActualHeight + 24)), DispatcherPriority.Loaded);
}
