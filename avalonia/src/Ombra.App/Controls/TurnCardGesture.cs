using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Ombra.App.ViewModels;

namespace Ombra.App.Controls;

/// <summary>Alt-click a disclosure chevron to apply its next state to every card in that turn, including unrealized rows.</summary>
public sealed class TurnCardGesture : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TurnCardGesture, ListBox, bool>("IsEnabled");

    static TurnCardGesture() => IsEnabledProperty.Changed.AddClassHandler<ListBox>((list, change) =>
    {
        list.RemoveHandler(InputElement.PointerPressedEvent, Pressed);
        if (change.NewValue is true)
            list.AddHandler(InputElement.PointerPressedEvent, Pressed, RoutingStrategies.Tunnel);
    });

    public static bool GetIsEnabled(ListBox list) => list.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(ListBox list, bool value) => list.SetValue(IsEnabledProperty, value);

    private static void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox list || e.KeyModifiers != KeyModifiers.Alt
            || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed
            || list.ItemsSource is not IList<RowViewModel> rows) return;
        var chevron = false;
        for (var visual = e.Source as Visual; visual is not null && visual != list; visual = visual.GetVisualParent())
        {
            if (visual is Icon icon && (icon.Classes.Contains("chevron") || icon.Classes.Contains("card-chevron"))) chevron = true;
            if (visual is not Button button) continue;
            if (!chevron)
                foreach (var child in button.GetVisualDescendants())
                    if (child is Icon candidate && candidate.IsEffectivelyVisible
                        && (candidate.Classes.Contains("chevron") || candidate.Classes.Contains("card-chevron"))
                        && new Rect(candidate.Bounds.Size).Contains(e.GetPosition(candidate)))
                    {
                        chevron = true;
                        break;
                    }
            if ((chevron || button.Classes.Contains("diff-collapse")) && button.DataContext is RowViewModel row
                && Expanded(row) is { } expanded && SetTurnExpanded(rows, row, !expanded))
                e.Handled = true;
            return;
        }
    }

    internal static bool SetTurnExpanded(IList<RowViewModel> rows, RowViewModel source, bool expanded)
    {
        var at = rows.IndexOf(source);
        if (at < 0) return false;
        var start = at;
        while (start > 0 && rows[start] is not UserRowViewModel) start--;
        for (var i = start; i < rows.Count && (i == start || rows[i] is not UserRowViewModel); i++)
        {
            switch (rows[i])
            {
                case TurnFoldRowViewModel { ShowChevron: true } fold: fold.IsExpanded = expanded; break;
                case StepGroupRowViewModel group: group.IsExpanded = expanded; break;
                case ToolRowViewModel { CanExpand: true } tool: tool.IsExpanded = expanded; break;
                case AssistantRowViewModel { HasThinking: true } reply: reply.IsThinkingOpen = expanded; break;
                case TurnEndRowViewModel end: end.IsFilesOpen = expanded; break;
                case AgentMessageRowViewModel agent: agent.IsExpanded = expanded; break;
            }
        }
        return true;
    }

    private static bool? Expanded(RowViewModel row) => row switch
    {
        TurnFoldRowViewModel { ShowChevron: true } fold => fold.IsExpanded,
        StepGroupRowViewModel group => group.IsExpanded,
        ToolRowViewModel { CanExpand: true } tool => tool.IsExpanded,
        AssistantRowViewModel { HasThinking: true } reply => reply.IsThinkingOpen,
        TurnEndRowViewModel end => end.IsFilesOpen,
        AgentMessageRowViewModel agent => agent.IsExpanded,
        _ => null,
    };
}
