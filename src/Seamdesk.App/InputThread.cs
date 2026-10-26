using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Seamdesk.Core;
using Seamdesk.Host;
using Seamdesk.Transport;
using static Seamdesk.App.Native;

namespace Seamdesk.App;

/// <summary>
/// The dedicated input thread: low-level mouse and keyboard hooks, the window that hides the parked cursor, and the
/// desktop-switch watcher, all on one message loop. Hook callbacks only translate the event, ask <see cref="Cursor"/>
/// whether to swallow it and move the real cursor where it asks: Windows silently removes a low-level hook that is too
/// slow (LowLevelMouseProc docs).
/// </summary>
internal sealed class InputThread : IDisposable
{
    private readonly Cursor cursor;
    private volatile HotkeyMatcher hotkeys;
    private readonly Action<Hotkey> onHotkey;
    private readonly ConcurrentQueue<Action> posted = new();
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;

    // Kept in fields: the native side holds only function pointers, and a collected delegate crashes the hook.
    private readonly HookProc mouseProc;
    private readonly HookProc keyboardProc;
    private readonly WndProc blankProc;
    private readonly WinEventProc desktopProc;
    private uint threadId;
    private nint blank;
    private bool secure;

    /// <summary>Swaps the hotkeys live (the settings window, or none while it captures a new one).</summary>
    public HotkeyMatcher Hotkeys
    {
        set => hotkeys = value;
    }

    public InputThread(Cursor cursor, HotkeyMatcher hotkeys, Action<Hotkey> onHotkey)
    {
        (this.cursor, this.hotkeys, this.onHotkey) = (cursor, hotkeys, onHotkey);
        (mouseProc, keyboardProc, blankProc, desktopProc) = (OnMouse, OnKeyboard, OnBlank, OnDesktopSwitch);
        thread = new Thread(Run) { IsBackground = true, Name = "Seamdesk input", Priority = ThreadPriority.Highest };
    }

    public Task StartAsync()
    {
        thread.Start();
        return started.Task;
    }

    /// <summary>Runs <paramref name="action"/> on the input thread, between hook callbacks.</summary>
    public void Post(Action action)
    {
        posted.Enqueue(action);
        PostThreadMessage(threadId, WM_APP, 0, 0);
    }

    public void Dispose() => PostThreadMessage(threadId, WM_QUIT, 0, 0);

    /// <summary>True when the input desktop is the user's own ("Default"), not the secure desktop (lock, UAC, Ctrl+Alt+Del).</summary>
    public static bool OnDefaultDesktop()
    {
        nint desk = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desk == 0)
        {
            return false; // the secure desktop refuses a user process
        }

