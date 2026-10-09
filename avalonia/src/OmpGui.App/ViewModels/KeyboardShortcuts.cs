namespace OmpGui.App.ViewModels;

/// <summary>One row of the shortcut sheet: what it does, its keys (one keycap each) and when it applies.</summary>
public sealed record KeyboardShortcut(string Action, IReadOnlyList<string> Keys, string? Note = null)
{
    public bool HasNote => Note is not null;
}

public sealed record KeyboardShortcutGroup(string Title, IReadOnlyList<KeyboardShortcut> Items);

/// <summary>
/// Every shortcut of the window, for the sheet ⌘/ (Ctrl+/) opens. The keys themselves live where they are handled
/// (MainWindow's KeyBindings and composer keys, the dictation bar, the request card); this list follows them.
/// </summary>
public static class KeyboardShortcuts
{
    [Flags]
    private enum Mod { None = 0, Command = 1, Shift = 2, Alt = 4, Control = 8 }

    /// <summary>The keycaps of a shortcut: macOS writes modifiers as symbols in the order ⌃⌥⇧⌘, elsewhere Ctrl, Alt, Shift.</summary>
    private static string[] Keys(bool mac, Mod mods, string key)
    {
        var caps = new List<string>();
        if (mac)
        {
            if (mods.HasFlag(Mod.Control)) caps.Add("⌃");
            if (mods.HasFlag(Mod.Alt)) caps.Add("⌥");
            if (mods.HasFlag(Mod.Shift)) caps.Add("⇧");
            if (mods.HasFlag(Mod.Command)) caps.Add("⌘");
        }
        else
        {
            if (mods.HasFlag(Mod.Command) || mods.HasFlag(Mod.Control)) caps.Add("Ctrl");
            if (mods.HasFlag(Mod.Alt)) caps.Add("Alt");
            if (mods.HasFlag(Mod.Shift)) caps.Add("Shift");
        }
        caps.Add(key);
        return [.. caps];
    }

    /// <summary>A shortcut as one string for a menu or a tooltip ("⇧⌘S", "Ctrl+Shift+S").</summary>
    private static string Text(bool mac, Mod mods, string key) => string.Join(mac ? "" : "+", Keys(mac, mods, key));

    /// <summary>"Comment on the page" in the composer's + menu.</summary>
    public static string CommentOnPage { get; } = Text(OperatingSystem.IsMacOS(), Mod.Command | Mod.Shift, "S");

    /// <summary>Steering a running reply (placeholder, Steer's tooltip).</summary>
    public static string Steer { get; } = Text(OperatingSystem.IsMacOS(), Mod.Alt, "Enter");

    /// <param name="mac">⌘-style keycaps.</param>
    /// <param name="sendWithModifier">Settings → General sends with ⌘/Ctrl+Enter: Enter then adds a line.</param>
    public static IReadOnlyList<KeyboardShortcutGroup> For(bool mac, bool sendWithModifier = false)
    {
        KeyboardShortcut S(string action, Mod mods, string key, string? note = null) => new(action, Keys(mac, mods, key), note);
        var send = sendWithModifier ? Mod.Command : Mod.None;
        return
        [
            new("General",
            [
                S("New session", Mod.Command, "N"),
                S("Show or hide the sidebar", Mod.Command, "B"),
                S("Next session", Mod.Control, "Tab"),
                S("Previous session", Mod.Control | Mod.Shift, "Tab"),
                S("Settings", Mod.Command, ","),
                S("Leave settings", Mod.None, "Esc", "on the settings page"),
                S("Keyboard shortcuts", Mod.Command, "/"),
                S("Terminal", Mod.Control, "`"),
                S("Events (for bug reports)", Mod.None, "F12"),
            ]),
            new("Message",
            [
                S("Send", send, "Enter"),
                S("New line", sendWithModifier ? Mod.None : Mod.Shift, "Enter"),
                S("Queue for when omp finishes", send, "Enter", "while omp works"),
                S("Steer omp at its next step", Mod.Alt, "Enter", "while omp works"),
                S("Edit the last queued message", Mod.None, "↑", "in an empty message box"),
                S("Commands and skills", Mod.None, "/"),
                S("Switch permission mode", Mod.Shift, "Tab"),
                S("Dictate", Mod.Command | Mod.Shift, "Space"),
                S("Stop omp", Mod.None, "Esc"),
            ]),
            new("Requests",
            [
                S("Choose an answer", Mod.None, "1–9"),
                S("Dismiss the request", Mod.None, "Esc", "when the request has focus"),
            ]),
            new("Panels",
            [
                // In the Views menu's order
                S("Files", Mod.Command | Mod.Shift, "F"),
                S("Background tasks", Mod.Command | Mod.Shift, "T"),
                S("Plan", Mod.Command | Mod.Shift, "P"),
                S("Browser preview", Mod.Command | Mod.Shift, "B"),
                S("Comment on the page", Mod.Command | Mod.Shift, "S"),
                S("Device size in the preview", Mod.Command | Mod.Shift, "M"),
            ]),
        ];
    }
}
