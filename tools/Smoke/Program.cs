using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Seamdesk.App;
using Seamdesk.Core;
using Seamdesk.Host;
using Seamdesk.Transport;
using static Seamdesk.App.Native;

// Interactive smoke test of the Windows host: real hooks, the hidden parked cursor, SendInput, on this PC's real
// monitors, against a fake peer placed below the monitor whose bottom edge is highest. Expected values come from the
// monitor geometry and Seamdesk.Core. It never runs longer than 30 s and puts the cursor back where it was.
// Run through tools/smoke.ps1. "--mwb-session N" only reports Mouse Without Borders detection for session N.

if (args is ["--mwb-session", string s])
{
    Console.WriteLine($"Mouse Without Borders in session {s}: {Mwb.Running(int.Parse(s)) ?? "not running"}");
    return 0;
}

if (Mwb.Running(Process.GetCurrentProcess().SessionId) is { } mwb)
{
    Console.WriteLine($"Not run: {mwb} is running. Quit Mouse Without Borders first; it would act on these moves too.");
    return 2;
}

if (!InputThread.OnDefaultDesktop() || !GetCursorPos(out POINT start))
{
    Console.WriteLine("Not run: no interactive desktop. Run it on the PC itself, not over ssh.");
    return 2;
}

Stopwatch clock = Stopwatch.StartNew();
int failures = 0;
void Check(string what, bool ok, string detail = "")
{
    failures += ok ? 0 : 1;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? $": {detail}" : "")}");
}

Screens screens = Displays.Read();
Console.WriteLine($"Monitors: {string.Join(", ", screens.Monitors.Select(m => $"{m.Rect}{(m.Primary ? " primary" : "")}"))}");
Console.WriteLine($"Virtual desktop {screens.Virtual}, park point {screens.Park}");

Display top = screens.Monitors.MinBy(m => m.Bottom);
Rect peerOnDesk = new(top.Left, top.Bottom, top.Right - top.Left, 1080);
if (screens.Rects.Any(r => r.Left < peerOnDesk.Right && peerOnDesk.Left < r.Right && r.Top < peerOnDesk.Bottom && peerOnDesk.Top < r.Bottom))
{
    Console.WriteLine($"Not run: no free space below {top.Rect} for the fake peer.");
    return 2;
}

Machine me = screens.ToMachine("this-pc", new Point(0, 0));
Desk desk = new([me, new Machine("smoke-peer", new Point(peerOnDesk.Left, peerOnDesk.Top), [new Rect(0, 0, peerOnDesk.Width, 1080)])]);
Console.WriteLine($"Fake peer below {top.Rect}, on the desk at {peerOnDesk}");

Cursor cursor = new("this-pc", desk, screens.Park);
cursor.LinkUp("smoke-peer");
Injector injector = new(() => screens);
InputThread input = new(cursor, new HotkeyMatcher(), _ => { });
List<Frame> sent = [];
ConcurrentQueue<(Point Pt, nint Extra)> seen = new();

using Timer watchdog = new(_ =>
{
    SetCursorPos(start.X, start.Y);
    Console.WriteLine("FAIL  watchdog: 30 s passed; cursor restored");
    Environment.Exit(3);
}, null, 30_000, Timeout.Infinite);

