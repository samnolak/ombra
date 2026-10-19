using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.App.Services;
using Ombra.ClientCore.Teams;

namespace Ombra.App.ViewModels;

/// <summary>
/// P5 "Share" (Views/Team/ShareDialog, Views/Team/TeamShareSection): a link in the conversation, a file (Files), a skill
/// (Settings › Skills), the project's changes as a patch (Changes), the browser's open pages as a collection; the toast
/// when something arrives (also MarksShared's), and what an accepted resource opens.
/// </summary>
public partial class MainViewModel
{
    public TeamShareViewModel Share => field ??= NewShare();

    private TeamShareViewModel NewShare()
    {
        var share = new TeamShareViewModel(this);
        share.Attach(Team);
        return share;
    }

    /// <summary>
    /// A team toast: a brief card with Open; when the window is in the background an OS notification too, which names
    /// nothing (no sender, no title: it shows on the lock screen and stays in the system's history).
    /// </summary>
    internal void ShowTeamToast(string text, Action open)
    {
        var card = new SessionCardViewModel("brief", "IconUsers", text, "");
        card.Primary = new SessionCardAction(Loc.T("Open"), new RelayCommand(() =>
        {
            CloseSessionCard();
            open();
        }));
        // Shown at once: a member's share must not wait behind (or be dropped with) a short note like "Shared"
        ShowTimedCard(card, UndoCardDuration, urgent: true);
        if ((!_windowActive || _windowState == "Minimized") && NotificationsEnabled)
            AttentionRequested?.Invoke("Ombra", Loc.T("Something new from your team"), open);
    }

    /// <summary>The Team page with the Inbox; what it lists is seen now.</summary>
    internal void ShowTeamInbox()
    {
        OpenTeamPageCommand.Execute(null);
        _ = Share.MarkInboxSeenAsync();
    }

    /// <summary>
    /// The Browser page's Nightly window, while Nightly shows the page (MainWindow.BrowserWindow.cs): opens a link in a
    /// new tab of it. Null with the built-in preview.
    /// </summary>
    public Action<Uri>? OpenInBrowserWindow { get; set; }

