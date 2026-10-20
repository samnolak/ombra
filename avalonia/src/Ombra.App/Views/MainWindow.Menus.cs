using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.ViewModels;
using Ombra.App.Localization;

namespace Ombra.App.Views;

/// <summary>An Edit menu item, done to whatever text has the keyboard.</summary>
public enum EditAction { Undo, Redo, Cut, Copy, Paste, SelectAll }

public sealed partial class MainWindow
{
    /// <summary>
    /// The window's menu bar (macOS shows it at the top of the screen): File, Edit, View and Window, where Mac users
    /// and accessibility tools look for them, each item with its shortcut. A text box that has the keyboard handles its
    /// own shortcuts first (Avalonia offers the key to the menu only when the window left it unhandled), so the Edit
    /// items run when chosen with the mouse or VoiceOver, or when the keyboard is somewhere a key alone does nothing.
    /// While the Browser page's Nightly window has the keyboard, the items' shortcuts are that window's
    /// (<see cref="Nightly.NightlyShortcutMonitor"/> takes them before the menu).
    /// </summary>
    internal NativeMenu MenuBar(MainViewModel vm)
    {
        var cmd = this.GetPlatformSettings()?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Meta;
        NativeMenuItem Item(string header, System.Windows.Input.ICommand command, Key key = Key.None, KeyModifiers extra = KeyModifiers.None) =>
            new(header) { Command = command, Gesture = key == Key.None ? null : new KeyGesture(key, cmd | extra) };
        NativeMenuItem Edit(string header, EditAction action, Key key, KeyModifiers extra = KeyModifiers.None) =>
            Item(header, new RelayCommand(() => RunEdit(action)), key, extra);

        var file = new NativeMenu
        {
            Item("New Chat", vm.NewChatCommand, Key.N),
            Item("Open Folder…", vm.OpenFolderCommand, Key.O),
        };
        var edit = new NativeMenu
        {
            Edit("Undo", EditAction.Undo, Key.Z),
            Edit("Redo", EditAction.Redo, Key.Z, KeyModifiers.Shift),
            new NativeMenuItemSeparator(),
            Edit("Cut", EditAction.Cut, Key.X),
            Edit("Copy", EditAction.Copy, Key.C),
            Edit("Paste", EditAction.Paste, Key.V),
            Edit("Select All", EditAction.SelectAll, Key.A),
            new NativeMenuItemSeparator(),
            // Find in chat (MainWindow.Find.cs handles the keys first; the items are for the mouse and VoiceOver)
            Item("Find…", vm.OpenFindCommand, Key.F),
            Item("Find Next", vm.FindNextCommand, Key.G),
            // No key equivalent: ⇧⌘G toggles the Changes tab, and the menu would take it first. With the bar open the
            // tunnel handler gives ⇧⌘G to Find Previous.
            Item("Find Previous", vm.FindPreviousCommand),
        };
        var view = new NativeMenu
        {
            // The command palette (MainViewModel.Palette.cs): opening it again keeps it open, so the menu and the
            // window's key binding may both take the key
            Item("Command Palette…", vm.OpenPaletteCommand, Key.K),
            Item("Go to File…", vm.OpenFilePaletteCommand, Key.P),
            new NativeMenuItemSeparator(),
            Item("Sessions", vm.ToggleSidebarCommand),
            Item("Terminal", vm.ToggleTerminalCommand),
            Item("Events", vm.ToggleDebugCommand),
        };
        if (OperatingSystem.IsMacOS())
        {
            // Avalonia disables AppKit's automatic full-screen menu item. Own the window command
            // explicitly, including while the embedded browser has the keyboard.
            var fullScreen = Item(Loc.T("Enter Full Screen"),
                new RelayCommand(() => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen),
                Key.F, KeyModifiers.Control);
            void UpdateFullScreenTitle() => fullScreen.Header = Loc.T(WindowState == WindowState.FullScreen ? "Exit Full Screen" : "Enter Full Screen");
            UpdateFullScreenTitle();
            PropertyChanged += (_, e) =>
            {
                if (e.Property == WindowStateProperty) UpdateFullScreenTitle();
            };
            view.Add(new NativeMenuItemSeparator());
            view.Add(fullScreen);
        }
        var window = new NativeMenu
        {
            Item("Minimize", new RelayCommand(() => WindowState = WindowState.Minimized), Key.M),
            Item("Zoom", new RelayCommand(() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized)),
            new NativeMenuItemSeparator(),
            // Through the close sequence: asks first while omp works or a shell runs (MainWindow.Closing.cs)
            Item("Close", new RelayCommand(CloseActiveSurface), Key.W),
        };
        var help = new NativeMenu
        {
            Item(Loc.T("Keyboard shortcuts"), new RelayCommand(() => vm.IsShortcutsOpen = true)),
            Item(Loc.T("Report a problem"), new AsyncRelayCommand(() => vm.OpenSettingsAtAsync("diagnostics"))),
            Item(Loc.T("What's new"), new AsyncRelayCommand(() => vm.OpenSettingsAtAsync("updates"))),
        };
        return
        [
            new NativeMenuItem("File") { Menu = file },
            new NativeMenuItem("Edit") { Menu = edit },
            new NativeMenuItem("View") { Menu = view },
            new NativeMenuItem("Window") { Menu = window },
            new NativeMenuItem(Loc.T("Help")) { Menu = help },
        ];
    }

    /// <summary>An Edit menu item on the focused text: a text box (the message box, a search box…) or selectable text
    /// (Copy and Select All in a message). Nothing when the keyboard is elsewhere.</summary>
    internal void RunEdit(EditAction action)
    {
        switch (FocusManager?.GetFocusedElement())
        {
            case TextBox box:
                switch (action)
                {
                    case EditAction.Undo when box.CanUndo: box.Undo(); break;
                    case EditAction.Redo when box.CanRedo: box.Redo(); break;
                    case EditAction.Cut when box.CanCut: box.Cut(); break;
                    case EditAction.Copy when box.CanCopy: box.Copy(); break;
                    // The message box turns a pasted image or file into an attachment (its PastingFromClipboard handler)
                    case EditAction.Paste when box.CanPaste: box.Paste(); break;
                    case EditAction.SelectAll: box.SelectAll(); break;
                }
                break;
            case SelectableTextBlock text when action == EditAction.Copy:
                text.Copy();
                break;
            case SelectableTextBlock text when action == EditAction.SelectAll:
                text.SelectAll();
                break;
        }
    }
}
