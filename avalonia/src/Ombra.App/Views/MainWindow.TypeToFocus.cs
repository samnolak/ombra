using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Ombra.App.Views;

/// <summary>
/// Typing while the conversation or the sidebar has the keyboard writes in the message box, as in Claude and ChatGPT: the
/// character goes there and the box takes the focus. Never from a text box, an editor or the terminal (they take their
/// own input, IME composition included), and never with Ctrl, ⌘ or Alt held (shortcuts).
/// </summary>
public sealed partial class MainWindow
{
    private KeyModifiers _heldModifiers;

    private void WireTypeToFocus()
    {
        AddHandler(KeyDownEvent, (_, e) => _heldModifiers = e.KeyModifiers, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, e) => _heldModifiers = e.KeyModifiers & ~ModifierOf(e.Key), RoutingStrategies.Tunnel, handledEventsToo: true);
        Deactivated += (_, _) => _heldModifiers = KeyModifiers.None;
        AddHandler(TextInputEvent, OnTypeToFocus, RoutingStrategies.Tunnel);
    }

    private void OnTypeToFocus(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not { Length: > 0 } text || !IsPrintable(text)) return;
        if ((_heldModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Alt)) != 0) return;
        if (FocusManager?.GetFocusedElement() is not Visual focused || !TypesIntoComposer(focused)) return;
        if (!Composer.IsEffectivelyVisible || !Composer.IsEffectivelyEnabled || Composer.IsReadOnly) return;
        e.Handled = true;
        Composer.Focus(NavigationMethod.Unspecified);
        var current = Composer.Text ?? "";
        Composer.Text = current + text;
        Composer.CaretIndex = Composer.Text.Length;
        Vm?.OnComposerTyped();
    }

    /// <summary>The conversation's rows or the sidebar's, not a field or editor in them (they take their own text).</summary>
    private bool TypesIntoComposer(Visual focused)
    {
        if (ReferenceEquals(focused, Composer) || focused is TextBox || ReferenceEquals(focused, _textInputOwner)) return false;
        for (Visual? v = focused; v is not null; v = v.GetVisualParent())
        {
            if (v is TextBox) return false;
            if (ReferenceEquals(v, Transcript) || ReferenceEquals(v, Sidebar)) return true;
        }
        return false;
    }

    private static bool IsPrintable(string text)
    {
        var any = false;
        foreach (var c in text)
        {
            if (char.IsControl(c)) return false;
            if (!char.IsWhiteSpace(c)) any = true;
        }
        return any;
    }

    private static KeyModifiers ModifierOf(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => KeyModifiers.Control,
        Key.LWin or Key.RWin => KeyModifiers.Meta,
        Key.LeftAlt or Key.RightAlt => KeyModifiers.Alt,
        Key.LeftShift or Key.RightShift => KeyModifiers.Shift,
        _ => KeyModifiers.None,
    };
}
