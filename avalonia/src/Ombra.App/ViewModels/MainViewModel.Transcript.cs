using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>The conversation's own actions: starter prompts on a new session, and the actions on a sent message.</summary>
public partial class MainViewModel
{
    /// <summary>A new session's starter prompts (Claude Code's suggestions): one fills the message box to edit and send.</summary>
    public IReadOnlyList<string> StarterPrompts { get; } =
    [
        Loc.T("Explain how this project is organized"),
        Loc.T("Find a bug and fix it"),
        Loc.T("Add tests for the code I changed last"),
    ];

    [RelayCommand]
    private void UseStarter(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        ComposerText = prompt;
        CaretToEndRequested?.Invoke();
        FocusComposerRequested?.Invoke();
    }

    /// <summary>Copies one of your messages as you wrote it.</summary>
    [RelayCommand]
    private Task CopyUserMessage(UserRowViewModel? row) => row is { Text.Length: > 0 } ? CopyTextAsync(row.Text) : Task.CompletedTask;

    /// <summary>Insert a quote, code block or expanded paste without replacing an existing draft.</summary>
    internal void InsertMessageText(string text)
    {
        if (IsReadOnly || text.Length == 0) return;
        var at = Math.Clamp(ComposerCaretIndex, 0, ComposerText.Length);
        ComposerText = ComposerText.Insert(at, text);
        ComposerCaretIndex = at + text.Length;
        FocusComposerRequested?.Invoke();
    }

    /// <summary>
    /// Rewind to before one of your messages (omp's <c>navigate_tree</c>, as Rewind… in the session menu): this chat goes on
    /// in place from the conversation before it, and the message comes back into the box to change and send.
    /// The row is matched by text from the end: earlier same-text messages may not yet be loaded.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRewindTo))]
    private async Task RewindToAsync(UserRowViewModel? row)
    {
        if (row is null) return;
        // This chat's omp throughout: another chat may be shown by the time omp answers
        var open = _open;
        var session = open.Controller;
        var occurrence = RewindOccurrenceFromEnd(Rows, row);
        var card = new SessionCardViewModel("rewind", "IconRewind", Loc.T("Rewinding…"), "") { State = SessionCardState.Running };
        ShowCard(card);
        var messages = await session.GetBranchMessagesAsync(open.Lifetime.Token);
        var matches = messages.Where(m => SameMessageText(m.Text, row.Text)).ToList();
        if (occurrence < 0 || occurrence >= matches.Count)
        {
            card.Finish(false, Loc.T("Not rewound"), Loc.T("omp does not list this message as one to rewind to. Rewind… in the session menu shows the ones it can."),
                secondary: new SessionCardAction(Loc.T("Dismiss"), new RelayCommand(CloseSessionCard)));
            return;
        }
        var restored = await session.RewindAsync(matches[^(occurrence + 1)].EntryId, open.Lifetime.Token);
        ApplyIfShown(open);
        RequestCatalogRefresh();
        if (restored is null)
        {
            card.Finish(false, Loc.T("Not rewound"), Loc.T("omp did not rewind; the conversation shows why."),
                secondary: new SessionCardAction(Loc.T("Dismiss"), new RelayCommand(CloseSessionCard)));
            return;
        }
        PutInComposer(open, restored.Value.Text, restored.Value.Images);
        card.Finish(true, Loc.T("Rewound"), Loc.T("This chat goes on from before that message, and your message is back in the box. What came after stays in the session file as a branch (omp's /tree)."),
            secondary: new SessionCardAction(Loc.T("Dismiss"), new RelayCommand(CloseSessionCard)));

    }

    internal static int RewindOccurrenceFromEnd(IList<RowViewModel> rows, UserRowViewModel target)
    {
        var occurrence = 0;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(rows[i], target)) return occurrence;
            if (rows[i] is UserRowViewModel { Confirmed: true } user && SameMessageText(user.Text, target.Text)) occurrence++;
        }
        return -1;
    }

    private static bool SameMessageText(string a, string b) =>
        string.Equals(a.Trim().ReplaceLineEndings("\n"), b.Trim().ReplaceLineEndings("\n"), StringComparison.Ordinal);


    /// <summary>Text (and the message's images) back into a chat's message box: the box on screen, or the chat's kept
    /// draft when another chat is shown.</summary>
    private void PutInComposer(OpenSession open, string text, IReadOnlyList<ImageAttachment>? images = null)
    {
        if (open != _open)
        {
            open.ComposerText = text;
            foreach (var image in images ?? []) open.Attachments.Add(new AttachmentViewModel(image, this));
            return;
        }
        ComposerText = text;
        foreach (var image in images ?? []) TryAddImage(image, Loc.T("Rewound message"));
        CaretToEndRequested?.Invoke();
    }

    private bool CanRewindTo(UserRowViewModel? row) => row is { Confirmed: true } && CanActOnSession();
}
