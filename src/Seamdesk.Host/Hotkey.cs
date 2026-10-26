namespace Seamdesk.Host;

[Flags]
public enum Modifiers { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

/// <summary>A key combination such as "Ctrl+Alt+Home": exact modifiers plus one virtual key.</summary>
public readonly record struct Hotkey(Modifiers Modifiers, ushort Key)
{
    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["Pause"] = 0x13, ["ScrollLock"] = 0x91, ["Tab"] = 0x09, ["Escape"] = 0x1B,
    };

    // After Named: static fields initialise in order.
    public static readonly Hotkey DefaultSnapBack = Parse("Ctrl+Alt+Home");
    public static readonly Hotkey DefaultStay = Parse("Ctrl+Alt+Insert");

    /// <summary>
    /// Fixed second chords for keyboards without Home or Insert: H for home, P for pin. Not Windows' own, and none of MWB's
    /// defaults (Ctrl+Alt+F1-F4 to switch, Win+Ctrl+Alt+E, L and R: MouseWithoutBordersProperties.cs:33-39, :188).
    /// </summary>
    public static readonly Hotkey AlsoSnapBack = Parse("Ctrl+Alt+H");
    public static readonly Hotkey AlsoStay = Parse("Ctrl+Alt+P");

    /// <summary>"Ctrl+Alt+Home", "Win+Shift+F12", "Ctrl+Alt+S". Modifiers in any order; exactly one other key.</summary>
    public static Hotkey Parse(string text)
    {
        Modifiers mods = Modifiers.None;
        ushort? key = null;
        foreach (string part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            Modifiers mod = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => Modifiers.Ctrl,
                "alt" => Modifiers.Alt,
                "shift" => Modifiers.Shift,
                "win" => Modifiers.Win,
                _ => Modifiers.None,
            };
            if (mod != Modifiers.None)
            {
                mods |= mod;
            }
            else if (key is null && KeyOf(part) is { } vk)
            {
                key = vk;
            }
            else
            {
                throw new FormatException($"'{text}': '{part}' is not a key, or there is more than one key");
            }
        }