try
{
    await input.StartAsync();
    Observer.Start(seen); // installed last, so it sees each event before Seamdesk's hook does

    // 1. Local -> Driving: push down off the bottom edge of `top`, with unmarked ("physical") relative moves.
    int x = top.Left + ((top.Right - top.Left) / 2);
    Point expectedArrival = new(x - peerOnDesk.Left, 0);
    SetCursorPos(x, top.Bottom - 6);
    cursor.Resync(new Point(x, top.Bottom - 6));
    for (int i = 0; i < 20 && cursor.Mode != Mode.Driving; i++)
    {
        Relative(0, 2, 0);
        Wait(() => cursor.Mode == Mode.Driving, 30);
    }

    Drain();
    Check("crossing down off the bottom edge hands the cursor to the peer below", sent.Contains(new Handoff("smoke-peer", expectedArrival)),
        $"expected Handoff at {expectedArrival}, sent [{string.Join(", ", sent)}]");
    int lowest = seen.Where(e => e.Extra == 0).Select(e => e.Pt.Y).DefaultIfEmpty(int.MinValue).Max();
    Console.WriteLine($"INFO  hook point at the edge: lowest y seen {lowest}, last row {top.Bottom - 1}: " +
        (lowest >= top.Bottom ? "NOT clipped (points past the desktop reach the hook)" : "clipped to the desktop"));
    Check("the cursor is parked at the park point", Wait(() => Here() == screens.Park), $"at {Here()}");
    Check("the parked cursor is hidden (blank window under it)", Wait(() => !Showing()));
    Thread.Sleep(50);

    // 2. Driving: what the hook reports after this PC's ballistics is forwarded 1:1, and the real cursor stays parked.
    seen.Clear();
    sent.Clear();
    for (int i = 0; i < 20; i++)
    {
        Relative(7, -3, 0);
        Thread.Sleep(5);
    }

    Thread.Sleep(100);
    Drain();
    (int Dx, int Dy) hooked = seen.Where(e => e.Extra == 0).Aggregate((0, 0), (a, e) => (a.Item1 + e.Pt.X - screens.Park.X, a.Item2 + e.Pt.Y - screens.Park.Y));
    (int Dx, int Dy) forwarded = sent.OfType<PointerMove>().Aggregate((0, 0), (a, m) => (a.Item1 + m.Dx, a.Item2 + m.Dy));
    Check("deltas forwarded = hook deltas from the park point (1:1, nothing rescaled)", hooked == forwarded && hooked != (0, 0), $"hook {hooked}, sent {forwarded} in {sent.Count} frames");
    Check("local input is swallowed: the real cursor stays parked", Here() == screens.Park, $"at {Here()}");

    // 3. The peer hands the cursor back.
    Point back = new(x, top.Bottom - 1);
    cursor.OnFrame("smoke-peer", new Handoff("this-pc", back));
    Drain();
    Relative(0, -1, 0); // the hand moves on: the hook shows the cursor at the hand-back point in that move's callback
    Check("hand-back releases the cursor at the hand-back point, visible", Wait(() => Here() == back && Showing()), $"at {Here()}, showing {Showing()}");

    // 4. Held: moves from the peer, applied 1:1 by Seamdesk.Core and injected as absolute moves, land on the exact pixel.
    cursor.OnFrame("smoke-peer", new Handoff("this-pc", screens.Park));
    Drain();
    Point tracked = screens.Park;
    int exact = 0, total = 0;
    List<Point> targets = [.. screens.Rects.SelectMany(r => new Point[]
    {
        new(r.Left, r.Top), new(r.Right - 1, r.Top), new(r.Right - 1, r.Bottom - 1), new(r.Left, r.Bottom - 1), new(r.Left + (r.Width / 2), r.Top + (r.Height / 2)),
    }), new(screens.Virtual.Left - 50, screens.Virtual.Top - 50)]; // past the top-left: clamped (the peer is below)
    seen.Clear();
    foreach (Point t in targets)
    {
        Location expected = desk.Move(new Location(me, tracked), t.X - tracked.X, t.Y - tracked.Y);
        cursor.OnFrame("smoke-peer", new PointerMove(t.X - tracked.X, t.Y - tracked.Y));
        Drain();
        total++;
        if (expected.Machine == me && Wait(() => Here() == expected.Point))
        {
            exact++;
        }
        else
        {
            Console.WriteLine($"      move to {t}: expected {expected.Machine.Name} {expected.Point}, cursor at {Here()}");
        }

        tracked = expected.Point;
    }

    Check("absolute moves land on exactly the pixel Seamdesk.Core computes (corners, centres, clamp)", exact == total, $"{exact}/{total}");
    Check("Seamdesk's own injections reach the hook carrying its mark", Wait(() => seen.Any(e => e.Extra == SeamdeskMark)));

    sent.Clear();
    Point from = new(x, top.Bottom - 10);
    cursor.OnFrame("smoke-peer", new PointerMove(from.X - tracked.X, from.Y - tracked.Y));
    cursor.OnFrame("smoke-peer", new PointerMove(0, 40));
    Drain();
    Check("held cursor pushed past the bottom edge goes to the peer", sent.Contains(new Handoff("smoke-peer", expectedArrival)) && cursor.Mode == Mode.Local,
        $"sent [{string.Join(", ", sent)}]");

    // 5. For the record: Windows' ballistics apply to relative injection, which is why Seamdesk injects absolute moves only.
    SetCursorPos(screens.Park.X, screens.Park.Y);
    Thread.Sleep(30);
    Relative(20, 0, SeamdeskMark);
    Thread.Sleep(50);
    Console.WriteLine($"INFO  a relative SendInput of +20 px moved the cursor {Here().X - screens.Park.X} px (pointer speed and acceleration apply)");
}
finally
{
    input.Dispose();
    SetCursorPos(start.X, start.Y);
}

