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
        var occurrence = Rows.OfType<UserRowViewModel>().TakeWhile(r => r != row).Count(r => Same(r.Text, row.Text));
        var card = new SessionCardViewModel("rewind", "IconRewind", "Rewinding…", "") { State = SessionCardState.Running };
        ShowCard(card);
        var messages = await _session.GetBranchMessagesAsync(_cts.Token);
        var matches = messages.Where(m => Same(m.Text, row.Text)).ToList();
        if (occurrence >= matches.Count)
        {
            card.Finish(false, "Not rewound", "omp does not list this message as one to rewind to. Rewind… in the session menu shows the ones it can.",
                secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
            return;
        }
        var text = await _session.RewindAsync(matches[occurrence].EntryId, _cts.Token);
        AfterSessionChange();
        if (text is null)
        {
            card.Finish(false, "Not rewound", "omp did not start the new session; the conversation shows why.",
                secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
            return;
        }
        ComposerText = text;
        CaretToEndRequested?.Invoke();
        card.Finish(true, "Rewound", "A new session goes on from before that message; the earlier one is kept in the sessions list. " +
            "Your message is back in the box.", secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));

        static bool Same(string a, string b) => string.Equals(a.Trim().ReplaceLineEndings("\n"), b.Trim().ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    private bool CanRewindTo(UserRowViewModel? row) => row is { Confirmed: true } && CanActOnSession();
}
