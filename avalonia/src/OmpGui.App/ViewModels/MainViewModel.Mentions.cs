using Avalonia.Threading;
using OmpGui.App.Services;

namespace OmpGui.App.ViewModels;

/// <summary>
/// "@" in the message box (Claude Code, Codex, omp's own terminal UI): typing <c>@</c> and part of a name anywhere in
/// the message lists the project's files and folders in the command menu; Tab or Enter puts the <c>@path</c> reference
/// in place of what was typed, as the Files pane's "@" button writes it. A folder ends in "/" and lists what is in it
/// next (omp's terminal UI does the same). The list comes from the Files pane's index (git's file list), searched off
/// the UI thread after the same short pause as "Go to file", and a newer key cancels an older search.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Entries the "@" menu lists at most (it scrolls; a longer name narrows it).</summary>
    public const int MaxMentionResults = 50;

    /// <summary>An "@" reference being typed: where its "@" is, where its word ends, and what follows the "@" up to the caret.</summary>
    internal sealed record MentionToken(int Start, int End, string Query);

    /// <summary>The project's files and the folders above them ('/'-separated, relative), for the "@" menu.</summary>
    internal sealed record MentionIndex(List<string> Paths, HashSet<string> Folders);

    private MentionToken? _mention;
    private CancellationTokenSource? _mentionSession, _mentionSearch;
    private Task<MentionIndex>? _mentionIndex;
    private bool _mentionCheckPosted;

    /// <summary>The "@" whose menu Esc closed: typing on in the same word does not open it again.</summary>
    private int _dismissedMentionAt = -1;

    /// <summary>
    /// The "@word" that ends at <paramref name="caret"/>, or null. omp reads a reference only after a space or an opening
    /// bracket or quote (so an e-mail address is none), up to a space or the next "@"; a quoted one is not completed.
    /// </summary>
    internal static MentionToken? FindMention(string text, int caret)
    {
        if (caret < 1 || caret > text.Length) return null;
        var at = caret - 1;
        while (text[at] != '@')
        {
            if (char.IsWhiteSpace(text[at]) || --at < 0) return null;
        }
        if (at > 0 && !char.IsWhiteSpace(text[at - 1]) && "([{<\"'`".IndexOf(text[at - 1]) < 0) return null;
        var query = text[(at + 1)..caret];
        if (query.Length > 0 && query[0] is '"' or '\'') return null;
        var end = caret;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '@') end++;
        return new MentionToken(at, end, query);
    }

    /// <summary>The view: the user typed into the message box (only typing opens the "@" menu, not text put there).</summary>
    public void OnComposerTyped() => CheckMention(canStart: true);

    partial void OnComposerCaretIndexChanged(int value)
    {
        if (_mention is not null) CheckMention(canStart: false);
    }

    /// <summary>The text changed before the caret moved: look once both have.</summary>
    private void PostMentionCheck()
    {
        if (_mentionCheckPosted) return;
        _mentionCheckPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _mentionCheckPosted = false;
            CheckMention(canStart: false);
        });
    }

    private void CheckMention(bool canStart)
    {
        var token = FindMention(ComposerText, ComposerCaretIndex);
        if (token is null)
        {
            _dismissedMentionAt = -1;
            if (_mention is not null) EndMention();
            return;
        }
        if (_mention is { } current)
        {
            if (current == token) return;
            if (current.Start != token.Start && !canStart)
            {
                // The caret went to another word: this reference is done
                EndMention();
                return;
            }
        }
        else if (!canStart || token.Start == _dismissedMentionAt) return;
        if (_mention?.Start != token.Start) StartMentionSession();
        _mention = token;
        _mentionSearch?.Cancel();
        var cts = _mentionSearch = new CancellationTokenSource();
        _ = SearchMentionsAsync(token, cts.Token);
    }

    private void StartMentionSession()
    {
        _mentionSession?.Cancel();
        _mentionSession = new CancellationTokenSource();
        _mentionIndex = null;
        _dismissedMentionAt = -1;
    }

    /// <summary>The reference is finished or left: the menu goes back to what the text says (a slash command or nothing).</summary>
    private void EndMention()
    {
        _mention = null;
        _mentionSearch?.Cancel();
        _mentionSession?.Cancel();
        _mentionIndex = null;
        UpdateSlashSuggestions();
    }

    /// <summary>Esc while a reference is completed: true when there was one (the menu closes, nothing else happens).</summary>
    public bool DismissMention()
    {
        if (_mention is not { } m) return false;
        _dismissedMentionAt = m.Start;
        _mention = null;
        _mentionSearch?.Cancel();
        _mentionSession?.Cancel();
        _mentionIndex = null;
        IsCommandMenuOpen = false;
        return true;
    }

    private async Task SearchMentionsAsync(MentionToken token, CancellationToken ct)
    {
        try
        {
            await Task.Delay(FilesViewModel.SearchDelay, ct);
            if (ProjectFolder is not { } root)
            {
                ShowMentions([], "Open a project folder to mention its files");
                return;
            }
            var session = _mentionSession!.Token;
            var indexTask = _mentionIndex ??= LoadMentionIndexAsync(root, session);
            var index = await indexTask.WaitAsync(ct);
            var hits = await Task.Run(() => SearchMentions(index, token.Query, MaxMentionResults, ct), ct);
            if (ct.IsCancellationRequested || _mention != token) return;
            ShowMentions(hits, $"No files match “{token.Query}”");
        }
        catch (OperationCanceledException) { }
    }

    private async Task<MentionIndex> LoadMentionIndexAsync(string root, CancellationToken ct)
    {
        var files = await Files.ProjectIndexAsync(root, ct);
        return await Task.Run(() => BuildMentionIndex(files), ct);
    }

    internal static MentionIndex BuildMentionIndex(IReadOnlyList<string> files)
    {
        var folders = new HashSet<string>(StringComparer.Ordinal);
        var paths = new List<string>(files.Count + files.Count / 4);
        foreach (var f in files)
        {
            for (var slash = f.IndexOf('/'); slash > 0; slash = f.IndexOf('/', slash + 1))
                if (folders.Add(f[..slash])) paths.Add(f[..slash]);
            paths.Add(f);
        }
        return new MentionIndex(paths, folders);
    }

    /// <summary>
    /// The entries for <paramref name="query"/>: nothing typed or a folder ending in "/" lists that folder (folders first);
    /// anything else is the Files pane's name search over files and folders.
    /// </summary>
    internal static List<(string Path, bool IsFolder)> SearchMentions(MentionIndex index, string query, int max, CancellationToken ct)
    {
        var q = query.Replace('\\', '/');
        if (q.Length == 0 || q.EndsWith('/'))
        {
            var folder = q.TrimEnd('/');
            var prefix = folder.Length == 0 ? "" : folder + "/";
            return [.. index.Paths
                .Where(p => p.Length > prefix.Length && p.StartsWith(prefix, ProjectFiles.PathComparison) && p.IndexOf('/', prefix.Length) < 0)
                .Select(p => (p, index.Folders.Contains(p)))
                .OrderBy(x => !x.Item2).ThenBy(x => x.p, Comparer<string>.Create(ProjectFiles.NaturalCompare))
                .Take(max)];
        }
        return [.. ProjectFiles.Search(index.Paths, q, max, ct).Select(h => (h.RelativePath, index.Folders.Contains(h.RelativePath)))];
    }

    private void ShowMentions(List<(string Path, bool IsFolder)> hits, string empty)
    {
        CommandSuggestions.Clear();
        foreach (var (path, folder) in hits)
        {
            var slash = path.LastIndexOf('/');
            CommandSuggestions.Add(new CommandSuggestionViewModel(
                FilesViewModel.Mention(folder ? path + "/" : path),
                (slash < 0 ? path : path[(slash + 1)..]) + (folder ? "/" : ""),
                slash < 0 ? "" : path[..slash], null, "")
            {
                IsMention = true,
                IsFolder = folder,
            });
        }
        _selectedSuggestion = 0;
        if (CommandSuggestions.Count > 0) CommandSuggestions[0].IsSelected = true;
        NoMatchText = empty;
        ShowNoCommandMatch = CommandSuggestions.Count == 0;
        IsCommandMenuOpen = true;
    }

    /// <summary>
    /// Puts the chosen reference in place of the typed "@word", then a space (a file) or nothing (a folder, whose
    /// entries the menu lists next), with the caret after it.
    /// </summary>
    private void InsertMention(CommandSuggestionViewModel s)
    {
        if (_mention is not { } token) return;
        var text = ComposerText;
        var start = Math.Min(token.Start, text.Length);
        var end = Math.Min(token.End, text.Length);
        var into = s.IsFolder && s.Insert.EndsWith('/');
        var suffix = into || end < text.Length && char.IsWhiteSpace(text[end]) ? "" : " ";
        if (!into)
        {
            _mention = null;
            _mentionSearch?.Cancel();
            _mentionSession?.Cancel();
            _mentionIndex = null;
            IsCommandMenuOpen = false;
        }
        ComposerText = text[..start] + s.Insert + suffix + text[end..];
        ComposerCaretIndex = start + s.Insert.Length + suffix.Length;
        // Past an existing space the caret is after it too
        if (suffix.Length == 0 && !into) ComposerCaretIndex++;
        if (into) CheckMention(canStart: true);
    }
}

public sealed partial class FilesViewModel
{
    /// <summary>
    /// The project's file list for the message box's "@" menu: the pane's own index while the pane shows this project
    /// (its refreshes keep it current), else read now (git's list, or a walk), off the UI thread.
    /// </summary>
    internal async Task<IReadOnlyList<string>> ProjectIndexAsync(string root, CancellationToken ct)
    {
        var mine = IsActive && SamePath(Root, root);
        if (mine && _index is { } kept) return kept;
        var (paths, truncated) = await ProjectFiles.IndexAsync(root, useGit: true, ct);
        // Still the pane's project after the read: the pane's own search takes it too
        if (IsActive && SamePath(Root, root) && _index is null)
        {
            _index = paths;
            _indexTruncated = truncated;
        }
        return paths;
    }
}
