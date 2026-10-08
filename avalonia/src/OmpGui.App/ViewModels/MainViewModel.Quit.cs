using CommunityToolkit.Mvvm.Input;

namespace OmpGui.App.ViewModels;

/// <summary>What closing the window now would end, in the words of the card that asks first.</summary>
public sealed record QuitWarning(string Title, string Message);

public sealed partial class MainViewModel
{
    /// <summary>
    /// What quitting would throw away: runs in any open chat (each omp gets stdin EOF, then its process tree is killed),
    /// requests omp waits on, or shells the user started (a dev server in a terminal tab). Null when nothing is lost, and
    /// once the window is already closing (the chats are gone; asking then would only keep the shells alive).
    /// </summary>
    internal QuitWarning? QuitWarning(int liveTerminals)
    {
        if (IsClosing) return null;
        var tabs = liveTerminals switch
        {
            0 => "",
            1 => "the shell in the terminal tab",
            _ => $"the shells in {liveTerminals} terminal tabs",
        };
        var working = WorkingSessions(); // MainViewModel.OpenSessions.cs
        var running = working.Count(w => w.Snapshot.Dialogs.Count == 0);
        var asking = working.Count - running;
        if (working.Count > 1)
        {
            var what = (running, asking) switch
            {
                (0, _) => $"cancels the requests omp waits on in {asking} chats",
                (_, 0) => $"stops the runs in {running} chats",
                _ => $"stops the runs in {Chats(running)} and cancels the requests omp waits on in {Chats(asking)}",
            };
            return new($"omp is working in {working.Count} chats", "Quitting " + what + (tabs.Length > 0 ? $", and ends {tabs}." : "."));
        }
        if (working.Count == 1)
        {
            var (open, s) = working[0];
            // In the background: which chat, since it is not on screen
            var where = open == _open ? "" : $" in “{ChatTitle(s)}”";
            if (s.Dialogs.Count == 0)
                return new("omp is still working" + where, "Quitting stops the current run" + (tabs.Length > 0 ? $" and ends {tabs}." : "."));
            return new("omp is waiting for your answer" + where, "Quitting cancels its request and stops omp" + (tabs.Length > 0 ? $", and ends {tabs}." : "."));
        }
        if (liveTerminals > 0)
            return new(liveTerminals == 1 ? "A terminal tab is still running" : $"{liveTerminals} terminal tabs are still running",
                $"Quitting ends {tabs} and whatever runs there.");
        return null;

        static string Chats(int n) => n == 1 ? "one chat" : $"{n} chats";
    }

    /// <summary>
    /// Asks on the session card (the in-window card every session action uses) before the window closes. Whatever
    /// covered the conversation (settings, the shortcut sheet, the image viewer) closes so the card is seen; "Keep
    /// working" puts back the card it replaced, unless that was a brief note that goes away by itself anyway.
    /// </summary>
    internal void AskBeforeQuit(QuitWarning warning, Action quit)
    {
        IsSettingsOpen = false;
        IsShortcutsOpen = false;
        if (IsImageViewerOpen) CloseImageViewerCommand.Execute(null);
        if (SessionCard is { Kind: "quit" } asking)
        {
            asking.Title = warning.Title;
            asking.Message = warning.Message;
            return;
        }
        var previous = SessionCard is { Kind: not "brief" } kept ? kept : null;
        var card = new SessionCardViewModel("quit", "IconAlert", warning.Title, warning.Message) { State = SessionCardState.Ask };
        var keep = new RelayCommand(() =>
        {
            if (!ReferenceEquals(SessionCard, card)) return;
            if (previous is not null) SessionCard = previous;
            else CloseSessionCard();
        });
        card.Primary = new SessionCardAction("Quit", new RelayCommand(quit));
        card.Secondary = new SessionCardAction("Keep working", keep);
        card.DismissCommand = keep;
        ShowCard(card);
    }
}
