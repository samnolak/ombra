using System.Diagnostics;
using System.Text;
using OmpGui.ClientCore;

namespace OmpGui.App.Services;

/// <summary>One entry of a folder listing (a file on disk, or one git knows was deleted).</summary>
public sealed record FileEntry(string Name, string FullPath, bool IsDirectory, long Size = 0, bool IsDeleted = false);

/// <summary>A match of the project-wide name search: its path relative to the project, '/'-separated.</summary>
public sealed record FileHit(string RelativePath, int Score);

/// <summary>
/// A project folder's files for the Files pane, read one folder at a time (folders first, in natural order), with the
/// folders nobody browses (dependencies, build output) marked, and a name search over the whole project that is
/// bounded, cancellable and runs off the UI thread.
/// </summary>
public static class ProjectFiles
{
    /// <summary>Never shown: version-control internals and OS litter.</summary>
    public static readonly IReadOnlySet<string> Hidden = new HashSet<string>(StringComparer.Ordinal) { ".git", ".hg", ".svn", ".DS_Store", "Thumbs.db" };

    /// <summary>
    /// Dependencies, virtual environments, caches and build output: shown dimmed and closed (they open on demand), left
    /// out of the search when git cannot say what is ignored.
    /// </summary>
    public static readonly IReadOnlySet<string> Heavy = new HashSet<string>(StringComparer.Ordinal)
    {
        "node_modules", "bin", "obj", ".venv", "venv", "dist", "build", "target", "__pycache__", ".next", ".nuxt", ".svelte-kit",
        ".gradle", ".tox", ".mypy_cache", ".pytest_cache", ".ruff_cache", ".cache", ".turbo", ".parcel-cache", "coverage",
        "Pods", ".terraform", ".dart_tool", ".angular", ".idea", ".vs",
    };

    /// <summary>Path comparison as the platform's file system does it (case-insensitive on Windows and macOS).</summary>
    public static readonly StringComparer PathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The largest search index: beyond it the search says it looked at the first files only.</summary>
    public const int MaxIndexedFiles = 200_000;

    /// <summary>A folder's entries: folders first, then files, each in natural order ("file2" before "file10").</summary>
    public static List<FileEntry> List(string dir)
    {
        var result = new List<FileEntry>();
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
        {
            if (Hidden.Contains(info.Name)) continue;
            var isDir = info is DirectoryInfo;
            long size = 0;
            if (!isDir)
            {
                try { size = ((FileInfo)info).Length; }
                catch (IOException) { }
            }
            result.Add(new FileEntry(info.Name, info.FullName, isDir, size));
        }
        result.Sort(Compare);
        return result;
    }

    public static int Compare(FileEntry a, FileEntry b) =>
        a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : NaturalCompare(a.Name, b.Name);

