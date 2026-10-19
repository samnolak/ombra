using Avalonia.Controls;
using Avalonia.Input;

namespace Ombra.App.Views;

/// <summary>
/// The 1 px splitters beside the right panel and Events and above the terminal: a 7 px strip grabs them (Styles.axaml,
/// a hairline in the focus colour while hovered or dragged), and a double-click restores the panel's default size, as the
/// sidebar's grip does.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The double-click's second press also starts the splitter's drag: its release must not keep the old size.</summary>
    private bool _splitterReset;

    private void WireSplitters()
    {
        RightPanelSplitter.DoubleTapped += (_, e) => ResetPanelSize(e, () => _rightPanelWidth = RightPanelDefaultWidth);
        DebugSplitter.DoubleTapped += (_, e) => ResetPanelSize(e, () => _debugWidth = DebugDefaultWidth);
        TerminalSplitter.DoubleTapped += (_, e) => ResetPanelSize(e, () => _terminalHeight = TerminalDefaultHeight);
        // A dragged right panel or Events width, and the terminal's height, are kept for the next time they open (a
        // splitter between two docked panels resizes both)
        foreach (var splitter in new[] { RightPanelSplitter, DebugSplitter, TerminalSplitter })
            splitter.DragCompleted += (_, _) =>
            {
                if (_splitterReset)
                {
                    _splitterReset = false;
                    FitPanels();
                    return;
                }
                if (ReferenceEquals(splitter, TerminalSplitter))
                {
                    if (WorkArea.RowDefinitions[2].ActualHeight > 0) _terminalHeight = WorkArea.RowDefinitions[2].ActualHeight;
                }
                else KeepDraggedWidths();
            };
    }

    private void ResetPanelSize(TappedEventArgs e, Action reset)
    {
        reset();
        _splitterReset = true;
        FitPanels();
        e.Handled = true;
    }
}
