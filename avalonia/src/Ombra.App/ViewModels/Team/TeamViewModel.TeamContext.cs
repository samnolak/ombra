using Ombra.ClientCore.Teams;

namespace Ombra.App.ViewModels;

/// <summary>The team as shared features see it (<see cref="ITeamContext"/>): the shown team, me, its members and its items.</summary>
public sealed partial class TeamViewModel : ITeamContext
{
    public ITeamItems? Items => Session?.Items;
}
