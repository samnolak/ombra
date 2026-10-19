using Ombra.App.ViewModels;
using Ombra.ClientCore.Teams;

namespace Ombra.App.Nightly;

/// <summary>
/// The app's team account (<see cref="TeamViewModel"/>, its <see cref="ITeamContext"/> items) as <see cref="INightlyTeamSource"/>.
/// The team account opens (key store, replica) on the first request from Nightly, not at app start.
/// </summary>
internal sealed class TeamViewModelNightlySource(Func<TeamViewModel> team) : INightlyTeamSource
{
    private TeamViewModel? _team;
    private ITeamItems? _items;

    public event Action? Changed;

    public NightlyTeamState Current
    {
        get
        {
            var t = _team ?? Avalonia.Threading.Dispatcher.UIThread.Invoke(Attach);
            if (t.Session is not { } session) return NightlyTeamState.Unavailable(NightlyTeamShare.SignedOut);
            if (session.Last is not { } last || t.Items is not { } items) return NightlyTeamState.Unavailable(NightlyTeamShare.NotReady);
            if (last.Teams.Count == 0) return NightlyTeamState.Unavailable(NightlyTeamShare.NoTeam);
            return new NightlyTeamState(items, last.MemberId, last.Teams, null);
        }
    }

    private TeamViewModel Attach()
    {
        if (_team is not null) return _team;
        var t = team();
        ((ITeamContext)t).Changed += Rewire;
        t.SessionChanged += _ => Rewire();
        _team = t;
        Rewire();
        return t;
    }

    private void Rewire()
    {
        var items = _team?.Items;
        if (!ReferenceEquals(_items, items))
        {
            if (_items is not null) _items.ItemsChanged -= OnItems;
            _items = items;
            if (_items is not null) _items.ItemsChanged += OnItems;
        }
        Changed?.Invoke();
    }

    private void OnItems(string _) => Changed?.Invoke();
}