        try
        {
            StringBuilder name = new(64);
            return GetUserObjectInformation(desk, UOI_NAME, name, name.Capacity * 2, out _) && name.ToString() == "Default";
        }
        finally
        {
            CloseDesktop(desk);
        }
    }

    private void Run()
    {
        threadId = GetCurrentThreadId();
        try
        {
            blank = CreateBlankWindow();
            nint module = GetModuleHandle(null);
            nint mouse = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);
            nint keyboard = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, module, 0);
            if (mouse == 0 || keyboard == 0)
            {
                throw new InvalidOperationException($"SetWindowsHookEx failed (error {Marshal.GetLastWin32Error()})");
            }

            nint desktop = SetWinEventHook(EVENT_SYSTEM_DESKTOPSWITCH, EVENT_SYSTEM_DESKTOPSWITCH, 0, desktopProc, 0, 0, WINEVENT_OUTOFCONTEXT);
            started.SetResult();
            while (GetMessage(out MSG msg, 0, 0, 0) > 0)
            {
                if (msg.Message == WM_APP)
                {
                    while (posted.TryDequeue(out Action? action))
                    {
                        action();
                    }

                    continue;
                }

                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            UnhookWinEvent(desktop);
            UnhookWindowsHookEx(mouse);
            UnhookWindowsHookEx(keyboard);
        }
        catch (Exception e)
        {
            started.TrySetException(e);
        }
    }

    private nint OnMouse(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            MSLLHOOKSTRUCT h = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (h.ExtraInfo != SeamdeskMark && (Swallow((int)wParam, h) | Place((int)wParam)))
            {
                return 1;
            }
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    private bool Swallow(int message, MSLLHOOKSTRUCT h)
    {
        try
        {
            short high = (short)(h.MouseData >> 16);
            MouseButton x = high == 2 ? MouseButton.X2 : MouseButton.X1;
            return message switch
            {
                WM_MOUSEMOVE => cursor.OnMove(new Point(h.Pt.X, h.Pt.Y)),
                WM_LBUTTONDOWN or WM_LBUTTONUP => cursor.OnButton(MouseButton.Left, message == WM_LBUTTONDOWN),
                WM_RBUTTONDOWN or WM_RBUTTONUP => cursor.OnButton(MouseButton.Right, message == WM_RBUTTONDOWN),
                WM_MBUTTONDOWN or WM_MBUTTONUP => cursor.OnButton(MouseButton.Middle, message == WM_MBUTTONDOWN),
                WM_XBUTTONDOWN or WM_XBUTTONUP => cursor.OnButton(x, message == WM_XBUTTONDOWN),
                WM_MOUSEWHEEL or WM_MOUSEHWHEEL => cursor.OnWheel(high, message == WM_MOUSEHWHEEL),
                _ => false,
            };
        }
        catch (Exception e)
        {
            Log.Write($"mouse hook: {e}");
            return false; // never let an exception unwind into user32
        }
    }

    /// <summary>
    /// Puts the real cursor where the cursor asks, inside this callback (as MWB's own hook does: InputHook.cs:268-283):
    /// Windows computes the next event's point from here. True for a move, which is then swallowed. The blank window goes
    /// under the cursor before it gets there, and away after it has left.
    /// </summary>
    private bool Place(int message)
    {
        if (cursor.TakePlace() is not { } p)
        {
            return false;
        }

        if (p.Hidden)
        {
            SetWindowPos(blank, HWND_TOPMOST, p.At.X - 16, p.At.Y - 16, 32, 32, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        SetCursorPos(p.At.X, p.At.Y);
        if (!p.Hidden)
        {
            ShowWindow(blank, SW_HIDE);
        }

        return message == WM_MOUSEMOVE;
    }

    /// <summary>Shows the real cursor at <paramref name="at"/> now, from any thread (<see cref="Seamdesk.Host.Show"/>).</summary>
    public void Show(Point at)
    {
        SetCursorPos(at.X, at.Y);
        ShowWindow(blank, SW_HIDE);
    }

    private nint OnKeyboard(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            KBDLLHOOKSTRUCT k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (k.ExtraInfo != SeamdeskMark && SwallowKey(k))
            {
                return 1;
            }
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    private bool SwallowKey(KBDLLHOOKSTRUCT k)
    {
        try
        {
            bool down = (k.Flags & LLKHF_UP) == 0;
            if (hotkeys.Feed((ushort)k.VkCode, down, out bool swallow) is { } hotkey)
            {
                Post(() => onHotkey(hotkey)); // out of the hook: it may send frames
            }

            return swallow || cursor.OnKey(new Key((ushort)k.VkCode, (ushort)k.ScanCode, down, (k.Flags & LLKHF_EXTENDED) != 0));
        }
        catch (Exception e)
        {
            Log.Write($"keyboard hook: {e}");
            return false;
        }
    }

    private void OnDesktopSwitch(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        bool now = !OnDefaultDesktop();
        if (now != secure)
        {
            secure = now;
            cursor.SecureDesktop(now);
        }
    }

    /// <summary>
    /// A 32x32 topmost popup that never activates and is all but transparent (alpha 1, so it still hit-tests). The parked
    /// cursor sits over it, and its WM_SETCURSOR sets no cursor, so none is drawn. Nothing global changes: if Seamdesk dies,
    /// the window goes with it and the cursor is back.
    /// </summary>
    private nint CreateBlankWindow()
    {
        WNDCLASSEX cls = new() { Size = Marshal.SizeOf<WNDCLASSEX>(), WndProc = blankProc, Instance = GetModuleHandle(null), ClassName = "SeamdeskBlankCursor" };
        RegisterClassEx(ref cls);
        nint hwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED, cls.ClassName, "", WS_POPUP, 0, 0, 32, 32, 0, 0, cls.Instance, 0);
        SetLayeredWindowAttributes(hwnd, 0, 1, LWA_ALPHA);
        return hwnd;
    }

    private nint OnBlank(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_SETCURSOR:
                SetCursor(0);
                return 1;
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }
}
