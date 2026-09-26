using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;

namespace OmpGui.App.Views.Settings;

/// <summary>
/// The Computer use, Git and worktrees and SSH pages scroll what just appeared into view: the feedback line at the top
/// after a save lower down, a form when it opens (its first field focused).
/// </summary>
internal static class WorkspaceReveal
{
    /// <summary>Reveals <paramref name="target"/> (and focuses <paramref name="focus"/>) whenever <paramref name="when"/>
    /// becomes true after <paramref name="property"/> changes on <paramref name="source"/>.</summary>
    public static void On(INotifyPropertyChanged source, string property, Func<bool> when, Control target, Control? focus = null)
    {
        source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != property || !when()) return;
            // After the layout pass that makes it visible
            Dispatcher.UIThread.Post(() =>
            {
                target.BringIntoView();
                focus?.Focus();
            }, DispatcherPriority.Background);
        };
    }
}