    /// <summary>Accepted links: the Browser page, each in a tab of its own (the first in the empty tab when there is one).</summary>
    internal void OpenSharedLinks(IReadOnlyList<string> urls)
    {
        if (urls.Count == 0) return;
        OpenBrowserPageCommand.Execute(null);
        foreach (var url in urls)
        {
            if (OpenInBrowserWindow is { } open)
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") open(uri);
                else Notify("IconAlert", Loc.T("Couldn't open the link"), url);
                continue;
            }
            if (Browser.ActiveTab.HasContent) Browser.NewTabCommand.Execute(null);
            if (Browser.NavigateForAgent(url) is { } error) Notify("IconAlert", Loc.T("Couldn't open the link"), error);
        }
    }

    internal void ShowChangesPane() => OpenTool(SidePane.Changes);

    /// <summary>omp's user skills folder, where an accepted skill is installed (tests point it at a temporary folder).</summary>
    internal Func<Task<string>> UserSkillsFolderAsync { get => field ??= OmpUserSkillsFolderAsync; init; }

    /// <summary>omp's agent folder's skills; ~/.omp/agent/skills when omp does not say.</summary>
    private async Task<string> OmpUserSkillsFolderAsync()
    {
        var r = await RunOmpCliAsync(["config", "path"], TimeSpan.FromSeconds(30), Lifetime);
        var agent = r.Ok && r.Stdout.Trim() is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".omp", "agent");
        return Path.Combine(agent, "skills");
    }

    // ───────────── Share actions ─────────────

    /// <summary>A link in the conversation (its menu's "Share with team…").</summary>
    public void ShareLink(string url) =>
        Share.Begin(TeamItemKinds.Link, url, url, () => TeamShare.Link(url));

    /// <summary>A file of the project (Files' menu, a path's menu in the conversation).</summary>
    [RelayCommand]
    public void ShareFile(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        var size = new FileInfo(path).Length;
        Share.Begin(TeamItemKinds.File, Path.GetFileName(path), Loc.F("{0} · {1} KB", ShownPath(path), Math.Round(size / 1024.0, 1)),
            () => TeamShare.FileFrom(path), path);
    }

    /// <summary>A path for reading: relative to the project when inside it, else with the home folder as ~ (the full one goes in a tooltip).</summary>
    private string ShownPath(string path) =>
        ProjectFolder is { } root && ProjectFiles.Relative(root, path) is { Length: > 0 } rel ? rel : GitProbe.Tilde(path);

    /// <summary>A skill of Settings › Skills: its folder is found where omp looks for skills.</summary>
    [RelayCommand]
    private async Task ShareSkill(SkillRowViewModel? skill)
    {
        if (skill is null) return;
        var folder = await FindSkillFolderAsync(skill.Name);
        if (folder is null)
        {
            Notify("IconAlert", Loc.T("Couldn't find the skill's folder"), Loc.F("No SKILL.md for {0} in omp's skill folders.", skill.Name));
            return;
        }
        Share.Begin(TeamItemKinds.Skill, skill.Name, ShownPath(folder), () => TeamShare.SkillFrom(folder), folder);
    }

    /// <summary>The project's changes since the last commit, as a patch (Changes' "Share changes").</summary>
    [RelayCommand]
    private async Task ShareChanges()
    {
        if (ProjectFolder is not { } project) return;
        var read = await GitReview.ReadProjectPatchAsync(project, Lifetime);
        if (read is not { } p)
        {
            Notify("IconAlert", Loc.T("Nothing to share"), Loc.T("git shows no changes since the last commit."));
            return;
        }
        var title = Loc.F("Changes in {0}", Path.GetFileName(Path.TrimEndingDirectorySeparator(project)));
        var files = p.Patch.Split('\n').Count(l => l.StartsWith("diff --git ", StringComparison.Ordinal));
        Share.Begin(TeamItemKinds.Edit, title, Loc.Plural(files, "{0} file changed", "{0} files changed"), () => TeamShare.Edit(title, p.Patch, p.Repo));
    }

    /// <summary>The Browser page's open pages as one collection.</summary>
    [RelayCommand]
    private void ShareBrowserTabs()
    {
        var links = Browser.Tabs.Where(t => t.Url is { Scheme: "http" or "https" }).Select(t => new SharedLink(t.Url!.AbsoluteUri, t.Title.Length > 0 ? t.Title : null)).ToList();
        if (links.Count == 0)
        {
            Notify("IconAlert", Loc.T("Nothing to share"), Loc.T("No pages are open in the browser."));
            return;
        }
        var title = links[0].Title ?? links[0].Url;
        if (links.Count > 1) title = Loc.Plural(links.Count, "{0} page", "{0} pages");
        Share.Begin(TeamItemKinds.Collection, title, string.Join("\n", links.Select(l => l.Url)), () => TeamShare.Collection(title, links));
    }

    /// <summary>
    /// Where omp finds a skill named <paramref name="name"/>: its user folder, the project's .omp/.claude/.agents
    /// folders, the other agents' user folders and the custom ones; a folder of that name with SKILL.md, or one whose
    /// SKILL.md says <c>name: …</c>.
    /// </summary>
    internal async Task<string?> FindSkillFolderAsync(string name) => (await FindSkillFoldersAsync([name])).GetValueOrDefault(name);

    /// <summary>The folders of several skills at once (omp's folders are asked for and read once).</summary>
    internal async Task<IReadOnlyDictionary<string, string>> FindSkillFoldersAsync(IReadOnlyList<string> names)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string> { await UserSkillsFolderAsync() };
        if (ProjectFolder is { } p) roots.AddRange(new[] { ".omp", ".claude", ".agents", ".agent" }.Select(d => Path.Combine(p, d, "skills")));
        roots.AddRange(new[] { ".claude", ".agents", ".agent", ".codex" }.Select(d => Path.Combine(home, d, "skills")));
        roots.AddRange(Plugins.SkillDirectories.Select(d => d.StartsWith('~') ? home + d[1..] : d));
        return await Task.Run(() =>
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            var wanted = names.ToHashSet(StringComparer.Ordinal);
            foreach (var root in roots.Where(Directory.Exists))
            {
                foreach (var name in wanted.Where(n => !found.ContainsKey(n)).ToList())
                    if (File.Exists(Path.Combine(root, name, "SKILL.md"))) found[name] = Path.Combine(root, name);
                if (found.Count == wanted.Count) break;
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var md = Path.Combine(dir, "SKILL.md");
                    if (!File.Exists(md)) continue;
                    var declared = File.ReadLines(md).Take(20).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("name: ", StringComparison.Ordinal))?[6..].Trim();
                    if (declared is not null && wanted.Contains(declared)) found.TryAdd(declared, dir);
                }
            }
            return (IReadOnlyDictionary<string, string>)found;
        });
    }

    /// <summary>omp's plugins folder (beside its agent folder: …/agent and …/plugins), where it keeps marketplace catalogs.</summary>
    internal async Task<string?> OmpPluginsFolderAsync() =>
        Path.GetDirectoryName(Path.GetDirectoryName(await UserSkillsFolderAsync())) is { } root ? Path.Combine(root, "plugins") : null;
}
