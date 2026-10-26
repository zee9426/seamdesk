using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;
using Seamdesk.Core;
using Seamdesk.Host;
using Seamdesk.Mwb;
using Seamdesk.Transport;
using Microsoft.Win32;

namespace Seamdesk.App;

/// <summary>Wires the Windows host together: transport, cursor, input thread, injector, system events and the tray.</summary>
internal sealed class App : ISeamdesk
{
    // 2: the pairing labels carry the new name (Glide spoke 1), so a Glide peer is told to update instead of "wrong code".
    public const int Protocol = 2;

    private static readonly string SettingsPath = Path.Combine(Log.Dir, "settings.json");
    private static readonly string IdentityPath = Path.Combine(Log.Dir, "identity.dpapi");
    private static readonly Startup Startup = new(TimeProvider.System, TimeSpan.FromSeconds(20));

    private readonly SemaphoreSlim effectsReady = new(0);
    private readonly System.Windows.Threading.Dispatcher ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    private readonly string me = Environment.MachineName;
    private Screens screens = null!;
    private Roster roster = null!;
    private Cursor cursor = null!;
    private Injector injector = null!;
    private InputThread input = null!;
    private Tray tray = null!;
    private Hotkey snapBack;
    private SettingsWindow? settingsWindow;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--dial", var host, var port])
        {
            return Dial(host, int.Parse(port));
        }

        using Mutex single = new(true, @"Local\Seamdesk", out bool first);
        if (!first)
        {
            Log.Write("start: Seamdesk is already running in this session");
            return 0;
        }