    /// <summary>Case-insensitive, with runs of digits compared by value.</summary>
    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var si = i;
                var sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                var c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
                continue;
            }
            var ca = char.ToLowerInvariant(a[i]);
            var cb = char.ToLowerInvariant(b[j]);
            if (ca != cb) return ca.CompareTo(cb);
            i++;
            j++;
        }
        var rest = (a.Length - i).CompareTo(b.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(a, b);
    }

    /// <summary><paramref name="path"/> relative to <paramref name="root"/> with '/' separators; null when outside it.</summary>
    public static string? Relative(string root, string path)
    {
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var p = Path.GetFullPath(path);
        if (string.Equals(r, p, PathComparison)) return "";
        if (!p.StartsWith(r + Path.DirectorySeparatorChar, PathComparison)) return null;
        return p[(r.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// A path as the agent wrote it (relative to the project, absolute, "~/…", "./…"), as a full path; null when it
    /// is not a usable path.
    /// </summary>
    public static string? Resolve(string? root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('`', '"', '\'');
        if (p.StartsWith("~/", StringComparison.Ordinal) || p == "~")
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Length > 2 ? p[2..] : "");
        if (p.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(p, UriKind.Absolute, out var uri)) p = uri.LocalPath;
        if (p.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        // Relative to what? Without a project, not to wherever the app happens to run
        if (!Path.IsPathRooted(p) && root is null) return null;
        try
        {
            return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(root!, p));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every file of the project for the name search, relative and '/'-separated: git's own list when the folder is in a
    /// repository (tracked and untracked, without what .gitignore excludes), else a walk that skips hidden and heavy
    /// folders. At most <see cref="MaxIndexedFiles"/>.
    /// </summary>
    public static async Task<(List<string> Paths, bool Truncated)> IndexAsync(string root, bool useGit, CancellationToken ct)
    {
        if (useGit)
        {
            var listed = await Git.RunAsync(root, ["-c", "core.quotepath=off", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], ct, TimeSpan.FromSeconds(20));
            if (listed is { ExitCode: 0 } ok)
            {
                var paths = new List<string>();
                var seen = new HashSet<string>(PathComparer);
                foreach (var p in ok.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!seen.Add(p)) continue; // an unmerged file is listed once per stage
                    paths.Add(p);
                    if (paths.Count >= MaxIndexedFiles) return (paths, true);
                }
                return (paths, false);
            }
        }
        return await Task.Run(() => Walk(root, ct), ct);
    }

    private static (List<string>, bool) Walk(string root, CancellationToken ct)
    {
        var paths = new List<string>();
        var pending = new Stack<(string Dir, string Rel)>();
        pending.Push((root, ""));
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, rel) = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var e in entries)
            {
                if (Hidden.Contains(e.Name)) continue;
                var r = rel.Length == 0 ? e.Name : rel + "/" + e.Name;
                if (e is DirectoryInfo d)
                {
                    // No heavy folders, and no links to folders (a link can loop back up the tree)
                    if (Heavy.Contains(e.Name) || d.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    pending.Push((d.FullName, r));
                }
                else
                {
                    paths.Add(r);
                    if (paths.Count >= MaxIndexedFiles) return (paths, true);
                }
            }
        }
        return (paths, false);
    }

    /// <summary>
    /// The best <paramref name="max"/> matches of <paramref name="query"/> (case-insensitive): the file name equal,
    /// starting with, containing the query, then the path containing it, then the name's letters in order. Several
    /// words must all be in the path; the last one ranks. A query with '/' matches against the path.
    /// </summary>
    public static List<FileHit> Search(IReadOnlyList<string> paths, string query, int max, CancellationToken ct)
    {
        var words = query.Trim().Replace('\\', '/').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        var last = words[^1];
        var byPath = last.Contains('/');
        var hits = new List<FileHit>();
        for (var i = 0; i < paths.Count; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            var path = paths[i];
            var ok = true;
            for (var w = 0; w < words.Length - 1 && ok; w++) ok = path.Contains(words[w], StringComparison.OrdinalIgnoreCase);
            if (!ok) continue;
            var name = path[(path.LastIndexOf('/') + 1)..];
            var score = byPath ? PathScore(path, last) : NameScore(name, path, last);
            if (score >= 0) hits.Add(new FileHit(path, score));
        }
        hits.Sort((a, b) => a.Score != b.Score ? a.Score.CompareTo(b.Score)
            : a.RelativePath.Length != b.RelativePath.Length ? a.RelativePath.Length.CompareTo(b.RelativePath.Length)
            : NaturalCompare(a.RelativePath, b.RelativePath));
        return hits.Count > max ? hits[..max] : hits;
    }

    private static int NameScore(string name, string path, string q)
    {
        if (name.Equals(q, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains(q, StringComparison.OrdinalIgnoreCase)) return 2;
        if (path.Contains(q, StringComparison.OrdinalIgnoreCase)) return 3;
        return InOrder(name, q) ? 4 : -1;
    }

    private static int PathScore(string path, string q)
    {
        if (path.EndsWith(q, StringComparison.OrdinalIgnoreCase)) return 0;
        if (path.Contains(q, StringComparison.OrdinalIgnoreCase)) return 1;
        return InOrder(path, q) ? 4 : -1;
    }

    /// <summary>The query's letters appear in order in <paramref name="text"/> ("fvm" finds FilesViewModel).</summary>
    private static bool InOrder(string text, string q)
    {
        var at = 0;
        foreach (var c in q)
        {
            at = text.IndexOf(c.ToString(), at, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            at++;
        }
        return true;
    }

    /// <summary>A byte count as people read it: "812 B", "14 KB", "3.4 MB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB".Replace(',', '.'),
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB".Replace(',', '.'),
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.#} GB".Replace(',', '.'),
    };
}

/// <summary>What git says about a file: the marks the Files pane shows.</summary>
public enum GitMark { None, Modified, Added, Untracked, Deleted, Renamed, Conflicted, Ignored }

/// <summary>
/// One reading of <c>git status --porcelain=v1 -z --ignored</c> for a folder: every changed file (full path), the
/// untracked and ignored folders git reports as a whole, the folders that contain changes, and the deleted files by
/// folder (so the tree can still show them).
/// </summary>
public sealed class GitSnapshot
{
    private readonly Dictionary<string, GitMark> _files = new(ProjectFiles.PathComparer);
    private readonly List<string> _untrackedDirs = [];
    private readonly List<string> _ignoredDirs = [];
    private readonly HashSet<string> _changedDirs = new(ProjectFiles.PathComparer);
    private readonly Dictionary<string, List<string>> _deletedByDir = new(ProjectFiles.PathComparer);

    public string TopLevel { get; }

    public GitSnapshot(string topLevel) => TopLevel = Path.TrimEndingDirectorySeparator(Path.GetFullPath(topLevel));

    /// <summary>Changed files (and untracked folders, ending in a separator) with their marks, ignored ones left out.</summary>
    public IEnumerable<(string Path, GitMark Mark)> Changes =>
        _files.Where(kv => kv.Value != GitMark.Ignored).Select(kv => (kv.Key, kv.Value))
            .Concat(_untrackedDirs.Select(d => (d + Path.DirectorySeparatorChar, GitMark.Untracked)));

    public static GitSnapshot Parse(string topLevel, string porcelainZ)
    {
        var s = new GitSnapshot(topLevel);
        var parts = porcelainZ.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var rec = parts[i];
            if (rec.Length < 4) continue;
            var x = rec[0];
            var y = rec[1];
            var rel = rec[3..];
            // A rename or copy is followed by its original path
            if (x is 'R' or 'C' || y is 'R' or 'C') i++;
            var full = s.Full(rel.TrimEnd('/'));
            var isDir = rel.EndsWith('/');
            var mark = (x, y) switch
            {
                ('?', '?') => GitMark.Untracked,
                ('!', '!') => GitMark.Ignored,
                ('U', _) or (_, 'U') or ('D', 'D') or ('A', 'A') => GitMark.Conflicted,
                ('A', _) => GitMark.Added,
                ('D', _) or (_, 'D') => GitMark.Deleted,
                ('R', _) or ('C', _) => GitMark.Renamed,
                _ => GitMark.Modified,
            };
            if (isDir && mark == GitMark.Untracked) s._untrackedDirs.Add(full);
            else if (isDir && mark == GitMark.Ignored) s._ignoredDirs.Add(full);
            else s._files[full] = mark;
            if (mark == GitMark.Deleted && Path.GetDirectoryName(full) is { } parent)
            {
                if (!s._deletedByDir.TryGetValue(parent, out var list)) s._deletedByDir[parent] = list = [];
                list.Add(full);
            }
            if (mark != GitMark.Ignored)
                for (var d = Path.GetDirectoryName(full); d is not null && d.Length >= s.TopLevel.Length; d = Path.GetDirectoryName(d))
                    if (!s._changedDirs.Add(d)) break;
        }
        return s;
    }

    private string Full(string rel) => Path.GetFullPath(Path.Combine(TopLevel, rel.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The file's own mark; inside an untracked or ignored folder, that folder's.</summary>
    public GitMark MarkOf(string fullPath)
    {
        if (_files.TryGetValue(fullPath, out var m)) return m;
        if (Under(_untrackedDirs, fullPath)) return GitMark.Untracked;
        if (Under(_ignoredDirs, fullPath)) return GitMark.Ignored;
        return GitMark.None;
    }

    public bool IsIgnored(string fullPath) => MarkOf(fullPath) == GitMark.Ignored;

    /// <summary>A folder with changed files somewhere below it (it gets a dot, as in editors).</summary>
    public bool HasChangesInside(string dirPath) => _changedDirs.Contains(Path.TrimEndingDirectorySeparator(dirPath));

    /// <summary>Files git knows were deleted from <paramref name="dirPath"/> (still in the last commit).</summary>
    public IReadOnlyList<string> DeletedIn(string dirPath) =>
        _deletedByDir.TryGetValue(Path.TrimEndingDirectorySeparator(dirPath), out var l) ? l : [];

    private static bool Under(List<string> dirs, string path)
    {
        foreach (var d in dirs)
            if (string.Equals(d, path, ProjectFiles.PathComparison) || path.StartsWith(d + Path.DirectorySeparatorChar, ProjectFiles.PathComparison))
                return true;
        return false;
    }

    public static string Letter(GitMark m) => m switch
    {
        GitMark.Modified => "M",
        GitMark.Added => "A",
        GitMark.Untracked => "U",
        GitMark.Deleted => "D",
        GitMark.Renamed => "R",
        GitMark.Conflicted => "!",
        _ => "",
    };

    public static string Describe(GitMark m) => m switch
    {
        GitMark.Modified => "Modified (not committed)",
        GitMark.Added => "Added to git (not committed)",
        GitMark.Untracked => "New file (not in git yet)",
        GitMark.Deleted => "Deleted (not committed)",
        GitMark.Renamed => "Renamed (not committed)",
        GitMark.Conflicted => "Merge conflict",
        GitMark.Ignored => "Ignored by .gitignore",
        _ => "",
    };
}

/// <summary>git as a tool: the Files pane reads status and file lists with it, off the UI thread, never locking.</summary>
public static class Git
{
    public sealed record Result(int ExitCode, string Output);

    /// <summary>git's program name (tests may point it elsewhere).</summary>
    public static string Program { get; set; } = "git";

    /// <summary>The repository's top folder when <paramref name="dir"/> is in one; null otherwise or when git is missing.</summary>
    public static async Task<string?> TopLevelAsync(string dir, CancellationToken ct)
    {
        var r = await RunAsync(dir, ["rev-parse", "--show-toplevel"], ct, TimeSpan.FromSeconds(5));
        return r is { ExitCode: 0 } && r.Output.Trim() is { Length: > 0 } top ? Path.GetFullPath(top) : null;
    }

    /// <summary>The status of the repository around <paramref name="dir"/>; null when it is not one or git is missing.</summary>
    public static async Task<GitSnapshot?> StatusAsync(string dir, CancellationToken ct)
    {
        if (await TopLevelAsync(dir, ct) is not { } top) return null;
        var r = await RunAsync(dir, ["-c", "core.quotepath=off", "status", "--porcelain=v1", "-z", "--ignored=traditional", "--untracked-files=normal"], ct, TimeSpan.FromSeconds(20));
        return r is { ExitCode: 0 } ? GitSnapshot.Parse(top, r.Output) : null;
    }

    /// <summary>
    /// Runs git in <paramref name="dir"/> and returns its output; null when git cannot start or the time runs out.
    /// GIT_OPTIONAL_LOCKS=0: reading the status never takes the index lock the agent's own git commands need.
    /// </summary>
    public static async Task<Result?> RunAsync(string dir, IReadOnlyList<string> args, CancellationToken ct, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(Program)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var (k, v) in WorkspaceTools.UntrustedRepoGit) psi.Environment[k] = v;
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
        if (p is null) return null;
        using var process = p;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token);
            var text = await output;
            await error;
            return new Result(process.ExitCode, text);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }
}
