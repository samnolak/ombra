using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.App.ViewModels;

/// <summary>
/// Workspace trust in the window (<see cref="ClientCore.WorkspaceTrust"/>). Every chat in a folder the user has not
/// trusted starts restricted; when omp reports that it left something of the folder's out (MCP servers, hooks,
/// extensions, skills, language servers…), the chat's session card names those items and offers Trust and restart (this chat
/// only, same conversation) or Keep restricted. The card is not modal: the chat works meanwhile, and a card that only
/// gave way to another comes back. Revoking restricts the folder's chats (a working one when its run ends) and asks
/// about its omp terminals; a chat omp moves to another folder restarts under that folder's trust.
/// </summary>
public sealed partial class MainViewModel
{
    private const int TrustNamesShown = 8;

    /// <summary>The app's trust store (null: nothing is restricted and nothing asked, as before trust existed).</summary>
    public WorkspaceTrust? WorkspaceTrust { get; init; }

    /// <summary>Each chat update: a start the window has not looked at yet shows, replaces or clears the chat's card;
    /// a restart that a revoke left for the end of a run happens once the chat is idle.</summary>
    private void CheckProjectTrust(OpenSession open)
    {
        if (WorkspaceTrust is not { } trust || IsClosing || !_opens.ContainsKey(open.Controller)) return;
        if (open.RestrictWhenIdle && SessionHost.IsIdle(open.Controller.Snapshot()))
        {
            open.RestrictWhenIdle = false;
            _ = RestartUnderTrustAsync(open);
            return;
        }
        if (open.Controller.ProjectLaunch is not { } launch || ReferenceEquals(launch, open.TrustLaunch)) return;
        if (launch.LeftOut is not { } leftOut) return; // omp has not answered for this start yet
        open.TrustLaunch = launch;
        var trusting = open.TrustCard is { IsRunning: true };
        if (open.TrustCard is { } old)
        {
            open.TrustCard = null;
            RemoveCard(open, old);
        }
        if (launch.Restricted && leftOut.Count > 0) _ = AskTrustAsync(open, launch, leftOut, trust);
        else if (trusting && !launch.Restricted && open == _open)
            ShowBrief("IconShield", Loc.T("Folder trusted"), Loc.T("omp restarted this chat with the folder's own tools."));
    }