        // A bare key would swallow ordinary typing.
        return key is { } k && mods != Modifiers.None ? new Hotkey(mods, k) : throw new FormatException($"'{text}' needs a modifier and a key");
    }

    /// <summary>The key just pressed with these modifiers, as the capture field sees it; null if Seamdesk can't name the key.</summary>
    public static Hotkey? From(Modifiers modifiers, ushort vk) =>
        modifiers != Modifiers.None && NameOf(vk) is not null ? new Hotkey(modifiers, vk) : null;

    /// <summary>"Ctrl+Alt+Home": what <see cref="Parse"/> reads back, and what the settings window shows.</summary>
    public override string ToString()
    {
        Modifiers held = Modifiers;
        IEnumerable<string> mods = new[] { (Modifiers.Win, "Win"), (Modifiers.Ctrl, "Ctrl"), (Modifiers.Alt, "Alt"), (Modifiers.Shift, "Shift") }
            .Where(m => held.HasFlag(m.Item1)).Select(m => m.Item2);
        return string.Join("+", mods.Append(NameOf(Key) ?? $"0x{Key:X2}"));
    }

    /// <summary>
    /// Why this hotkey is a poor choice, or null if it's fine. <paramref name="blocked"/> is set when Seamdesk must refuse
    /// it: Windows keeps it for itself, or it is the other Seamdesk hotkey. Otherwise the reason is a warning: Seamdesk would
    /// take a shortcut that apps use.
    /// </summary>
    public string? Problem(Hotkey other, out bool blocked)
    {
        blocked = true;
        if (this == other)
        {
            return "That's already the other Seamdesk shortcut.";
        }

        if (this == AlsoSnapBack || this == AlsoStay)
        {
            return $"Seamdesk always keeps {this} as a second shortcut.";
        }

        bool letterOrDigit = Key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A;
        bool windows = Modifiers.HasFlag(Modifiers.Win) && (letterOrDigit || Key is 0x09 or 0x20 or 0x24 or >= 0x25 and <= 0x28 or 0x1B)
            || (Modifiers is Modifiers.Alt or (Modifiers.Alt | Modifiers.Shift) && Key is 0x09 or 0x1B or 0x20 or 0x73) // Alt+Tab, Alt+Esc, Alt+Space, Alt+F4
            || (Modifiers is Modifiers.Ctrl or (Modifiers.Ctrl | Modifiers.Shift) && Key == 0x1B)                      // Ctrl+Esc, Ctrl+Shift+Esc
            || (Modifiers == (Modifiers.Ctrl | Modifiers.Alt) && Key == 0x2E);                                           // Ctrl+Alt+Del
        if (windows)
        {
            return $"Windows uses {this} itself.";
        }

        blocked = false;
        bool appShortcut = (Modifiers is Modifiers.Ctrl or (Modifiers.Ctrl | Modifiers.Shift) or Modifiers.Alt && (letterOrDigit || Key is >= 0x70 and <= 0x87))
            || (Modifiers is Modifiers.Ctrl or Modifiers.Shift or (Modifiers.Ctrl | Modifiers.Shift) && Key is 0x09 or 0x20 or 0x21 or 0x22 or 0x23 or 0x24 or 0x2D or 0x2E or >= 0x25 and <= 0x28);
        return appShortcut ? $"Apps often use {this}. Seamdesk would take it from them everywhere." : null;
    }

    private static string? NameOf(ushort vk) => vk switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        _ => Named.FirstOrDefault(n => n.Value == vk).Key,
    };

    private static ushort? KeyOf(string name) => name.Length switch
    {
        1 when char.IsAsciiLetterOrDigit(name[0]) => char.ToUpperInvariant(name[0]),
        2 or 3 when (name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out int f) && f is >= 1 and <= 24 => (ushort)(0x6F + f),
        _ => Named.TryGetValue(name, out ushort vk) ? vk : null,
    };
}

/// <summary>
/// Watches the low-level keyboard hook's key stream for hotkeys. Tracks which modifiers are down (left and right count
/// the same), fires on the key-down of a matching combination and reports both that key-down and its key-up as
/// hotkey keys, so the host swallows them and the foreground app never sees half a hotkey.
/// </summary>
public sealed class HotkeyMatcher(params Hotkey[] hotkeys)
{
    private readonly HashSet<ushort> swallowing = [];
    private readonly HashSet<ushort> modifiersDown = [];

    /// <summary>The hotkey this key-down completes, or null. Sets <paramref name="swallow"/> for the hotkey's own key, down and up.</summary>
    public Hotkey? Feed(ushort vk, bool isDown, out bool swallow)
    {
        swallow = false;
        if (ModifierOf(vk) is not null)
        {
            _ = isDown ? modifiersDown.Add(vk) : modifiersDown.Remove(vk);
            return null;
        }

        if (!isDown)
        {
            swallow = swallowing.Remove(vk);
            return null;
        }

        if (swallowing.Contains(vk))
        {
            swallow = true; // auto-repeat of a held hotkey: fired once already
            return null;
        }

        Modifiers down = modifiersDown.Aggregate(Modifiers.None, (all, m) => all | ModifierOf(m)!.Value);
        foreach (Hotkey h in hotkeys)
        {
            if (h.Key == vk && h.Modifiers == down)
            {
                swallowing.Add(vk);
                swallow = true;
                return h;
            }
        }

        return null;
    }

    private static Modifiers? ModifierOf(ushort vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => Modifiers.Shift,
        0x11 or 0xA2 or 0xA3 => Modifiers.Ctrl,
        0x12 or 0xA4 or 0xA5 => Modifiers.Alt,
        0x5B or 0x5C => Modifiers.Win,
        _ => null,
    };
}
