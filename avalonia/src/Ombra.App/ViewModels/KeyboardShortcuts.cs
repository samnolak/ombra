using Ombra.App.Localization;

namespace Ombra.App.ViewModels;

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

    /// <summary>The rail's Editor page (⌘⇧E is Thinking effort).</summary>
    public static string EditorPage { get; } = Text(OperatingSystem.IsMacOS(), Mod.Command | Mod.Shift, "O");

    /// <summary>Steering a running reply (placeholder, Steer's tooltip).</summary>
    public static string Steer { get; } = Text(OperatingSystem.IsMacOS(), Mod.Alt, "Enter");

    /// <summary>Interrupt and send: stop the running reply and send the message in its place (menu, tooltip).</summary>
    public static string InterruptAndSend { get; } = Text(OperatingSystem.IsMacOS(), Mod.Alt | Mod.Shift, "Enter");

    /// <summary>The find bar's next and previous match (its buttons' tooltips).</summary>
    public static string FindNext { get; } = Text(OperatingSystem.IsMacOS(), Mod.Command, "G");
    public static string FindPrevious { get; } = Text(OperatingSystem.IsMacOS(), Mod.Command | Mod.Shift, "G");
    public static string Terminal { get; } = Text(OperatingSystem.IsMacOS(), Mod.Control, "`");
    public static string TerminalPanelHint => Loc.F("{0} shows or hides this panel", Terminal);
    public static string TerminalPanelHideTip => Loc.F("Hide the terminal panel ({0})", Terminal);

    /// <param name="mac">⌘-style keycaps.</param>
    /// <param name="sendWithModifier">Settings → General sends with ⌘/Ctrl+Enter: Enter then adds a line.</param>
    public static IReadOnlyList<KeyboardShortcutGroup> For(bool mac, bool sendWithModifier = false)
    {
        KeyboardShortcut S(string action, Mod mods, string key, string? note = null) => new(Loc.T(action), Keys(mac, mods, key), note is null ? null : Loc.T(note));
        var send = sendWithModifier ? Mod.Command : Mod.None;
        return
        [
            new(Loc.T("General"),
            [
                S("Command palette", Mod.Command, "K"),
                S("Go to file", Mod.Command, "P"),
                S("New chat", Mod.Command, "N"),
                S("Show or hide the sidebar", Mod.Command, "B"),
                S("Focus the message box", Mod.Command, "L", "outside the browser"),
                S("Focus the message box", Mod.Command, "Esc"),
                S("Next session", Mod.Control, "Tab"),
                S("Previous session", Mod.Control | Mod.Shift, "Tab"),
                S("Open the sidebar's chat 1–9", Mod.Command, "1–9"),
                S("Back to the previous chat", Mod.Command, "["),
                S("Forward to the next chat", Mod.Command, "]"),
                S("Next chat that needs you", Mod.Command | Mod.Alt, "A"),
                S("Find in chat", Mod.Command, "F"),
                S("Next match", Mod.Command, "G", "while finding in chat"),
                S("Previous match", Mod.Command | Mod.Shift, "G", "while finding in chat"),
                S("Settings", Mod.Command, ","),
                S("Leave settings", Mod.None, "Esc", "on the settings page"),
                S("Keyboard shortcuts", Mod.Command, "/"),
                S("Terminal", Mod.Control, "`"),
                S("Events (for bug reports)", Mod.None, "F12"),
            ]),
            new(Loc.T("Sessions"),
            [
                S("Move between sessions", Mod.None, "↑ / ↓", "in the session list"),
                S("Open session", Mod.None, "Enter", "in the session list"),
                S("Rename", Mod.None, "F2", "in the session list"),
                S("Delete…", Mod.None, "Delete", "in the session list"),
                S("Mark as read / unread", Mod.Command | Mod.Shift, "U"),
                S("Mark all as read", Mod.Shift, "Esc"),
                S("Resize sidebar", Mod.None, "← / →", "on the sidebar resize handle"),
                S("Reset sidebar width", Mod.None, "Home", "on the sidebar resize handle"),
            ]),
            new(Loc.T("Message"),
            [
                S("Send", send, "Enter"),
                S("New line", sendWithModifier ? Mod.None : Mod.Shift, "Enter"),
                S("Queue for when omp finishes", send, "Enter", "while omp works"),
                S("Steer omp at its next step", Mod.Alt, "Enter", "while omp works"),
                S("Stop omp and send this instead", Mod.Alt | Mod.Shift, "Enter", "while omp works"),
                S("Edit the last queued message", Mod.None, "↑", "in an empty message box"),
                S("Commands and skills", Mod.None, "/"),
                S("Switch permission mode", Mod.Shift, "Tab"),
                S("Choose model", Mod.Command | Mod.Shift, "I"),
                S("Thinking effort", Mod.Command | Mod.Shift, "E"),
                S("Dictate", Mod.Command | Mod.Shift, "Space"),
                S("Stop omp", Mod.None, "Esc"),
            ]),
            new(Loc.T("Requests"),
            [
                S("Choose an answer", Mod.None, "1–9"),
                S("Dismiss the request", Mod.None, "Esc", "when the request has focus"),
            ]),
            new(Loc.T("Panels"),
            [
                // In the ⋯ menu's order (the right panel's tabs)
                S("Close the focused pane", Mod.Command, "\\"),
                S("Files", Mod.Command | Mod.Shift, "F"),
                S("Changes", Mod.Command | Mod.Shift, "G"),
                S("Background tasks", Mod.Command | Mod.Shift, "T"),
                S("Plan", Mod.Command | Mod.Shift, "P"),
                S("Browser preview", Mod.Command | Mod.Shift, "B"),
                S("Comment on the page", Mod.Command | Mod.Shift, "S"),
                S("Editor page", Mod.Command | Mod.Shift, "O"),
                S("Go to line", Mod.Control, "G", "in the editor"),
                S("Find and replace", Mod.Command, "F / H", "in the editor"),
                S("Close focused tab or window", Mod.Command, "W"),
                S("Focus address", Mod.Command, "L", "in the browser"),
                S("New tab", Mod.Command, "T", "in the browser"),
                S("Reload", Mod.Command, "R", "in the browser"),
                S("Back", Mod.Command, "[", "in the browser"),
                S("Forward", Mod.Command, "]", "in the browser"),
                S("Copy page URL", Mod.Command | Mod.Shift, "C", "in the browser"),
                S("Next tab", Mod.Control, "Tab", "in the browser"),
                S("Previous tab", Mod.Control | Mod.Shift, "Tab", "in the browser"),
                S("Device size in the preview", Mod.Command | Mod.Shift, "M"),
            ]),
        ];
    }
}
