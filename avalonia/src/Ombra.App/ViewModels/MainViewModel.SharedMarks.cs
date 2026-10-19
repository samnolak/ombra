using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore.Teams;

namespace Ombra.App.ViewModels;

/// <summary>
/// K4 shared marks (Views/Team/SharedMarksView, Views/Team/SendMarksButton): the message box's code and page marks go
/// "Send to…" everyone or members of the team (code marks with the project's repository and commit); a received mark
/// opens at its place (the file at its line in Files, the page at its pin in the browser pane), its page marks are pins
/// there, and «В запрос агенту» puts it in the message box for my agent.
/// </summary>
public partial class MainViewModel
{
    public SharedMarksViewModel TeamMarks => field ??= NewTeamMarks();

    private SharedMarksViewModel NewTeamMarks()
    {
        var marks = new SharedMarksViewModel(Team, ShowSharedMark) { Toast = ShowTeamToast, ToComposer = AddReceivedMark };
        marks.PagePinsChanged += pins => Preview.SetSharedPins(pins);
        _ = RefreshTeamRepoAsync(marks);
        return marks;
    }

    /// <summary>The project's repository (fingerprint and HEAD) for code marks; null outside git.</summary>
    private async Task<RepoIdentity?> RefreshTeamRepoAsync(SharedMarksViewModel marks)
    {
        RepoIdentity? repo = null;
        try { repo = ProjectRoot is { } root ? await RepoIdentity.OfAsync(root) : null; }
        catch (Exception) { /* not a repository, or git is missing: marks go without it */ }
        marks.LocalRepo = repo;
        return repo;
    }

    /// <summary>A received mark joins my message box (and the page's pins); the composer shows why when it does not fit.</summary>
    private bool AddReceivedMark(Ombra.ClientCore.LocalMark mark)
    {
        if (!Marks.Add(mark)) return false;
        IsTeamPageOpen = false;
        FocusComposerRequested?.Invoke();
        return true;
    }

    /// <summary>Goes to a team member's mark: the Team page and settings close; the file opens at the line, or the page at the pin.</summary>
    private void ShowSharedMark(SharedMark m)
    {
        if (IsSettingsOpen) CloseSettingsCommand.Execute(null);
        IsTeamPageOpen = false;
        IsBrowserPageOpen = false;
        if (m.Mark.Code is { } c)
        {
            Files.OpenLinkCommand.Execute(new Controls.FileLink(c.Path, c.Line));
            _ = WarnIfMovedAsync(m);
        }
        else Preview.RevealShared(m);
    }

    /// <summary>A code mark left on another commit: the line it names may have moved since.</summary>
    private async Task WarnIfMovedAsync(SharedMark m)
    {
        await RefreshTeamRepoAsync(TeamMarks);
        if (TeamMarks.MayHaveMoved(m) && m.Commit is { } commit)
            Notify("IconAlert", Loc.F("Left on commit {0}: the line may have moved.", commit[..Math.Min(7, commit.Length)]), m.Mark.Code!.LineText.Trim());
    }

    /// <summary>Sends the open code and page marks to the members chosen in "Send to…"; the sent ones leave the message box.</summary>
    [RelayCommand]
    private async Task SendMarksToTeam()
    {
        var open = Marks.Items.Where(m => !m.IsResolved && SharedMarks.CanShare(m.Model)).ToList();
        if (open.Any(m => m.IsCode)) await RefreshTeamRepoAsync(TeamMarks);
        var sent = await TeamMarks.SendAsync([.. open.Select(m => m.Model)]);
        if (sent == 0) { ComposerMessage = TeamMarks.Message; return; }
        foreach (var m in open) Marks.Remove(m);
        Notify("IconUsers", TeamMarks.Message, "");
    }
}
