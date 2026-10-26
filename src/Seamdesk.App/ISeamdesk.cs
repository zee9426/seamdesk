using Seamdesk.Core;
using Seamdesk.Host;
using Seamdesk.Transport;

namespace Seamdesk.App;

internal enum Health { Good, Waiting, Problem, Off }

/// <summary>How a peer is doing, in words for the tray and the settings window. <see cref="Detail"/> is the reason, if any.</summary>
internal sealed record PeerStatus(string Text, Health Health, string? Detail = null)
{
    public static readonly PeerStatus Unknown = new("Not connected yet", Health.Waiting);

    public static PeerStatus Of(LinkState state) => state.Status switch
    {
        LinkStatus.Connected => new("Connected", Health.Good),
        LinkStatus.Connecting => new("Connecting…", Health.Waiting, Plain(state.Reason)),
        _ => new("Reconnecting…", state.Reason is null ? Health.Waiting : Health.Problem, Plain(state.Reason)),
    };

    // Socket messages read like "No connection could be made because the target machine actively refused it.": keep
    // them, but start them with a capital and end them once.
    private static string? Plain(string? reason) =>
        reason is null or "" ? null : char.ToUpperInvariant(reason[0]) + reason[1..].TrimEnd('.') + ".";
}

/// <summary>
/// What the settings window and the tray need from the running app. <see cref="App"/> is the real one; tools/Shots
/// fakes it to render every window without a network or hooks. Members are called and events raised on the UI thread.
/// </summary>
internal interface ISeamdesk
{
    /// <summary>This machine's name.</summary>
    string Me { get; }

    string Version { get; }

    /// <summary>This machine's monitors as Windows arranges them.</summary>
    IReadOnlyList<Rect> MyMonitors { get; }

    /// <summary>A snapshot of the settings in force. Edit it and pass it to <see cref="Apply"/>, or use <see cref="Change"/>.</summary>
    Settings Settings { get; }

    /// <summary>A peer's link changed, a pairing finished, or the active layout of an MWB peer changed.</summary>
    event Action? Changed;

    PeerStatus Status(string peer);

    /// <summary>The saved layout an MWB peer is using now (an index into its Layouts), or -1 if it isn't connected.</summary>
    int ActiveLayout(string mwbPeer);

    bool Staying { get; }

    void ToggleStay();

    /// <summary>Saves <paramref name="changed"/> and puts it into effect at once: desk, hotkeys, peers, clipboard.</summary>
    void Apply(Settings changed);

    /// <summary>One change to the settings, applied at once.</summary>
    void Change(Action<Settings> edit)
    {
        Settings s = Settings.Clone();
        edit(s);
        Apply(s);
    }

    /// <summary>A fresh 8-digit code for another machine to type; it works once, for 2 minutes.</summary>
    PairingCode ShowPairingCode();

    /// <summary>Pairs with the machine showing <paramref name="code"/>. Returns its name; throws with a plain message.</summary>
    Task<string> PairAsync(string host, string code);

    /// <summary>Forgets a peer, Seamdesk (unpairs it) or MWB, with its place on the desk and everything kept for it.</summary>
    void Remove(string peer);

    bool HasMwbKey(string peer);

    /// <summary>Keeps an MWB peer's security key (DPAPI). Returns why it was refused, or null.</summary>
    string? SetMwbKey(string peer, string key);

    /// <summary>While the shortcut field listens, Seamdesk's own hotkeys must not swallow the keys.</summary>
    void PauseHotkeys(bool paused);
}