        int session = Process.GetCurrentProcess().SessionId;
        Step($"Seamdesk {typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}, session {session}");
        new Thread(Watchdog) { IsBackground = true, Name = "Seamdesk startup watchdog" }.Start();
        Step("theme");
        System.Windows.Application wpf = new() { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        wpf.ThemeMode = System.Windows.ThemeMode.System;
        Ui.UseTheme(wpf);
        Step("looking for Mouse Without Borders");
        if (Mwb.Running(session) is { } name)
        {
            Step($"{name} is running: asking the user", waitsForUser: true);
            if (!new MwbRunningWindow(name, () => Mwb.Quit(session)).ShowDialog().GetValueOrDefault())
            {
                Log.Write("start: the user kept Mouse Without Borders");
                return 1;
            }
        }

        App app = new();
        wpf.Startup += async (_, _) =>
        {
            try
            {
                await app.StartAsync(wpf);
            }
            catch (Exception e)
            {
                Log.Write($"start: {e}");
                Startup.Begin("could not start: telling the user", waitsForUser: true);
                System.Windows.MessageBox.Show($"Seamdesk could not start: {e.Message}", "Seamdesk", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                wpf.Shutdown(1);
            }
        };
        Step("WPF");
        return wpf.Run();
    }

    /// <summary>One line in seamdesk.log per startup step, so a start that hangs shows where it stopped.</summary>
    private static void Step(string step, bool waitsForUser = false)
    {
        Startup.Begin(step, waitsForUser);
        Log.Write($"start: {step}");
    }

    /// <summary>
    /// If the tray isn't up within 20 s (the dialogs that wait for the user don't count), logs the step and says so,
    /// then exits: a stuck Seamdesk would otherwise hold the single-instance mutex, and the next start would end silently.
    /// </summary>
    private static void Watchdog()
    {
        while (!Startup.IsUp)
        {
            if (Startup.Stuck() is { } step)
            {
                Log.Write($"start: stuck for 20 s at: {step}");
                System.Windows.MessageBox.Show(
                    $"Seamdesk didn't finish starting. It has been stuck for 20 seconds at: {step}.\n\nThe details are in {Log.FilePath}.\n\nSeamdesk will close now. Start it again; if this happens again, send that file.",
                    "Seamdesk", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                Environment.Exit(2);
            }

            Thread.Sleep(1000);
        }
    }

    private async Task StartAsync(System.Windows.Application wpf)
    {
        Step("settings");
        Settings settings = Settings.Load(SettingsPath);
        Step("displays");
        screens = Displays.Read();
        injector = new Injector(() => screens);

        Step("identity");
        if (await LoadIdentityAsync() is not { } identity)
        {
            wpf.Shutdown(1);
            return;
        }

        Step("network node");
        roster = new Roster(SettingsPath, settings, pins => new Node(new LocalNode(identity, new Hello(Protocol, Version, me), pins, TimeProvider.System)) { Log = Log.Write },
            screens, FullscreenFocused, MwbKeys.Key, MwbKeys.Forget) { Log = Log.Write, MwbDelay = Delay };
        roster.Changed += () =>
        {
            if (ui.CheckAccess())
            {
                Changed?.Invoke();
            }
            else
            {
                ui.BeginInvoke(() => Changed?.Invoke());
            }
        };
        roster.ClipboardReceived += text => ui.BeginInvoke(() => SetClipboard(text));
        roster.Notice += text => ui.BeginInvoke(() => tray.Notice(text));

        Step("cursor");
        cursor = roster.Cursor;
        if (Native.GetCursorPos(out Native.POINT p))
        {
            cursor.Resync(new Point(p.X, p.Y));
        }

        cursor.EffectsReady += () =>
        {
            if (effectsReady.CurrentCount == 0)
            {
                effectsReady.Release();
            }
        };
        Step("input hooks");
        input = new InputThread(cursor, Hotkeys(), OnHotkey);
        await input.StartAsync();
        _ = Task.Run(RunEffectsAsync);

        Step("tray");
        string? mwbApp = Mwb.Installed();
        tray = new Tray(this, roster.KickAll, OpenSettings, () => Quit(wpf), mwbApp is null ? null : () =>
        {
            Mwb.RestoreAutostart();
            Quit(wpf);
            Process.Start(new ProcessStartInfo(mwbApp) { UseShellExecute = true });
        });
        Startup.Up();
        Step("listening and dialling peers, Mouse Without Borders peers");
        roster.Start();

        // Retry every peer at once on resume, unlock and any address change (design.md, Reconnect model).
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Resume)
            {
                roster.KickAll();
            }
        };
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            {
                roster.KickAll();
            }
        };
        NetworkChange.NetworkAddressChanged += (_, _) => roster.KickAll();
        SystemEvents.DisplaySettingsChanged += (_, _) => OnDisplayChange(); // raised on WM_DISPLAYCHANGE
        Step("up");
    }

    /// <summary>
    /// This PC's identity. One this logon can't read (or that DPAPI doesn't hand back in 10 s) is never replaced without
    /// asking: on yes it is renamed aside, kept, and a new identity made. Null if the user says no.
    /// </summary>
    private async Task<Identity?> LoadIdentityAsync()
    {
        DpapiIdentityStore store = new(IdentityPath);
        try
        {
            return await Identity.LoadOrCreateAsync(store, me, TimeSpan.FromSeconds(10), TimeProvider.System);
        }
        catch (UnreadableIdentityException e)
        {
            Log.Write($"start: can't read {IdentityPath}: {e}");
            Step("identity unreadable: asking the user", waitsForUser: true);
            string aside = $"{IdentityPath}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            if (System.Windows.MessageBox.Show(
                    $"Seamdesk can't read this PC's identity, the key your other Seamdesk PCs know it by ({e.Message}).\n\n" +
                    "This can happen if Seamdesk was first started from a different kind of Windows sign-in, such as over SSH.\n\n" +
                    $"Start fresh? Seamdesk keeps the old file, renamed to {Path.GetFileName(aside)}, and makes a new identity. " +
                    "Your other Seamdesk PCs will then need to pair with this one again.\n\nNo closes Seamdesk and changes nothing.",
                    "Seamdesk", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            {
                Log.Write("start: the user kept the unreadable identity; closing");
                return null;
            }

            File.Move(IdentityPath, aside);
            Step($"identity: moved the old one to {aside}; making a new one");
            return await Identity.LoadOrCreateAsync(store, me, TimeSpan.FromSeconds(10), TimeProvider.System);
        }
    }

    /// <summary>
    /// <c>Seamdesk.exe --dial host port</c>: one TCP connect from this very exe, for finding out whether Windows Firewall lets
    /// it out (docs/release.md). The exit code and a line in seamdesk.log say which: 0 connected, 1 refused (the network
    /// answered, so the firewall let the packet through), 2 access denied, 3 anything else.
    /// </summary>
    private static int Dial(string host, int port)
    {
        try
        {
            using System.Net.Sockets.TcpClient c = new();
            if (!c.ConnectAsync(host, port).Wait(5000))
            {
                Log.Write($"dial {host}:{port}: no answer in 5 s");
                return 3;
            }

            Log.Write($"dial {host}:{port}: connected");
            return 0;
        }
        catch (AggregateException e) when (e.GetBaseException() is System.Net.Sockets.SocketException se)
        {
            Log.Write($"dial {host}:{port}: {se.SocketErrorCode} ({se.NativeErrorCode})");
            return se.SocketErrorCode switch { System.Net.Sockets.SocketError.ConnectionRefused => 1, System.Net.Sockets.SocketError.AccessDenied => 2, _ => 3 };
        }
    }

    private void OnHotkey(Hotkey hotkey)
    {
        if (hotkey == snapBack || hotkey == Hotkey.AlsoSnapBack)
        {
            cursor.SnapBack();
        }
        else
        {
            ui.BeginInvoke(ToggleStay);
        }
    }

    // --- ISeamdesk: the settings window and the tray

    public string Me => me;

    public string Version { get; } =
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public IReadOnlyList<Rect> MyMonitors => screens.Rects;

    public Settings Settings => roster.Settings;

    public event Action? Changed;

    public bool Staying { get; private set; }

    public PeerStatus Status(string peer) => roster.State(peer) is { } state ? PeerStatus.Of(state) : PeerStatus.Unknown;

    public int ActiveLayout(string mwbPeer) => roster.ActiveLayout(mwbPeer);

    public void ToggleStay()
    {
        Staying = cursor.ToggleStay();
        Changed?.Invoke();
    }

    public PairingCode ShowPairingCode() => roster.ShowPairingCode();

    public Task<string> PairAsync(string host, string code) => roster.PairAsync(host, code);

    public void Remove(string peer) => roster.Remove(peer);

    public bool HasMwbKey(string peer) => MwbKeys.Has(peer);

    public string? SetMwbKey(string peer, string key)
    {
        string? error = MwbKeys.Set(peer, key);
        if (error is null)
        {
            _ = roster.RestartMwbAsync();
        }

        return error;
    }

    public void PauseHotkeys(bool paused) => input.Hotkeys = paused ? new HotkeyMatcher() : Hotkeys();

    public void Apply(Settings changed)
    {
        roster.Apply(changed);
        input.Hotkeys = Hotkeys();
    }

    private HotkeyMatcher Hotkeys()
    {
        Settings settings = roster.Settings;
        snapBack = HotkeyOr(settings.SnapBackHotkey, Hotkey.DefaultSnapBack);
        return new HotkeyMatcher(snapBack, Hotkey.AlsoSnapBack, HotkeyOr(settings.StayHotkey, Hotkey.DefaultStay), Hotkey.AlsoStay);
    }

    /// <summary>A bad hotkey in the file falls back to the default, with a note in the log.</summary>
    private static Hotkey HotkeyOr(string? text, Hotkey fallback)
    {
        try
        {
            return text is null ? fallback : Hotkey.Parse(text);
        }
        catch (FormatException e)
        {
            Log.Write($"settings: {e.Message}; using the default");
            return fallback;
        }
    }

    private void OpenSettings()
    {
        if (settingsWindow is null)
        {
            settingsWindow = new SettingsWindow(this);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
        }

        settingsWindow.Activate();
    }

    /// <summary>Executes the cursor's effects in order. Sends are awaited one by one, so moves pile up and get summed meanwhile.</summary>
    private async Task RunEffectsAsync()
    {
        while (true)
        {
            await effectsReady.WaitAsync();
            foreach (string note in cursor.TakeNotes())
            {
                Log.Write($"cursor: {note}");
            }

            foreach (Effect effect in cursor.TakeEffects())
            {
                try
                {
                    switch (effect)
                    {
                        case Send s:
                            await roster.SendAsync(s, () => ui.InvokeAsync(ClipboardText).Task);
                            break;
                        case MoveTo m:
                            injector.MoveTo(m.Point);
                            break;
                        case Inject i:
                            injector.Inject(i.Input);
                            break;
                        case Show show:
                            input.Show(show.At);
                            break;
                    }
                }
                catch (Exception e)
                {
                    Log.Write($"effect {effect}: {e}");
                }
            }
        }
    }

    private static void SetClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Log.Write("clipboard: busy; the peer's text was dropped");
        }
    }

    private static string? ClipboardText()
    {
        try
        {
            return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null; // another app holds the clipboard open
        }
    }

    private void OnDisplayChange()
    {
        screens = Displays.Read();
        roster.DisplaysChanged(screens);
        if (Native.GetCursorPos(out Native.POINT p))
        {
            cursor.Resync(new Point(p.X, p.Y));
        }
    }

    /// <summary>
    /// A wait of under a tick, for pacing MWB moves: a high-resolution waitable timer (Windows 10 1803 and later) fires
    /// within about half a millisecond, where <c>Task.Delay</c> waits for the system timer's next 15.6 ms tick. Never
    /// longer than a millisecond, so not cancelled.
    /// </summary>
    private static Task Delay(TimeSpan wait, CancellationToken ct)
    {
        Microsoft.Win32.SafeHandles.SafeWaitHandle timer = Native.CreateWaitableTimerExW(0, null, Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
        long due = -wait.Ticks;
        if (timer.IsInvalid || !Native.SetWaitableTimer(timer, ref due, 0, 0, 0, false))
        {
            timer.Dispose();
            return Task.Delay(wait, ct);
        }

        Waitable fired = new(timer);
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle registered = ThreadPool.RegisterWaitForSingleObject(fired, (_, _) => done.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        return done.Task.ContinueWith(_ =>
        {
            registered.Unregister(null);
            fired.Dispose();
        }, TaskScheduler.Default);
    }

    private sealed class Waitable : WaitHandle
    {
        public Waitable(Microsoft.Win32.SafeHandles.SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    /// <summary>A fullscreen Direct3D app, a "busy" fullscreen app or presentation mode (SHQueryUserNotificationState).</summary>
    private static bool FullscreenFocused() => Native.SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4;

    private void Quit(System.Windows.Application wpf)
    {
        settingsWindow?.Close();
        cursor.SnapBack("Seamdesk quit");
        if (cursor.TakePlace() is { Hidden: false } back)
        {
            input.Show(back.At); // here, not as an effect: Seamdesk may be gone before the effects run
        }

        input.Dispose();
        tray.Dispose();
        _ = roster.DisposeAsync();
        wpf.Shutdown();
    }
}
