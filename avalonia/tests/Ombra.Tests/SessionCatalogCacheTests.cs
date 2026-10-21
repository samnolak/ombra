using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class SessionCatalogCacheTests
{
    private sealed class CatalogFiles
    {
        public string Directory { get; } = TestProcesses.TempDir("catalog-cache");
        public string Root { get; }
        public string CachePath { get; }
        public ConcurrentBag<string> Opens { get; } = [];

        public CatalogFiles()
        {
            Root = System.IO.Directory.CreateDirectory(Path.Combine(Directory, "sessions")).FullName;
            CachePath = SessionCatalogCache.PathFor(Path.Combine(Directory, "profile.json"));
        }

        public SessionCatalogCache Cache() => new(CachePath, path =>
        {
            var stream = SessionCatalog.OpenSummary(path);
            Opens.Add(path);
            return stream;
        });

        public string Add(string id, string title, string? root = null)
        {
            var project = System.IO.Directory.CreateDirectory(Path.Combine(root ?? Root, "project")).FullName;
            var path = Path.Combine(project, id + ".jsonl");
            File.WriteAllText(path, Content(title));
            return path;
        }
    }

    private static string Content(string title) => string.Join('\n',
        JsonSerializer.Serialize(new { type = "title", title }),
        JsonSerializer.Serialize(new { type = "session", id = "session", cwd = "/work/project" }),
        JsonSerializer.Serialize(new { type = "message", message = new { role = "user", content = "Question", timestamp = 1700000000000L } })) + "\n";

    [Fact]
    public async Task Warm_scans_and_a_new_instance_reuse_real_content_reads_and_store_private_summaries()
    {
        var f = new CatalogFiles();
        var first = f.Add("one", "First");
        var second = f.Add("two", "Second");
        var cache = f.Cache();
        var cold = await cache.ScanAsync(f.Root);
        Assert.True(cold.IsAuthoritative);
        Assert.Equal(2, f.Opens.Count);
        var written = File.GetLastWriteTimeUtc(f.CachePath);
        using var one = new FileStream(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var two = new FileStream(second, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        foreach (var reader in new[] { cache, f.Cache() })
        {
            var warm = await reader.ScanAsync(f.Root);
            Assert.True(warm.IsAuthoritative);
            Assert.Equal(cold.Sessions.OrderBy(s => s.Path), warm.Sessions.OrderBy(s => s.Path));
        }
        Assert.Equal(2, f.Opens.Count);
        Assert.Equal(written, File.GetLastWriteTimeUtc(f.CachePath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(f.CachePath));
    }

    [Fact]
    public async Task Size_and_timestamp_changes_new_files_and_authoritative_deletions_are_reconciled()
    {
        var f = new CatalogFiles();
        var timed = f.Add("time", "Before");
        var sized = f.Add("size", "Small");
        var unchanged = f.Add("unchanged", "Keep");
        var deleted = f.Add("deleted", "Delete");
        await f.Cache().ScanAsync(f.Root);
        var sizeTime = File.GetLastWriteTimeUtc(sized);
        var time = File.GetLastWriteTimeUtc(timed);
        File.WriteAllText(timed, Content("After!")); // Same bytes, different mtime.
        File.SetLastWriteTimeUtc(timed, time.AddSeconds(5));
        File.WriteAllText(sized, Content("A much longer title"));
        File.SetLastWriteTimeUtc(sized, sizeTime); // Different bytes, same mtime.
        File.Delete(deleted);
        var added = f.Add("added", "Added");
        f.Opens.Clear();
        using var held = new FileStream(unchanged, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await f.Cache().ScanAsync(f.Root);
        Assert.True(result.IsAuthoritative);
        Assert.Equal(new[] { added, sized, timed }.Order(), f.Opens.Order());
        Assert.Equal("After!", result.Sessions.Single(s => s.Path == timed).Title);
        Assert.Equal("A much longer title", result.Sessions.Single(s => s.Path == sized).Title);
        Assert.DoesNotContain(result.Sessions, s => s.Path == deleted);
        Assert.DoesNotContain((await f.Cache().ScanAsync(f.Root)).Sessions, s => s.Path == deleted);
    }

    [Fact]
    public async Task Root_changes_and_copied_profile_caches_never_import_another_catalog()
    {
        var f = new CatalogFiles();
        f.Add("one", "Private first root");
        var cache = f.Cache();
        await cache.ScanAsync(f.Root);
        var otherRoot = Path.Combine(f.Directory, "other-root");
        var absent = await cache.ScanAsync(otherRoot);
        Assert.False(absent.IsAuthoritative);
        Assert.Empty(absent.Sessions);
        var other = f.Add("other", "Other root", otherRoot);
        var changed = await cache.ScanAsync(otherRoot);
        Assert.Equal(other, Assert.Single(changed.Sessions).Path);
        var restored = await f.Cache().ScanAsync(f.Root);
        Assert.Equal("Private first root", Assert.Single(restored.Sessions).Title);

        var otherProfile = SessionCatalogCache.PathFor(Path.Combine(f.Directory, "other-profile.json"));
        File.Copy(f.CachePath, otherProfile);
        var profileCache = new SessionCatalogCache(otherProfile);
        var unavailable = await profileCache.ScanAsync(f.Root, 500, default,
            _ => throw new IOException("Root unavailable"), p => p.EnumerateFiles("*.jsonl"), p => p.LastWriteTimeUtc);
        Assert.False(unavailable.IsAuthoritative);
        Assert.Empty(unavailable.Sessions);
        Assert.Equal("Private first root", Assert.Single((await profileCache.ScanAsync(f.Root)).Sessions).Title);
    }

    [Fact]
    public async Task Stored_root_is_the_last_saved_scan_of_this_profile_only()
    {
        // The sidebar lists this root at startup, before omp has started and reported its own (MainViewModel.Sessions.cs)
        var f = new CatalogFiles();
        Assert.Null(f.Cache().StoredRoot());
        f.Add("one", "First");
        await f.Cache().ScanAsync(f.Root);
        Assert.Equal(f.Root, f.Cache().StoredRoot());
        var otherRoot = Path.Combine(f.Directory, "other-root");
        f.Add("other", "Other", otherRoot);
        await f.Cache().ScanAsync(otherRoot);
        Assert.Equal(otherRoot, f.Cache().StoredRoot());

        var otherProfile = SessionCatalogCache.PathFor(Path.Combine(f.Directory, "other-profile.json"));
        File.Copy(f.CachePath, otherProfile);
        Assert.Null(new SessionCatalogCache(otherProfile).StoredRoot());
        File.WriteAllText(f.CachePath, "{\"Version\":1,");
        Assert.Null(f.Cache().StoredRoot());
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("version")]
    [InlineData("entry")]
    [InlineData("foreign-path")]
    public async Task Damaged_or_stale_cache_is_rebuilt_without_discarding_live_rows(string damage)
    {
        var f = new CatalogFiles();
        var file = f.Add("one", "Known title");
        var cache = f.Cache();
        await cache.ScanAsync(f.Root);
        var document = JsonNode.Parse(File.ReadAllText(f.CachePath))!;
        switch (damage)
        {
            case "version": document["Version"] = 999; break;
            case "entry": document["Entries"]![0] = null; break;
            case "foreign-path": document["Entries"]![0]!["Summary"]!["Path"] = Path.Combine(f.Directory, "elsewhere.jsonl"); break;
        }
        File.WriteAllText(f.CachePath, damage == "truncated" ? "{\"Version\":" : document.ToJsonString());
        var failed = await cache.ScanAsync(f.Root, 500, default,
            _ => throw new IOException("Root temporarily unavailable"), p => p.EnumerateFiles("*.jsonl"), p => p.LastWriteTimeUtc);
        Assert.False(failed.IsAuthoritative);
        Assert.Equal(file, Assert.Single(failed.Sessions).Path);
        // Corrupt it again to exercise a cold rebuild rather than the live instance's repaired document.
        File.WriteAllText(f.CachePath, damage == "truncated" ? "{" : document.ToJsonString());
        f.Opens.Clear();
        var recovered = await f.Cache().ScanAsync(f.Root);
        Assert.True(recovered.IsAuthoritative);
        Assert.Equal("Known title", Assert.Single(recovered.Sessions).Title);
        Assert.Equal(file, Assert.Single(f.Opens));
        using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True((await f.Cache().ScanAsync(f.Root)).IsAuthoritative);
    }

    [Fact]
    public async Task Partial_enumeration_preserves_persisted_rows_until_an_authoritative_scan_proves_deletion()
    {
        var f = new CatalogFiles();
        var changed = f.Add("changed", "Before");
        var deleted = f.Add("deleted", "Retained during outage");
        await f.Cache().ScanAsync(f.Root);
        File.WriteAllText(changed, Content("After the change"));
        File.Delete(deleted);
        static IEnumerable<FileInfo> PartialFiles(DirectoryInfo project)
        {
            foreach (var file in project.EnumerateFiles("*.jsonl")) yield return file;
            throw new IOException("Enumeration interrupted");
        }
        var partial = await f.Cache().ScanAsync(f.Root, 500, default,
            r => r.EnumerateDirectories(), PartialFiles, p => p.LastWriteTimeUtc);
        Assert.False(partial.EnumerationComplete);
        Assert.False(partial.IsAuthoritative);
        Assert.Equal("After the change", partial.Sessions.Single(s => s.Path == changed).Title);
        Assert.Contains(partial.Sessions, s => s.Path == deleted);
        var outage = await f.Cache().ScanAsync(f.Root, 500, default,
            _ => throw new IOException("Still unavailable"), PartialFiles, p => p.LastWriteTimeUtc);
        Assert.Equal(2, outage.Sessions.Count);
        var complete = await f.Cache().ScanAsync(f.Root);
        Assert.True(complete.IsAuthoritative);
        Assert.Equal(changed, Assert.Single(complete.Sessions).Path);
        Assert.Single((await f.Cache().ScanAsync(f.Root)).Sessions);
    }

    [Fact]
    public async Task A_capped_scan_keeps_older_persisted_rows_across_restarts()
    {
        var f = new CatalogFiles();
        var old = f.Add("old", "Old known row");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-1));
        await f.Cache().ScanAsync(f.Root);
        var added = f.Add("new", "New row");
        var capped = await f.Cache().ScanAsync(f.Root, limit: 1);
        Assert.True(capped.IsCapped);
        Assert.False(capped.IsAuthoritative);
        Assert.Contains(capped.Sessions, s => s.Path == old);
        Assert.Contains(capped.Sessions, s => s.Path == added);
        File.Delete(old);
        var complete = await f.Cache().ScanAsync(f.Root);
        Assert.True(complete.IsAuthoritative);
        Assert.Equal(added, Assert.Single(complete.Sessions).Path);
    }

    [Fact]
    public async Task Unfinished_entries_are_not_persisted_under_stable_metadata()
    {
        var f = new CatalogFiles();
        var file = f.Add("partial", "Before");
        File.WriteAllText(file, Content("Before")[..^1] + " ");
        var time = File.GetLastWriteTimeUtc(file);
        var cache = f.Cache();
        var partial = await cache.ScanAsync(f.Root);
        Assert.False(partial.IsAuthoritative);
        Assert.NotEmpty(partial.Errors);
        File.WriteAllText(file, Content("After!"));
        File.SetLastWriteTimeUtc(file, time);
        var repaired = await f.Cache().ScanAsync(f.Root);
        Assert.True(repaired.IsAuthoritative);
        Assert.Equal("After!", Assert.Single(repaired.Sessions).Title);
        Assert.Equal("After!", Assert.Single((await cache.ScanAsync(f.Root)).Sessions).Title);
    }

    private sealed class ShortReadStream(string path)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position >= 64 ? ValueTask.FromResult(0) : base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, 64 - Position)], cancellationToken);
    }

    [Fact]
    public async Task Short_content_reads_are_retried_even_when_file_metadata_did_not_change()
    {
        var f = new CatalogFiles();
        f.Add("one", "Complete title");
        var shortReader = new SessionCatalogCache(f.CachePath, path => new ShortReadStream(path));
        var incomplete = await shortReader.ScanAsync(f.Root);
        Assert.False(incomplete.IsAuthoritative);
        Assert.Empty(incomplete.Sessions);
        var complete = await f.Cache().ScanAsync(f.Root);
        Assert.True(complete.IsAuthoritative);
        Assert.Equal("Complete title", Assert.Single(complete.Sessions).Title);
        Assert.Single(f.Opens);
    }

    [Fact]
    public async Task Changes_between_metadata_enumeration_and_open_cannot_poison_the_cache()
    {
        var f = new CatalogFiles();
        var file = f.Add("one", "Before");
        var time = File.GetLastWriteTimeUtc(file);
        var racing = new SessionCatalogCache(f.CachePath, path =>
        {
            File.WriteAllText(path, Content("Changed during read"));
            return SessionCatalog.OpenSummary(path);
        });
        var changed = await racing.ScanAsync(f.Root);
        Assert.False(changed.IsAuthoritative);
        Assert.NotEmpty(changed.Errors);
        File.WriteAllText(file, Content("After!"));
        File.SetLastWriteTimeUtc(file, time);
        var complete = await f.Cache().ScanAsync(f.Root);
        Assert.True(complete.IsAuthoritative);
        Assert.Equal("After!", Assert.Single(complete.Sessions).Title);
    }

    [Fact]
    public async Task Cancellation_leaves_the_last_committed_cache_intact_and_releases_the_scan_gate()
    {
        var f = new CatalogFiles();
        var file = f.Add("one", "Before");
        await f.Cache().ScanAsync(f.Root);
        var committed = File.ReadAllBytes(f.CachePath);
        File.WriteAllText(file, Content("Updated after cancellation"));
        using var cancellation = new CancellationTokenSource();
        var cache = new SessionCatalogCache(f.CachePath, path =>
        {
            cancellation.Cancel();
            return SessionCatalog.OpenSummary(path);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.ScanAsync(f.Root, ct: cancellation.Token));
        Assert.Equal(committed, File.ReadAllBytes(f.CachePath));
        var recovered = await cache.ScanAsync(f.Root);
        Assert.True(recovered.IsAuthoritative);
        Assert.Equal("Updated after cancellation", Assert.Single(recovered.Sessions).Title);
    }

    [Fact]
    public async Task Concurrent_scans_share_complete_reads_and_a_contending_writer_cannot_replace_the_cache()
    {
        var f = new CatalogFiles();
        var file = f.Add("one", "Before");
        var cache = f.Cache();
        var scans = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.ScanAsync(f.Root)));
        Assert.All(scans, scan => Assert.True(scan.IsAuthoritative));
        Assert.Single(f.Opens);
        var committed = File.ReadAllBytes(f.CachePath);
        File.WriteAllText(file, Content("Updated while another writer owns storage"));
        using (var lease = new FileStream(f.CachePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var contended = await f.Cache().ScanAsync(f.Root);
            Assert.True(contended.IsAuthoritative);
            Assert.Equal("Updated while another writer owns storage", Assert.Single(contended.Sessions).Title);
            Assert.Equal(committed, File.ReadAllBytes(f.CachePath));
        }
        var committedUpdate = await f.Cache().ScanAsync(f.Root);
        using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(committedUpdate.Sessions, (await f.Cache().ScanAsync(f.Root)).Sessions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_scan_cannot_republish_live_rows_superseded_by_another_instance(bool delete)
    {
        var f = new CatalogFiles();
        var file = f.Add("one", "Old title");
        var older = f.Cache();
        var newer = f.Cache();
        await older.ScanAsync(f.Root);
        await newer.ScanAsync(f.Root);
        if (delete) File.Delete(file);
        else File.WriteAllText(file, Content("Title committed by the newer instance"));
        var authoritative = await newer.ScanAsync(f.Root);
        Assert.True(authoritative.IsAuthoritative);
        var committed = File.ReadAllBytes(f.CachePath);

        Task<SessionCatalogScanResult> FailedScan(SessionCatalogCache cache) =>
            cache.ScanAsync(f.Root, 500, default, _ => throw new IOException("Root unavailable"),
                p => p.EnumerateFiles("*.jsonl"), p => p.LastWriteTimeUtc);

        var live = await FailedScan(older);
        Assert.False(live.IsAuthoritative);
        // An incomplete scan may retain a live row, but may not resurrect it in the shared cache.
        Assert.Equal(delete ? "Old title" : "Title committed by the newer instance", Assert.Single(live.Sessions).Title);
        Assert.Equal(committed, File.ReadAllBytes(f.CachePath));
        var restarted = await FailedScan(f.Cache());
        Assert.False(restarted.IsAuthoritative);
        if (delete) Assert.Empty(restarted.Sessions);
        else Assert.Equal("Title committed by the newer instance", Assert.Single(restarted.Sessions).Title);
    }

    [Fact]
    public async Task Unwritable_cache_storage_does_not_hide_sessions_or_disable_in_memory_reuse()
    {
        var f = new CatalogFiles();
        var file = f.Add("one", "Still visible");
        var blocked = Path.Combine(f.Directory, "not-a-directory");
        File.WriteAllText(blocked, "occupied");
        var cache = new SessionCatalogCache(Path.Combine(blocked, "cache.json"));
        Assert.True((await cache.ScanAsync(f.Root)).IsAuthoritative);
        using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var warm = await cache.ScanAsync(f.Root);
        Assert.True(warm.IsAuthoritative);
        Assert.Equal("Still visible", Assert.Single(warm.Sessions).Title);
    }
}
