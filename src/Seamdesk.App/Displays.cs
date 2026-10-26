using Seamdesk.Host;
using static Seamdesk.App.Native;

namespace Seamdesk.App;

internal static class Displays
{
    /// <summary>
    /// Every monitor, in physical pixels: the process is Per-Monitor-V2 aware (app.manifest), so GetMonitorInfo reports
    /// physical coordinates (High DPI docs, "Mixed-mode DPI scaling and DPI-aware APIs").
    /// </summary>
    public static Screens Read()
    {
        List<Display> monitors = [];
        EnumDisplayMonitors(0, 0, (nint h, nint dc, ref RECT r, nint data) =>
        {
            MONITORINFO info = new() { Size = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(h, ref info))
            {
                RECT m = info.Monitor;
                monitors.Add(new Display(m.Left, m.Top, m.Right, m.Bottom, (info.Flags & MONITORINFOF_PRIMARY) != 0));
            }

            return true;
        }, 0);
        return new Screens(monitors);
    }
}
