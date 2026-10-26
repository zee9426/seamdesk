using System.Runtime.InteropServices;
using Seamdesk.Core;
using Seamdesk.Host;
using Seamdesk.Transport;
using static Seamdesk.App.Native;

namespace Seamdesk.App;

/// <summary>Injects what a controller sends, with SendInput, marked with <see cref="SeamdeskMark"/>.</summary>
internal sealed class Injector(Func<Screens> screens)
{
    private static readonly int Size = Marshal.SizeOf<INPUT>();

    /// <summary>
    /// An absolute move over the virtual desktop. Absolute input gets no pointer ballistics (only relative motion "is
    /// subject to the effects of the mouse speed", MOUSEINPUT docs), so the controller's acceleration is the only one.
    /// </summary>
    public bool MoveTo(Point p)
    {
        (int x, int y) = screens().Normalize(p);
        return Mouse(x, y, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
    }

    public bool Inject(Frame input) => input switch
    {
        PointerButton b => Mouse(0, 0, b.Button switch { MouseButton.X1 => 1u, MouseButton.X2 => 2u, _ => 0u }, ButtonFlag(b)),
        Wheel w => Mouse(0, 0, (uint)w.Delta, w.Horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL),
        Key k => Send(new INPUT
        {
            Type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                Ki = new KEYBDINPUT
                {
                    // An MWB controller sends no scan code; MWB maps it from the virtual key (InputSimulation.cs SendKey).
                    Vk = k.VirtualKey, Scan = k.ScanCode != 0 ? k.ScanCode : (ushort)MapVirtualKey(k.VirtualKey, 0), ExtraInfo = SeamdeskMark,
                    Flags = (k.Extended ? KEYEVENTF_EXTENDEDKEY : 0) | (k.Down ? 0 : KEYEVENTF_KEYUP),
                },
            },
        }),
        _ => false,
    };

    private static uint ButtonFlag(PointerButton b) => (b.Button, b.Down) switch
    {
        (MouseButton.Left, true) => MOUSEEVENTF_LEFTDOWN,
        (MouseButton.Left, false) => MOUSEEVENTF_LEFTUP,
        (MouseButton.Right, true) => MOUSEEVENTF_RIGHTDOWN,
        (MouseButton.Right, false) => MOUSEEVENTF_RIGHTUP,
        (MouseButton.Middle, true) => MOUSEEVENTF_MIDDLEDOWN,
        (MouseButton.Middle, false) => MOUSEEVENTF_MIDDLEUP,
        (_, true) => MOUSEEVENTF_XDOWN,
        _ => MOUSEEVENTF_XUP,
    };

    private static bool Mouse(int dx, int dy, uint data, uint flags) => Send(new INPUT
    {
        Type = INPUT_MOUSE,
        U = new InputUnion { Mi = new MOUSEINPUT { Dx = dx, Dy = dy, MouseData = data, Flags = flags, ExtraInfo = SeamdeskMark } },
    });

    /// <summary>False when Windows blocked it (UIPI, or the secure desktop has the input).</summary>
    private static bool Send(INPUT input) => SendInput(1, [input], Size) == 1;
}
