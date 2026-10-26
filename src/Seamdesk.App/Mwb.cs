using System.Diagnostics;

namespace Seamdesk.App;

/// <summary>Mouse Without Borders, standalone ("Garage") or in PowerToys. Both hook the same input, so Seamdesk won't run beside it.</summary>
internal static class Mwb
{
    // Garage: MouseWithoutBorders.exe and its helper. PowerToys: the AssemblyNames of its MWB projects.
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "MouseWithoutBorders", "MouseWithoutBordersHelper", "PowerToys.MouseWithoutBorders", "PowerToys.MouseWithoutBordersHelper",
    };

    /// <summary>The MWB process running in this session, if any (one in another user's session hooks a different desktop).</summary>
    public static string? Running(int session) =>
        Process.GetProcesses().Where(p => p.SessionId == session && Names.Contains(p.ProcessName)).Select(p => p.ProcessName).FirstOrDefault();

    /// <summary>
    /// Ends MWB's processes in this session. Only ever called after the user chose "Quit Mouse Without Borders and start
    /// Seamdesk". False if one would not end (say, it runs elevated).
    /// </summary>
    public static bool Quit(int session)
    {
        foreach (Process p in Process.GetProcesses().Where(p => p.SessionId == session && Names.Contains(p.ProcessName)))
        {
            try
            {
                p.Kill();
                p.WaitForExit(5000);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Write($"quit {p.ProcessName}: {e.Message}");
            }
        }

        return Running(session) is null;
    }

    /// <summary>
    /// The installed MWB to go back to: standalone Garage first, then PowerToys (per-user, then per-machine), or null.
    /// PowerToys starts its Mouse Without Borders module itself if the user has it turned on.
    /// </summary>
    public static string? Installed() =>
        new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Garage", "Mouse without Borders", "MouseWithoutBorders.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Garage", "Mouse without Borders", "MouseWithoutBorders.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerToys", "PowerToys.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerToys", "PowerToys.exe"),
        }.FirstOrDefault(File.Exists);

    /// <summary>
    /// "Go back to Mouse Without Borders": puts back the autostart that install.ps1 switched off (from its backup) and
    /// removes Seamdesk's own run-at-sign-in. Does nothing without the script, i.e. on a build that wasn't installed.
    /// </summary>
    public static void RestoreAutostart()
    {
        string script = Path.Combine(AppContext.BaseDirectory, "mwb-autostart.ps1");
        if (!File.Exists(script))
        {
            return;
        }

        try
        {
            using Process? p = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Action Restore -RemoveSeamdeskRun Seamdesk")
            { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(15000);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"restore MWB autostart: {e.Message}");
        }
    }
}
