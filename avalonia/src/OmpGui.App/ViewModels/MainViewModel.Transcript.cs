using CommunityToolkit.Mvvm.Input;

namespace OmpGui.App.ViewModels;

/// <summary>The conversation's own actions: starter prompts on a new session, and the actions on a sent message.</summary>
public partial class MainViewModel
{
    /// <summary>A new session's starter prompts (Claude Code's suggestions): one fills the message box to edit and send.</summary>
    public IReadOnlyList<string> StarterPrompts { get; } =
    [
        "Explain how this project is organized",
        "Find a bug and fix it",
        "Add tests for the code I changed last",
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
    private void CopyUserMessage(UserRowViewModel? row)
    {
        if (row is { Text.Length: > 0 }) CopyTextRequested?.Invoke(row.Text);
    }

    /// <summary>
    /// Rewind to before one of your messages (omp's <c>branch</c>, as Rewind… in the session menu): a new session goes on
    /// from the conversation before it, and the message comes back into the box to change and send.
    /// The row is matched to omp's list of messages by its text and, among equal texts, by its place.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRewindTo))]
    private async Task RewindToAsync(UserRowViewModel? row)
    {
        if (row is null) return;
        // This chat's omp throughout: another chat may be shown by the time omp answers
        var open = _open;
        var session = open.Controller;
        var occurrence = Rows.OfType<UserRowViewModel>().TakeWhile(r => r != row).Count(r => Same(r.Text, row.Text));
        var card = new SessionCardViewModel("rewind", "IconRewind", "Rewinding…", "") { State = SessionCardState.Running };
        ShowCard(card);
        var messages = await session.GetBranchMessagesAsync(open.Lifetime.Token);
        var matches = messages.Where(m => Same(m.Text, row.Text)).ToList();
        if (occurrence >= matches.Count)
        {
            card.Finish(false, "Not rewound", "omp does not list this message as one to rewind to. Rewind… in the session menu shows the ones it can.",
                secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
            return;
        }
        var text = await session.RewindAsync(matches[occurrence].EntryId, open.Lifetime.Token);
        ApplyIfShown(open);
        RequestCatalogRefresh();
        if (text is null)
        {
            card.Finish(false, "Not rewound", "omp did not start the new session; the conversation shows why.",
                secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
            return;
        }
        PutInComposer(open, text);
        card.Finish(true, "Rewound", "A new session goes on from before that message; the earlier one is kept in the sessions list. " +
            "Your message is back in the box.", secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));

        static bool Same(string a, string b) => string.Equals(a.Trim().ReplaceLineEndings("\n"), b.Trim().ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    /// <summary>Text back into a chat's message box: the box on screen, or the chat's kept draft when another chat is shown.</summary>
    private void PutInComposer(OpenSession open, string text)
    {
        if (open != _open)
        {
            open.ComposerText = text;
            return;
        }
        ComposerText = text;
        CaretToEndRequested?.Invoke();
    }

    private bool CanRewindTo(UserRowViewModel? row) => row is { Confirmed: true } && CanActOnSession();
}
