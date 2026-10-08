using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using OmpGui.App.Controls;

namespace OmpGui.App.Views;

/// <summary>The conversation's scrolling (<see cref="ChatScrollController"/> owns the position) and the cards that follow its last row.</summary>
public sealed partial class MainWindow
{
    private ScrollViewer? _scroll;
    private ChatScrollController? _chatScroll;

    /// <summary>Hands the transcript's scrolling to its controller once the list's template is in place (OnLoaded).</summary>
    private void WireTranscriptScroll()
    {
        _scroll = Transcript.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scroll is null) return;
        var rows = Transcript.ItemsPanelRoot as TranscriptPanel;
        _chatScroll = new ChatScrollController(_scroll, Transcript, rows);
        _chatScroll.FollowingChanged += () => JumpToLatest.IsVisible = !_chatScroll.IsFollowing;
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
