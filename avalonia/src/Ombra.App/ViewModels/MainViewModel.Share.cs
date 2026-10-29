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
            var refused = Uri.TryCreate(url, UriKind.Absolute, out var link) ? Browser.OpenUserTab(link) : url;
            if (refused is not null) Notify("IconAlert", Loc.T("Couldn't open the link"), refused);
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
    /// Lines of a file from the editor ("Share with team…"): whole lines <paramref name="firstLine"/>–<paramref name="lastLine"/>
    /// of <paramref name="text"/>, with the path relative to the project (its name only outside it), the project, branch and commit.
    /// </summary>
    public void ShareSnippet(string fullPath, int firstLine, int lastLine, string text, string? language)
    {
        var project = ProjectFolder;
        // Both resolved: the editor's document and the project may name the same folder differently (/var vs /private/var)
        var rel = project is { } root && ProjectFiles.Relative(Ombra.ClientCore.SessionCatalog.ResolveLinks(root), Ombra.ClientCore.SessionCatalog.ResolveLinks(fullPath)) is { Length: > 0 } r
            ? r.Replace('\\', '/') : Path.GetFileName(fullPath);
        var snippet = new SharedSnippet(rel, firstLine, lastLine, language, text,
            project is { } p ? Path.GetFileName(Path.TrimEndingDirectorySeparator(p)) : null);
        var lines = lastLine > firstLine ? Loc.F("lines {0}-{1}", firstLine, lastLine) : Loc.F("line {0}", firstLine);
        Share.Begin(TeamShare.SnippetKind, TeamShare.SnippetName(snippet), Loc.F("{0} · {1}", rel, lines), () =>
        {
            // Where it is from, read when it is sent (git may be slow or missing: then it goes without)
            if (project is not null && GitProbe.ReadHeadAsync(RunTool, project, Lifetime).GetAwaiter().GetResult() is (GitProbeState.Ok, { } head, _))
                snippet = snippet with { Branch = head.Branch, Commit = head.ShortCommit };
            return TeamShare.Snippet(snippet);
        }, fullPath);
    }

    /// <summary>An assistant reply (its menu's "Send to colleague…"): its Markdown as a note titled with the chat.</summary>
    [RelayCommand]
    public void ShareReply(AssistantRowViewModel? reply)
    {
        if (reply is not { HasText: true }) return;
        var note = new SharedNote(SessionTitle, reply.Text, TeamShare.NoteFromReply, SessionTitle);
        Share.Begin(TeamShare.NoteKind, SessionTitle, Excerpt(reply.Text), () => TeamShare.Note(note));
    }

    /// <summary>A comment I sent with a message ("Send to team…" on its row): its note, place and details, and the page.</summary>
    [RelayCommand]
    public void ShareSentComment(SentCommentViewModel? comment)
    {
        if (comment?.Model is not { } c) return;
        var place = c.Url ?? (c.Path is { } path ? ShownPath(path) + (c.Line is { } l ? $":{l}" : "") : c.Where);
        var text = (c.Note.Length > 0 ? c.Note : Loc.T("(no note)")) + "\n\n" + Loc.F("On {0}", comment.Where)
            + (c.Details.Count > 0 ? "\n\n" + (c.Url is null ? TeamShareViewModel.Fenced(string.Join('\n', c.Details), null) : string.Join('\n', c.Details.Select(d => "- " + d))) : "");
        var title = c.Note.Length > 0 ? c.Note : comment.Where;
        var note = new SharedNote(title, text, TeamShare.NoteFromComment, SessionTitle, c.Url);
        Share.Begin(TeamShare.NoteKind, title, place, () => TeamShare.Note(note));
    }

    /// <summary>The start of a text for the Share dialog's detail line.</summary>
    private static string Excerpt(string text)
    {
        var plain = Markdig.Markdown.ToPlainText(text).ReplaceLineEndings(" ").Trim();
        return plain.Length > 160 ? plain[..160] + "…" : plain;
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
