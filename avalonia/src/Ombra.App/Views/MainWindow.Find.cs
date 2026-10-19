using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App.Controls;
using Ombra.App.ViewModels;

namespace Ombra.App.Views;

/// <summary>
/// Find in chat's bar over the conversation (MainViewModel.Find.cs searches the rows): ⌘F / Ctrl+F opens it (with the
/// text selected in a message, if any), Enter and Shift+Enter or ⌘G and ⇧⌘G (Ctrl elsewhere) go through the matches, Esc
/// closes it and gives the keyboard back to the message box. Each match is scrolled into view by the transcript's
/// scroll owner (<see cref="ChatScrollController.Reveal"/>: its row realized there, however far) and selected in the
/// row's text.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The text holding the match shown, selected; cleared when another match is shown or the bar closes.</summary>
    private SelectableTextBlock? _findSelection;

    /// <summary>The keys (constructor).</summary>
    private void WireFindKeys()
    {
        // While the bar is open ⌘G / ⇧⌘G are its own wherever the keyboard is (⇧⌘G is the Changes tab otherwise)
        AddHandler(KeyDownEvent, OnFindKeyTunnel, RoutingStrategies.Tunnel);
        // ⌘F, and ⌘G with the bar closed: only when what has the keyboard left them alone (the file editor's own find,
        // a terminal's keys)
        AddHandler(KeyDownEvent, OnFindKeyBubble, RoutingStrategies.Bubble);
        FindBox.AddHandler(KeyDownEvent, OnFindBoxKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>The model's requests (OnDataContextChanged).</summary>
    private void WireFindModel(MainViewModel vm)
    {
        vm.FindFocusRequested += FocusFindBox;
        vm.FindRevealRequested += RevealFind;
        vm.FindViewRowRequested += FindViewRow;
        // Replies render again in a new theme's colors (MarkdownView): the match shown is selected in the new text
        ActualThemeVariantChanged += (_, _) =>
        {
            if (Vm?.FindShown is not null)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (Vm?.FindShown is { } m) RevealFind(m); }, Avalonia.Threading.DispatcherPriority.Background);
        };
    }

    private KeyModifiers CommandKey => this.GetPlatformSettings()?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    private void OnFindKeyTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.G || Vm is not { IsFindOpen: true } vm) return;
        if (e.KeyModifiers == CommandKey) vm.FindNextCommand.Execute(null);
        else if (e.KeyModifiers == (CommandKey | KeyModifiers.Shift)) vm.FindPreviousCommand.Execute(null);
        else return;
        e.Handled = true;
    }

    private void OnFindKeyBubble(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != CommandKey || Vm is not { IsSettingsOpen: false } vm) return;
        if (e.Key == Key.F)
        {
            // The words selected in a message are what is looked for (a line of them at most)
            if (FocusManager?.GetFocusedElement() is SelectableTextBlock { SelectedText: { Length: > 0 and <= 200 } selected } focusedText
                && Transcript.IsVisualAncestorOf(focusedText) && !selected.Contains('\n'))
                vm.OpenFindInChat(selected);
            else vm.OpenFindInChat();
            e.Handled = true;
        }
        else if (e.Key == Key.G && !string.IsNullOrEmpty(vm.FindQuery))
        {
            vm.FindNextCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Enter: the next match, Shift+Enter the previous one; Esc closes the bar.</summary>
    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                vm.FindNextCommand.Execute(null);
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.Shift:
                vm.FindPreviousCommand.Execute(null);
                break;
            case Key.Escape when e.KeyModifiers == KeyModifiers.None:
                vm.CloseFindCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void FocusFindBox()
    {
        if (TryFocus()) return;
        // The bar was hidden a moment ago: once it is laid out
        Dispatcher.UIThread.Post(() => TryFocus(), DispatcherPriority.Loaded);

        bool TryFocus()
        {
            if (!FindBox.IsEffectivelyVisible || !FindBox.Focus()) return false;
            FindBox.SelectAll();
            return true;
        }
    }

    /// <summary>The row at the top of the conversation's view, or -1 while it follows the latest message.</summary>
    private int FindViewRow()
    {
        if (_chatScroll is not { IsFollowing: false } || _scroll is null || Transcript.ItemsPanelRoot is not TranscriptPanel rows
            || rows.TranslatePoint(default, _scroll) is not { } origin) return -1;
        return rows.RowAtOffset(-origin.Y).Row;
    }

    /// <summary>Brings a match into view and selects it in its row's text; null clears the selection.</summary>
    private void RevealFind(FindMatch? match)
    {
        ClearFindSelection();
        if (match is null || _chatScroll is not { } scroll || Transcript.ItemsPanelRoot is not TranscriptPanel rows || Vm is not { } vm) return;
        var index = vm.Rows.IndexOf(match.Row);
        if (index < 0) return;
        var query = TranscriptFind.Normalize(vm.FindQuery);
        scroll.Reveal(rows, index, Transcript.ContainerFromIndex, container => SelectMatch(container, match, query));
        FollowTranscript();
    }

    private void ClearFindSelection()
    {
        _findSelection?.ClearSelection();
        _findSelection = null;
    }

    /// <summary>
    /// Selects the match among the row's shown text: the same occurrence of the query as in the row's text, counted
    /// through its text blocks (the last one shown when the row shows fewer, as Markdown drops its marks). Returns where
    /// it is in the row, or null when the row shows none of it (the whole row is brought into view).
    /// </summary>
    private Rect? SelectMatch(Control container, FindMatch match, string query)
    {
        ClearFindSelection();
        var found = new List<(SelectableTextBlock Block, int Start)>();
        foreach (var block in container.GetVisualDescendants().OfType<SelectableTextBlock>())
        {
            if (!block.IsEffectivelyVisible) continue;
            var text = block.Inlines is { Count: > 0 } inlines ? InlineText(inlines) : block.Text ?? "";
            // Offsets in a text that normalization made shorter would select the wrong characters: such a text is
            // searched as it is (its decomposed letters then only match themselves)
            var normalized = TranscriptFind.Nfc(text);
            var searched = normalized.Length == text.Length ? normalized : text;
            foreach (var start in TranscriptFind.Occurrences(searched, query)) found.Add((block, start));
        }
        if (found.Count == 0) return null;
        var (hit, at) = found[Math.Min(match.Occurrence, found.Count - 1)];
        hit.SelectionStart = at;
        hit.SelectionEnd = at + query.Length;
        _findSelection = hit;
        var caret = hit.TextLayout.HitTestTextPosition(at);
        if (hit.TranslatePoint(new Point(caret.X + hit.Padding.Left, caret.Y + hit.Padding.Top), container) is not { } p) return null;
        return new Rect(p, new Size(Math.Max(1, caret.Width), Math.Max(1, caret.Height)));
    }

    /// <summary>The text of a block's inlines as its text positions count it: a run its text, a line break or an embedded
    /// control one character (MarkdownView's paragraphs).</summary>
    private static string InlineText(InlineCollection inlines)
    {
        var sb = new System.Text.StringBuilder();
        Append(inlines, sb);
        return sb.ToString();

        static void Append(InlineCollection inlines, System.Text.StringBuilder sb)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run r:
                        sb.Append(r.Text);
                        break;
                    case Span s:
                        Append(s.Inlines, sb);
                        break;
                    default:
                        sb.Append('\n');
                        break;
                }
            }
        }
    }
}
