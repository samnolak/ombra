using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Views;

/// <summary>An Edit menu item, done to whatever text has the keyboard.</summary>
public enum EditAction { Undo, Redo, Cut, Copy, Paste, SelectAll }

public sealed partial class MainWindow
{
    /// <summary>
    /// The window's menu bar (macOS shows it at the top of the screen): File, Edit, View and Window, where Mac users
    /// and accessibility tools look for them, each item with its shortcut. A text box that has the keyboard handles its
    /// own shortcuts first (Avalonia offers the key to the menu only when the window left it unhandled), so the Edit
    /// items run when chosen with the mouse or VoiceOver, or when the keyboard is somewhere a key alone does nothing.
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
            Item("New Session", vm.NewSessionCommand, Key.N),
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
        };
        var view = new NativeMenu
        {
            Item("Sessions", vm.ToggleSidebarCommand),
            Item("Terminal", vm.ToggleTerminalCommand),
            Item("Events", vm.ToggleDebugCommand),
        };
        var window = new NativeMenu
        {
            Item("Minimize", new RelayCommand(() => WindowState = WindowState.Minimized), Key.M),
            Item("Zoom", new RelayCommand(() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized)),
            new NativeMenuItemSeparator(),
            // Through the close sequence: asks first while omp works or a shell runs (MainWindow.Closing.cs)
            Item("Close", new RelayCommand(Close), Key.W),
        };
        return
        [
            new NativeMenuItem("File") { Menu = file },
            new NativeMenuItem("Edit") { Menu = edit },
            new NativeMenuItem("View") { Menu = view },
            new NativeMenuItem("Window") { Menu = window },
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