    private async Task AskTrustAsync(OpenSession open, ProjectLaunch launch, IReadOnlyList<OmpProjectItem> leftOut, WorkspaceTrust trust)
    {
        ProjectTrustCheck check;
        ProjectTrustSnapshot shown;
        // The folder as the card shows it: Trust and restart trusts this, not whatever the folder holds by the click
        try
        {
            (check, shown) = await Task.Run(() => (trust.Check(launch.Folder),
                ClientCore.WorkspaceTrust.Snapshot(launch.Folder, leftOut.Select(i => i.Source).Where(s => s.Length > 0))), open.Lifetime.Token);
        }
        catch (OperationCanceledException) { return; }
        if (IsClosing || !ReferenceEquals(open.TrustLaunch, launch)) return;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(launch.Folder)) is { Length: > 0 } n ? n : launch.Folder;
        var changed = check.Status == ProjectTrustStatus.Changed;
        var card = new SessionCardViewModel("trust", "IconShield",
            changed ? Loc.T("This folder's tools changed") : Loc.T("Do you trust this folder?"),
            changed
                ? Loc.F("What “{0}” would run on this computer has changed since you trusted it. Until you trust it again, omp works here without the folder's own tools; yours still load.", name)
                : Loc.F("“{0}” has its own tools that omp would run on this computer. Until you trust it, omp works here without them; your own tools and settings still load.", name))
        {
            State = SessionCardState.Ask,
            Detail = DescribeLeftOut(leftOut),
        };
        card.Primary = new SessionCardAction(Loc.T("Trust and restart"), new AsyncRelayCommand(() => TrustAndRestartAsync(open, shown, card)));
        var keep = new RelayCommand(() => KeepRestricted(open, card));
        card.Secondary = new SessionCardAction(Loc.T("Keep restricted"), keep);
        card.DismissCommand = keep;
        open.TrustCard = card;
        if (open != _open) open.Card ??= card;
        else if (SessionCard is null or { Kind: "brief" }) ShowCard(card);
        // Another card is up: this one comes back when it closes (RestoreTrustCard)
    }

    /// <summary>What omp left out, a line per kind: "MCP servers: github, postgres".</summary>
    internal static string DescribeLeftOut(IReadOnlyList<OmpProjectItem> items) =>
        string.Join("\n", items.GroupBy(i => i.Kind).OrderBy(g => KindOrder(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var names = g.Select(i => i.Name).Distinct().ToList();
            var shown = string.Join(", ", names.Take(TrustNamesShown));
            if (names.Count > TrustNamesShown) shown += " " + Loc.F("and {0} more", names.Count - TrustNamesShown);
            return KindLabel(g.Key) + ": " + shown;
        }));

    private static readonly string[] Kinds = ["mcp", "hook", "extension", "command", "skill", "agent", "tool", "plugin", "lsp", "settings"];

    private static int KindOrder(string kind) => Array.IndexOf(Kinds, kind) is var i and >= 0 ? i : Kinds.Length;

    private static string KindLabel(string kind) => kind switch
    {
        "mcp" => Loc.T("MCP servers"),
        "hook" => Loc.T("Hooks"),
        "extension" => Loc.T("Extensions"),
        "command" => Loc.T("Commands"),
        "skill" => Loc.T("Skills"),
        "agent" => Loc.T("Subagents"),
        "tool" => Loc.T("Custom tools"),
        "plugin" => Loc.T("Plugins"),
        "lsp" => Loc.T("Language servers"),
        "settings" => Loc.T("Project settings"),
        _ => kind,
    };

    private async Task TrustAndRestartAsync(OpenSession open, ProjectTrustSnapshot shown, SessionCardViewModel card)
    {
        if (WorkspaceTrust is not { } trust) return;
        bool trusted;
        try { trusted = trust.Trust(shown); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            card.Message = Loc.F("The decision could not be saved: {0}", e.Message);
            return;
        }
        if (trusted) await RestartTrustedAsync(open, card);
        else await RecheckChangedFolderAsync(open, card);
    }

    /// <summary>The folder's configuration changed after the card was shown, so nothing was trusted: omp restarts the
    /// chat restricted and reports the folder as it is now, on a new card (CheckProjectTrust).</summary>
    private async Task RecheckChangedFolderAsync(OpenSession open, SessionCardViewModel card)
    {
        var before = open.Controller.ProjectLaunch;
        card.State = SessionCardState.Running;
        card.Title = Loc.T("The folder changed, checking it again…");
        card.Primary = card.Secondary = null;
        try { await open.Controller.ReloadProjectTrustAsync(open.Lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (!ReferenceEquals(open.TrustCard, card) || !ReferenceEquals(open.Controller.ProjectLaunch, before)) return;
        card.Finish(false, Loc.T("Not trusted: the folder changed"),
            Loc.T("Its tools changed after this card was shown, so the folder was not trusted. Stop the current run, then check again to see what it would run now."),
            primary: new SessionCardAction(Loc.T("Check again"), new AsyncRelayCommand(() => RecheckChangedFolderAsync(open, card))),
            secondary: new SessionCardAction(Loc.T("Later"), new RelayCommand(() => KeepRestricted(open, card))));
    }

    private async Task RestartTrustedAsync(OpenSession open, SessionCardViewModel card)
    {
        var before = open.Controller.ProjectLaunch;
        card.State = SessionCardState.Running;
        card.Title = Loc.T("Restarting with the folder's tools…");
        card.Primary = card.Secondary = null;
        try { await open.Controller.ReloadProjectTrustAsync(open.Lifetime.Token); }
        catch (OperationCanceledException) { return; }
        // A new start is taken up by CheckProjectTrust; the same one means omp did not restart (a run was going, or it failed)
        if (!ReferenceEquals(open.TrustCard, card) || !ReferenceEquals(open.Controller.ProjectLaunch, before)) return;
        card.Finish(false, Loc.T("Trusted, not restarted yet"),
            Loc.T("The folder is trusted. Stop the current run, then restart: until then omp works here without the folder's tools."),
            primary: new SessionCardAction(Loc.T("Restart now"), new AsyncRelayCommand(() => RestartTrustedAsync(open, card))),
            secondary: new SessionCardAction(Loc.T("Later"), new RelayCommand(() => KeepRestricted(open, card))));
    }

    private void KeepRestricted(OpenSession open, SessionCardViewModel card)
    {
        if (ReferenceEquals(open.TrustCard, card)) open.TrustCard = null;
        RemoveCard(open, card);
    }

    /// <summary>Takes the card away wherever it is: on screen, or kept with its chat in the background.</summary>
    private void RemoveCard(OpenSession open, SessionCardViewModel card)
    {
        if (open == _open && ReferenceEquals(SessionCard, card)) CloseSessionCard();
        else if (ReferenceEquals(open.Card, card)) open.Card = null;
    }

    /// <summary>The shown chat's unanswered trust card again, once the card that replaced it has closed.</summary>
    private void RestoreTrustCard()
    {
        if (_open.TrustCard is { IsAsking: true } card && !ReferenceEquals(SessionCard, card)) SessionCard = card;
    }

    private async Task RestartUnderTrustAsync(OpenSession open)
    {
        try { await open.Controller.ReloadProjectTrustAsync(open.Lifetime.Token); }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// omp moved the chat to <paramref name="folder"/> (<c>/move</c>, a worktree): the trust it started under was the
    /// old folder's, so it restarts under the new folder's (restricted, with its card, unless that one is trusted).
    /// </summary>
    internal async Task FollowTrustAfterMoveAsync(OpenSession open, string folder)
    {
        if (WorkspaceTrust is null || open.Controller.ProjectLaunch is not { } launch || SessionCatalog.SamePath(launch.Folder, folder)) return;
        if (SessionHost.IsIdle(open.Controller.Snapshot())) await RestartUnderTrustAsync(open);
        else open.RestrictWhenIdle = true;
    }

    /// <summary>
    /// After a folder's trust was revoked: its chats that run with the folder's tools restart without them — idle
    /// ones now, working ones when their run ends — and its omp terminals are offered to close. Returns how many
    /// chats are working.
    /// </summary>
    internal async Task<int> RestrictFolderChatsAsync(string folder)
    {
        if (WorkspaceTrust is not { } trust || !trust.Check(folder).Restricted) return 0;
        var busy = 0;
        foreach (var open in _opens.Values.ToList())
        {
            if (open.Controller.ProjectLaunch is not { Restricted: false } launch || !SessionCatalog.SamePath(launch.Folder, folder)) continue;
            if (!SessionHost.IsIdle(open.Controller.Snapshot()))
            {
                busy++;
                open.RestrictWhenIdle = true;
                continue;
            }
            await RestartUnderTrustAsync(open);
        }
        OfferToCloseTrustedTerminals(folder);
        return busy;
    }

    /// <summary>omp terminals started in the folder while it was trusted keep its tools: a card offers to close them.</summary>
    private void OfferToCloseTrustedTerminals(string folder)
    {
        var tuis = Terminals.Where(t => t is { Kind: TerminalKind.OmpTui, HasExited: false } && t.WorkingDirectory is { } dir
            && SessionCatalog.SamePath(dir, folder) && !t.Args.Contains(ClientCore.WorkspaceTrust.UntrustedProjectFlag)).ToList();
        if (tuis.Count == 0) return;
        foreach (var t in tuis) t.Status = Loc.T("still runs with the folder's tools");
        var card = new SessionCardViewModel("trust-terminal", "IconTerminal", Loc.T("omp's terminal still has the folder's tools"),
            Loc.F("{0} omp terminal(s) in this folder started while it was trusted and keep its tools until closed. Open omp's terminal again to start it restricted.", tuis.Count));
        card.Primary = new SessionCardAction(Loc.T("Close them"), new RelayCommand(() =>
        {
            foreach (var t in tuis) CloseTerminal(t);
            CloseSessionCard();
        }));
        card.Secondary = new SessionCardAction(Loc.T("Keep them"), new RelayCommand(CloseSessionCard));
        ShowCard(card);
    }
}
