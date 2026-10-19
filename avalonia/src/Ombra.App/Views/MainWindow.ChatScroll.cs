using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Ombra.App.Controls;
using Ombra.App.ViewModels;

namespace Ombra.App.Views;

/// <summary>The conversation's scrolling (<see cref="ChatScrollController"/> owns the position) and the cards that follow its last row.</summary>
public sealed partial class MainWindow
{
    private ScrollViewer? _scroll;
    private ChatScrollController? _chatScroll;
    private DialogViewModel? _cardsDialog;
    private Control? _dialogAnswer;
    private Rect _dialogAnswerBounds;
    private Size _dialogViewport;
    private Size _dialogExtent;

    /// <summary>
    /// Reveal the pending question after layout, including later composer fits and window resizes.
    /// Approval's primary answers precede optional rule/feedback rows: the card's end is not its answer.
    /// Only geometry changes reveal it again; scrolling the cards by hand does not.
    /// </summary>
    private void KeepDialogAnswerInView()
    {
        var dialog = Vm?.CurrentDialog;
        var changed = !ReferenceEquals(dialog, _cardsDialog);
        if (changed)
        {
            _cardsDialog = dialog;
            _dialogAnswer = null;
            _dialogViewport = default;
            _dialogExtent = default;
            _dialogAnswerBounds = default;
        }
        if (dialog is null || !DialogHost.IsEffectivelyVisible || dialog.IsWritingFeedback) return;
        if (CardsScroll.Viewport.Height <= 0) return;

        var answerBounds = default(Rect);
        if (dialog.IsApproval)
        {
            _dialogAnswer ??= DialogHost.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => button.Name == "AllowButton");
            if (_dialogAnswer is not { IsEffectivelyVisible: true } answer || answer.Bounds.Height <= 0
                || answer.TranslatePoint(default, CardsStack) is not { } position) return;
            // Coordinates in the content do not change when the reader scrolls.
            answerBounds = new Rect(position, answer.Bounds.Size);
        }
        if (!changed && _dialogViewport == CardsScroll.Viewport && _dialogExtent == CardsScroll.Extent
            && _dialogAnswerBounds == answerBounds) return;
        _dialogViewport = CardsScroll.Viewport;
        _dialogExtent = CardsScroll.Extent;
        _dialogAnswerBounds = answerBounds;
        if (_dialogAnswer is { } target) target.BringIntoView();
        else CardsScroll.ScrollToEnd();
    }

    /// <summary>Hands the transcript's scrolling to its controller once the list's template is in place (OnLoaded).</summary>
    private void WireTranscriptScroll()
    {
        _scroll = Transcript.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scroll is null) return;
        var rows = Transcript.ItemsPanelRoot as TranscriptPanel;
        _chatScroll = new ChatScrollController(_scroll, Transcript, rows);
        if (Vm is { } jumpModel)
        {
            var jump = new JumpToLatestController(JumpToLatest, _chatScroll, jumpModel);
            Closed += (_, _) => jump.Dispose();
        }
        _chatScroll.OlderMessagesRequested += () =>
        {
            if (Vm is { } vm && vm.LoadOlderMessagesCommand.CanExecute(null)) vm.LoadOlderMessagesCommand.Execute(null);
        };
        if (Vm is { } model) model.HistoryReadingRequested += _chatScroll.ReadHistory;
        // The cards under the conversation follow its height and the view's
        _scroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ExtentProperty || e.Property == ScrollViewer.ViewportProperty) FollowTranscript();
        };
        if (rows is not null) rows.ContentHeightChanged += (_, _) => FollowTranscript();
        _chatScroll.FollowLatest();
    }

    /// <summary>The user sent a message or opened another conversation, or asked for the latest message ("Jump to latest"):
    /// to the end, and following it again, whatever the reader was looking at.</summary>
    private void FollowLatest() => _chatScroll?.FollowLatest();

    /// <summary>Where the reader is in the conversation being left for another chat (kept with that chat).</summary>
    private object? SaveChatScroll() =>
        _chatScroll is { } scroll && Transcript.ItemsPanelRoot is TranscriptPanel rows ? scroll.Save(rows) : null;

    /// <summary>A chat shown again: its reader back where they were (on the latest message for one not shown before).</summary>
    private void RestoreChatScroll(object? anchor)
    {
        if (_chatScroll is not { } scroll || Transcript.ItemsPanelRoot is not TranscriptPanel rows) return;
        // An older page can supply the user/step header of a previously orphaned visible row.
        // Keep that row readable rather than restoring an offset inside a newly collapsed turn.
        if (anchor is ChatScrollAnchor { Item: RowViewModel { IsFolded: true } held } && Vm is { } vm)
        {
            if (held.Group is { } group) group.IsExpanded = true;
            for (var i = vm.Rows.IndexOf(held) - 1; i >= 0; i--)
            {
                if (vm.Rows[i] is TurnFoldRowViewModel fold) { fold.IsExpanded = true; break; }
                if (vm.Rows[i] is UserRowViewModel) break;
            }
        }
        scroll.Restore(anchor as ChatScrollAnchor, rows);
        FollowTranscript();
    }

    /// <summary>
    /// The cards under the conversation (an approval, a question, the activity line) follow it, as in Claude Code, instead
    /// of waiting at the bottom of the page: while the conversation is shorter than its view, they are drawn up to just
    /// under its last row (a new session's page keeps them where they are).
    /// </summary>
    private void FollowTranscript()
    {
        if (_scroll is null || Vm is not { } vm || Transcript.ItemsPanelRoot is not TranscriptPanel rows) return;
        // The rows' own height (the extent is never less than the view) and the list's padding around them
        var content = rows.ContentHeight + Math.Max(0, _scroll.Extent.Height - rows.Bounds.Height);
        var gap = vm.IsConversationEmpty ? 0 : Math.Floor(Math.Max(0, _scroll.Viewport.Height - content));
        if (Math.Abs(((CardsScroll.RenderTransform as TranslateTransform)?.Y ?? 0) + gap) < 0.5) return;
        CardsScroll.RenderTransform = gap > 0 ? new TranslateTransform(0, -gap) : null;
    }
}