Console.WriteLine($"{(failures == 0 ? "OK" : $"{failures} FAILED")} in {clock.Elapsed.TotalSeconds:0.0} s; cursor restored to ({start.X}, {start.Y})");
return failures == 0 ? 0 : 1;

void Drain()
{
    foreach (Effect effect in cursor.TakeEffects())
    {
        switch (effect)
        {
            case Send send: sent.Add(send.Frame); break;
            case MoveTo m: injector.MoveTo(m.Point); break;
            case Inject i: injector.Inject(i.Input); break;
        }
    }
}

static void Relative(int dx, int dy, nint mark) => SendInput(1,
    [new INPUT { Type = INPUT_MOUSE, U = new InputUnion { Mi = new MOUSEINPUT { Dx = dx, Dy = dy, Flags = MOUSEEVENTF_MOVE, ExtraInfo = mark } } }],
    Marshal.SizeOf<INPUT>());

static Point Here() => GetCursorPos(out POINT p) ? new Point(p.X, p.Y) : new Point(int.MinValue, int.MinValue);

static bool Showing() => Observer.CursorShowing();

static bool Wait(Func<bool> condition, int ms = 500)
{
    Stopwatch w = Stopwatch.StartNew();
    while (!condition())
    {
        if (w.ElapsedMilliseconds > ms)
        {
            return false;
        }

        Thread.Sleep(2);
    }

    return true;
}

/// <summary>An observe-only low-level mouse hook on its own thread: records every point and dwExtraInfo, swallows nothing.</summary>
internal static class Observer
{
    private static HookProc? proc;

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int Size, Flags;
        public nint Cursor;
        public POINT Pt;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO info);

    public static bool CursorShowing()
    {
        CURSORINFO info = new() { Size = Marshal.SizeOf<CURSORINFO>() };
        return GetCursorInfo(ref info) && (info.Flags & 1) != 0 && info.Cursor != 0;
    }

    public static void Start(ConcurrentQueue<(Point, nint)> seen)
    {
        using ManualResetEventSlim ready = new();
        Thread t = new(() =>
        {
            proc = (code, wParam, lParam) =>
            {
                if (code >= 0 && wParam == WM_MOUSEMOVE)
                {
                    MSLLHOOKSTRUCT h = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    seen.Enqueue((new Point(h.Pt.X, h.Pt.Y), h.ExtraInfo));
                }

                return CallNextHookEx(0, code, wParam, lParam);
            };
            SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
            ready.Set();
            while (GetMessage(out MSG msg, 0, 0, 0) > 0)
            {
                DispatchMessage(ref msg);
            }
        }) { IsBackground = true };
        t.Start();
        ready.Wait();
    }
}
