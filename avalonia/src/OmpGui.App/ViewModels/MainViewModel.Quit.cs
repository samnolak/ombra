using CommunityToolkit.Mvvm.Input;
using OmpGui.App.Localization;

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
            1 => Loc.T("the shell in the terminal tab"),
            _ => Loc.F("the shells in {0} terminal tabs", liveTerminals),
        };
        var working = WorkingSessions(); // MainViewModel.OpenSessions.cs
        var running = working.Count(w => w.Snapshot.Dialogs.Count == 0);
        var asking = working.Count - running;
        if (working.Count > 1)
        {
            var what = (running, asking) switch
            {
                (0, _) => Loc.F("cancels the requests omp waits on in {0} chats", asking),
                (_, 0) => Loc.F("stops the runs in {0} chats", running),
                _ => Loc.F("stops the runs {0} and cancels the requests omp waits on {1}", Chats(running), Chats(asking)),
            };
            return new(Loc.F("omp is working in {0} chats", working.Count),
                tabs.Length > 0 ? Loc.F("Quitting {0}, and ends {1}.", what, tabs) : Loc.F("Quitting {0}.", what));
        }
        if (working.Count == 1)
        {
            var (open, s) = working[0];
            // In the background: which chat, since it is not on screen
            var title = s.Dialogs.Count == 0
                ? open == _open ? Loc.T("omp is still working") : Loc.F("omp is still working in “{0}”", ChatTitle(s))
                : open == _open ? Loc.T("omp is waiting for your answer") : Loc.F("omp is waiting for your answer in “{0}”", ChatTitle(s));
            if (s.Dialogs.Count == 0)
                return new(title, tabs.Length > 0 ? Loc.F("Quitting stops the current run and ends {0}.", tabs) : Loc.T("Quitting stops the current run."));
            return new(title, tabs.Length > 0 ? Loc.F("Quitting cancels its request and stops omp, and ends {0}.", tabs) : Loc.T("Quitting cancels its request and stops omp."));
        }
        if (liveTerminals > 0)
            return new(liveTerminals == 1 ? Loc.T("A terminal tab is still running") : Loc.F("{0} terminal tabs are still running", liveTerminals),
                Loc.F("Quitting ends {0} and whatever runs there.", tabs));
        return null;

        static string Chats(int n) => n == 1 ? Loc.T("in one chat") : Loc.F("in {0} chats", n);
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
        card.Primary = new SessionCardAction(Loc.T("Quit"), new RelayCommand(quit));
        card.Secondary = new SessionCardAction(Loc.T("Keep working"), keep);
        card.DismissCommand = keep;
        ShowCard(card);
    }
}
