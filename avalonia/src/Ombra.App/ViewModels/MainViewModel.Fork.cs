using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// Fork (omp's RPC <c>fork</c>): the chat goes on in a new session file and the original stays in the sessions list as
/// it was. Fork in the session menu copies the whole conversation; Fork from here on a reply keeps it up to that reply.
/// </summary>
public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(CanActOnSession))]
    private Task ForkSessionAsync() => ForkAsync(null);

    [RelayCommand(CanExecute = nameof(CanForkFrom))]
    private Task ForkFromAsync(AssistantRowViewModel? row) => row is null ? Task.CompletedTask : ForkAsync(row.Key);

    private bool CanForkFrom(AssistantRowViewModel? row) => row is { Streaming: false } && CanActOnSession();

    private async Task ForkAsync(long? rowKey)
    {
        // This chat's omp throughout: another chat may be shown by the time omp answers
        var open = _open;
        var card = new SessionCardViewModel("fork", "IconBranch", Loc.T("Forking…"), "") { State = SessionCardState.Running };
        ShowCard(card);
        var outcome = await open.Controller.ForkAsync(rowKey, open.Lifetime.Token);
        ApplyIfShown(open);
        RequestCatalogRefresh();
        var dismiss = new SessionCardAction(Loc.T("Dismiss"), new RelayCommand(CloseSessionCard));
        switch (outcome)
        {
            case ForkOutcome.Forked:
                card.Finish(true, Loc.T("Forked"), rowKey is null
                    ? Loc.T("This chat goes on in a copy of the session; the original is kept in the sessions list.")
                    : Loc.T("This chat goes on in a new session that ends at that reply; the original is kept in the sessions list."),
                    secondary: dismiss);
                break;
            case ForkOutcome.Busy:
                card.Finish(false, Loc.T("Not forked"), Loc.T("omp is still working (a reply, a command or a background task). Fork once it is idle."), secondary: dismiss);
                break;
            case ForkOutcome.NotFound:
                card.Finish(false, Loc.T("Not forked"), Loc.T("omp does not have this reply in the current session (it may belong to an earlier branch)."), secondary: dismiss);
                break;
            case ForkOutcome.Cancelled:
                card.Finish(false, Loc.T("Not forked"), Loc.T("An extension cancelled the fork."), secondary: dismiss);
                break;
            default:
                card.Finish(false, Loc.T("Not forked"), Loc.T("omp did not start the new session; the conversation shows why."), secondary: dismiss);
                break;
        }
    }
}
