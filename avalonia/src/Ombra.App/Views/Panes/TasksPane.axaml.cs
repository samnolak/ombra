using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Ombra.App.ViewModels;

namespace Ombra.App.Views.Panes;

/// <summary>The Background tasks pane: subagents and background jobs.</summary>
public partial class TasksPane : UserControl
{
    public TasksPane() => InitializeComponent();

    private void OnCardViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (sender is Control { DataContext: SubagentRowViewModel row } card)
        {
            var visible = e.EffectiveViewport.Intersect(new Rect(card.Bounds.Size));
            row.SetInViewport(card.IsEffectivelyVisible && visible.Width > 0 && visible.Height > 0);
        }
    }

    private void OnCardDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control { DataContext: SubagentRowViewModel row }) row.SetInViewport(false);
    }
}
