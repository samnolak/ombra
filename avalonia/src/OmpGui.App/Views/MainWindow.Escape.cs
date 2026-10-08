using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace OmpGui.App.Views;

public sealed partial class MainWindow
{
    /// <summary>
    /// Esc that reached the window from somewhere it means "leave this" rather than "stop omp": a search or address box
    /// (the sidebar's and the Files filter once they are empty: the first Esc clears them; the preview's), the preview, a
    /// terminal tab (the shell or the program in it already got the key; vim's Esc must not stop the run), or the session
    /// card (Esc is its ×). Text boxes hand the keyboard back to the message box. Esc stops the run only from the message
    /// box, the conversation or a toolbar button.
    /// </summary>
    private bool EscapeStaysLocal()
    {
        if (FocusManager?.GetFocusedElement() is not Visual focused) return false;
        if (Within(TerminalPanel, focused)) return true;
        if (Within(SessionCardHost, focused))
        {
            if (Vm?.SessionCard?.DismissCommand is { } dismiss && dismiss.CanExecute(null)) dismiss.Execute(null);
            return true;
        }
        // The sidebar's search, as a Mac search field: the first Esc clears it (the whole list again, the keyboard stays
        // there), the next one leaves it
        if (ReferenceEquals(focused, SessionFilterBox) && Vm is { SessionFilter.Length: > 0 } vm)
        {
            vm.SessionFilter = "";
            return true;
        }
        if (focused is TextBox box && !ReferenceEquals(box, Composer) || Within(PreviewPanel, focused))
        {
            if (Composer.IsEffectivelyVisible && Composer.IsEffectivelyEnabled) Composer.Focus();
            return true;
        }
        return false;

        static bool Within(Visual host, Visual v) => ReferenceEquals(host, v) || host.IsVisualAncestorOf(v);
    }
}
