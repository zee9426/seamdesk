using System.Text.Json;
using System.Text.Json.Serialization;
using Seamdesk.Core;
using Seamdesk.Transport;

namespace Seamdesk.Host;

/// <summary>Which way copy and paste goes between this machine and a peer. Off unless the user chooses.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClipboardMode>))]
public enum ClipboardMode { Off, ToPeer, BothWays }

public static class ClipboardModes
{
    /// <summary>This machine's text goes to the peer when the cursor crosses there.</summary>
    public static bool Sends(this ClipboardMode mode) => mode is ClipboardMode.ToPeer or ClipboardMode.BothWays;

    /// <summary>The peer's text is taken only both ways, so "this PC to the peer" never lets it land here.</summary>
    public static bool Takes(this ClipboardMode mode) => mode == ClipboardMode.BothWays;
}

/// <summary>
/// Which Mouse Without Borders wire format to speak to a peer: whichever it speaks (Auto), the format of standalone MWB and
/// PowerToys up to 0.100 (Old), or that of PowerToys 0.101 and later (Current).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MwbVersion>))]
public enum MwbVersion { Auto, Old, Current }

/// <summary>%LOCALAPPDATA%\Seamdesk\settings.json, edited by the settings window.</summary>
public sealed class Settings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Paired peers (public key hashes, not secrets).</summary>
    public List<Pin> Peers { get; set; } = [];

    /// <summary>Where each machine's desktop origin sits on the desk, by machine name. A peer without one is not on the desk.</summary>
    public Dictionary<string, Point> Offsets { get; set; } = [];

    public long LayoutRevision { get; set; }

    /// <summary>The last monitors each Seamdesk peer reported, so the layout editor can place a peer that is offline.</summary>
    public Dictionary<string, List<Rect>> Monitors { get; set; } = [];

    /// <summary>
    /// Copy and paste per Seamdesk peer, by name. This machine sends its text to the peer when the cursor crosses there if
    /// set to ToPeer or BothWays, and takes the peer's text only when set to BothWays.
    /// </summary>
    public Dictionary<string, ClipboardMode> Clipboard { get; set; } = [];

    public string? SnapBackHotkey { get; set; }

    public string? StayHotkey { get; set; }

    /// <summary>Don't cross while a fullscreen app (game, presentation) is focused. Off by default.</summary>
    public bool AutoPauseFullscreen { get; set; }

    /// <summary>Machines running Mouse Without Borders (compatibility mode).</summary>
    public List<MwbPeerSettings> MwbPeers { get; set; } = [];

    /// <summary>Every peer by name, Seamdesk peers first. A machine that is both (an MWB peer since paired as Seamdesk) is listed once.</summary>
    [JsonIgnore]
    public IEnumerable<string> PeerNames => Peers.Select(p => p.Name).Concat(MwbPeers.Select(p => p.Name)).Distinct();

    /// <summary>The peers Seamdesk links with, as the tray lists them: every Seamdesk peer, and MWB peers with legacy security on.</summary>
    [JsonIgnore]
    public IEnumerable<string> LinkedPeerNames => Peers.Select(p => p.Name).Concat(MwbPeers.Where(p => p.LegacySecurity).Select(p => p.Name)).Distinct();

    public static Settings Load(string path)
    {
        Settings s = File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new() : new();

        // Older Seamdesk added a second pin when a pinned machine paired again; the later one is the newer pairing.
        s.Peers = [.. s.Peers.GroupBy(p => p.Spki).Select(g => g.Last())];
        return s;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>A deep copy, for the settings window to edit until the user saves.</summary>
    public Settings Clone() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, Json), Json)!;
}

/// <summary>A machine running Mouse Without Borders instead of Seamdesk.</summary>
public sealed class MwbPeerSettings
{
    /// <summary>The peer's name on the desk (the key of its offset).</summary>
    public string Name { get; set; } = "";

    /// <summary>Its host name or address. Seamdesk dials only this, and accepts a connection only from it.</summary>
    public string Host { get; set; } = "";

    /// <summary>
    /// "Legacy security": talk MWB's protocol, protected only by MWB's shared key. Off unless turned on, and off again
    /// once the peer pairs as Seamdesk.
    /// </summary>
    public bool LegacySecurity { get; set; }

    /// <summary>The wire format to speak. Auto shows what it found in <see cref="Detected"/>.</summary>
    public MwbVersion Version { get; set; }

    /// <summary>
    /// The format the peer spoke last (Old or Current). Once it is Current, Auto refuses the old format until the user
    /// chooses Old: it never falls back on its own.
    /// </summary>
    public MwbVersion? Detected { get; set; }

    /// <summary>"Let this machine control Seamdesk": the peer's MWB may drive this machine. Off unless turned on.</summary>
    public bool AllowControl { get; set; }

    /// <summary>
    /// Copy and paste with the peer: this machine's text goes to it with ToPeer or BothWays; its text is taken only with
    /// BothWays.
    /// </summary>
    public ClipboardMode Clipboard { get; set; }

    /// <summary>Settings from 0.4 to 0.6 say <c>"SendClipboard": true</c> for <see cref="ClipboardMode.ToPeer"/>. Never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SendClipboard
    {
        get => false;
        set => Clipboard = value ? ClipboardMode.ToPeer : Clipboard;
    }

    /// <summary>This machine's MWB id towards the peer; made up on first use.</summary>
    public uint MachineId { get; set; }

    /// <summary>The peer's monitor layouts, in its own desktop coordinates. MWB never sends them.</summary>
    public List<List<Rect>> Layouts { get; set; } = [];

    /// <summary>Every saved layout has a monitor, and there is one. Without, Seamdesk doesn't link to the peer.</summary>
    [JsonIgnore]
    public bool HasScreens => Layouts.Count > 0 && Layouts.All(l => l.Count > 0);
}
