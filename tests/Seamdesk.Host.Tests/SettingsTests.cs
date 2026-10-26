using Seamdesk.Core;
using Seamdesk.Transport;

namespace Seamdesk.Host.Tests;

public class SettingsTests
{
    [Fact]
    public void Every_setting_survives_a_save_and_load_and_a_hand_edited_older_file_still_loads()
    {
        string dir = Directory.CreateTempSubdirectory("seamdesk-settings-").FullName;
        Settings s = new()
        {
            Peers = [new Pin("ab12", "laptop", "192.168.1.20")],
            Offsets = { ["pc"] = new Point(0, 0), ["laptop"] = new Point(1440, 1105) },
            LayoutRevision = 7,
            Monitors = { ["laptop"] = [new Rect(0, 0, 2560, 1440)] },
            Clipboard = { ["laptop"] = ClipboardMode.BothWays },
            SnapBackHotkey = "Ctrl+Alt+Home",
            StayHotkey = "Win+Shift+F12",
            AutoPauseFullscreen = true,
            MwbPeers = [new MwbPeerSettings { Name = "old", Host = "old.lan", LegacySecurity = true, Version = MwbVersion.Current, Detected = MwbVersion.Current, AllowControl = true, Clipboard = ClipboardMode.BothWays, MachineId = 4242, Layouts = [[new Rect(0, 0, 1920, 1080)], [new Rect(0, 0, 2560, 1440), new Rect(2560, 0, 1920, 1200)]] }],
        };
        string path = Path.Combine(dir, "settings.json");

        s.Save(path);
        Settings back = Settings.Load(path);

        Assert.Equal(File.ReadAllText(path), Save(back, Path.Combine(dir, "again.json")));
        Assert.Contains("\"laptop\": \"BothWays\"", File.ReadAllText(path)); // readable by hand, as tools/mwb-check.md edits it
        Assert.Equal(new Point(1440, 1105), back.Offsets["laptop"]);
        Assert.Equal(new Rect(2560, 0, 1920, 1200), back.MwbPeers[0].Layouts[1][1]);
        Assert.Contains("\"Detected\": \"Current\"", File.ReadAllText(path));
        Assert.DoesNotContain("SendClipboard", File.ReadAllText(path));

        // The file tools/mwb-check.md has people write for 0.4.0.
        File.WriteAllText(path, """
            {
              "Offsets": { "laptop": { "X": 0, "Y": 1080 } },
              "MwbPeers": [ { "Name": "laptop", "Host": "LAPTOP-NAME", "LegacySecurity": true, "SendClipboard": true,
                              "Layouts": [ [ { "Left": 0, "Top": 0, "Width": 2560, "Height": 1440 } ] ] } ]
            }
            """);
        Settings old = Settings.Load(path);
        Assert.Equal(new Point(0, 1080), old.Offsets["laptop"]);
        Assert.Equal([new Rect(0, 0, 2560, 1440)], old.MwbPeers[0].Layouts[0]);
        Assert.Empty(old.Clipboard);
        Assert.Equal((MwbVersion.Auto, null, false, ClipboardMode.ToPeer), (old.MwbPeers[0].Version, old.MwbPeers[0].Detected, old.MwbPeers[0].AllowControl, old.MwbPeers[0].Clipboard));
    }

    /// <summary>An older Seamdesk saved a second pin when a pinned machine paired again; the later one is the newer pairing.</summary>
    [Fact]
    public void Duplicate_pins_an_older_Seamdesk_saved_load_as_the_newer_one()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory("seamdesk-settings-").FullName, "settings.json");
        Pin newer = new("ab12", "laptop", "192.168.1.21");
        new Settings { Peers = [newer with { Address = "192.168.1.20" }, newer] }.Save(path);

        Assert.Equal([newer], Settings.Load(path).Peers);
    }

    private static string Save(Settings s, string path)
    {
        s.Save(path);
        return File.ReadAllText(path);
    }
}
