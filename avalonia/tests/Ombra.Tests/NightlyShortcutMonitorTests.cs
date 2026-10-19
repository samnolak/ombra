using Ombra.App.Nightly;

namespace Ombra.Tests;

/// <summary>Which key downs the Browser page's Nightly window takes from Ombra (NightlyShortcutMonitor).</summary>
public sealed class NightlyShortcutMonitorTests
{
    private const ulong Command = 1UL << 20, Control = 1UL << 18, Shift = 1UL << 17, Option = 1UL << 19;

    [Theory]
    [InlineData(Command, 0x0C)] // ⌘Q
    [InlineData(Command, 0x04)] // ⌘H
    [InlineData(Command | Option, 0x04)] // ⌥⌘H
    [InlineData(Command, 0x2E)] // ⌘M
    [InlineData(Command, 0x32)] // ⌘`
    [InlineData(Command, 0x35)] // ⌘Esc
    [InlineData(0UL, 0x00)] // plain typing
    [InlineData(Shift, 0x00)]
    public void Ombra_keeps_the_apps_own_keys_and_plain_typing(ulong modifiers, int keyCode)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.True(NightlyShortcutMonitor.IsOmbras(modifiers, keyCode));
    }

    [Theory]
    [InlineData(Command, 0x11)] // ⌘T
    [InlineData(Command, 0x25)] // ⌘L
    [InlineData(Command, 0x0D)] // ⌘W
    [InlineData(Command | Shift, 0x11)] // ⇧⌘T
    [InlineData(Control, 0x30)] // ⌃Tab
    [InlineData(Command, 0x2B)] // ⌘, (Nightly's settings)
    [InlineData(Control | Option, 0x7C)] // a space's key
    [InlineData(Command | Option, 0x7C)]
    public void The_browser_window_takes_its_shortcuts(ulong modifiers, int keyCode)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.False(NightlyShortcutMonitor.IsOmbras(modifiers, keyCode));
    }
}
