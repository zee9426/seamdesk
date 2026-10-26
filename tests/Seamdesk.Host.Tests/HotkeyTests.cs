using Seamdesk.Host;

namespace Seamdesk.Host.Tests;

public class HotkeyTests
{
    public static TheoryData<string, Modifiers, ushort> Valid => new()
    {
        { "Ctrl+Alt+Home", Modifiers.Ctrl | Modifiers.Alt, 0x24 },
        { "shift + win + f12", Modifiers.Shift | Modifiers.Win, 0x7B },
        { "Alt+Ctrl+S", Modifiers.Ctrl | Modifiers.Alt, 0x53 },
        { "Control+5", Modifiers.Ctrl, 0x35 },
        { "Ctrl+F1", Modifiers.Ctrl, 0x70 },
        { "Ctrl+F24", Modifiers.Ctrl, 0x87 },
    };

    [Theory]
    [MemberData(nameof(Valid))]
    public void Parses_modifiers_in_any_order_and_one_key(string text, Modifiers mods, ushort vk) =>
        Assert.Equal(new Hotkey(mods, vk), Hotkey.Parse(text));

    [Theory]
    [InlineData("Home")]        // no modifier: would swallow ordinary typing
    [InlineData("Ctrl+Alt")]    // no key
    [InlineData("Ctrl+A+B")]    // two keys
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Foo")]
    public void Rejects(string text) => Assert.Throws<FormatException>(() => Hotkey.Parse(text));

    /// <summary>What the capture field makes of a key press, and what it shows: modifiers in Windows' order, then the key.</summary>
    [Theory]
    [InlineData(Modifiers.Alt | Modifiers.Ctrl, 0x24, "Ctrl+Alt+Home")]
    [InlineData(Modifiers.Shift | Modifiers.Win, 0x7B, "Win+Shift+F12")]
    [InlineData(Modifiers.Ctrl, 0x35, "Ctrl+5")]
    [InlineData(Modifiers.None, 0x24, null)]    // no modifier
    [InlineData(Modifiers.Ctrl, 0xBA, null)]    // a key Seamdesk can't name (OEM ;)
    public void A_captured_key_shows_as_text_that_parses_back(Modifiers mods, ushort vk, string? shown)
    {
        Hotkey? captured = Hotkey.From(mods, vk);

        Assert.Equal(shown, captured?.ToString());
        if (captured is { } h)
        {
            Assert.Equal(h, Hotkey.Parse(h.ToString()));
        }
    }

    /// <summary>The capture field's verdict on a new "snap back" while "stay" is Ctrl+Alt+Insert.</summary>
    [Theory]
    [InlineData("Ctrl+Alt+Home", false, null)]
    [InlineData("Win+Shift+F12", false, null)]
    [InlineData("Ctrl+Alt+Insert", true, "That's already the other Seamdesk shortcut.")]
    [InlineData("Ctrl+Alt+P", true, "Seamdesk always keeps Ctrl+Alt+P as a second shortcut.")]
    [InlineData("Win+L", true, "Windows uses Win+L itself.")]
    [InlineData("Alt+Tab", true, "Windows uses Alt+Tab itself.")]
    [InlineData("Alt+F4", true, "Windows uses Alt+F4 itself.")]
    [InlineData("Ctrl+Shift+Escape", true, "Windows uses Ctrl+Shift+Escape itself.")]
    [InlineData("Ctrl+C", false, "Apps often use Ctrl+C. Seamdesk would take it from them everywhere.")]
    [InlineData("Ctrl+Shift+Left", false, "Apps often use Ctrl+Shift+Left. Seamdesk would take it from them everywhere.")]
    public void Conflicts_with_Windows_block_and_app_shortcuts_warn(string text, bool blocked, string? problem)
    {
        Assert.Equal(problem, Hotkey.Parse(text).Problem(Hotkey.DefaultStay, out bool isBlocked));
        Assert.Equal(blocked, isBlocked);
    }

    [Fact]
    public void Fires_on_exact_modifiers_and_swallows_only_the_hotkeys_own_key()
    {
        const ushort LCtrl = 0xA2, RCtrl = 0xA3, RAlt = 0xA5, LShift = 0xA0, Home = 0x24, Ins = 0x2D;
        HotkeyMatcher m = new(Hotkey.DefaultSnapBack, Hotkey.DefaultStay);

        // vk, down, fires, swallowed
        (ushort, bool, Hotkey?, bool)[] script =
        [
            (LCtrl, true, null, false),
            (RAlt, true, null, false),
            (Home, true, Hotkey.DefaultSnapBack, true),
            (Home, true, null, true),                 // auto-repeat of the held hotkey: still swallowed, fires once
            (Home, false, null, true),                // its key-up is swallowed too
            (LShift, true, null, false),
            (Home, true, null, false),                // Ctrl+Alt+Shift+Home: extra modifier, no match, reaches the app
            (Home, false, null, false),
            (LShift, false, null, false),
            (RCtrl, true, null, false),
            (LCtrl, false, null, false),              // right Ctrl still held: still Ctrl
            (Ins, true, Hotkey.DefaultStay, true),
            (Ins, false, null, true),
            (RAlt, false, null, false),
            (Home, true, null, false),                // Ctrl+Home: Alt is up, no match
        ];

        foreach ((ushort vk, bool down, Hotkey? fires, bool swallowed) in script)
        {
            Hotkey? got = m.Feed(vk, down, out bool swallow);
            Assert.Equal((fires, swallowed), (got, swallow));
        }
    }
}
