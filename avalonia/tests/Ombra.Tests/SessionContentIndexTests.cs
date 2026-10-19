using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>The sidebar's search inside saved sessions: what is found, what is read again, what survives damage.</summary>
public sealed class SessionContentIndexTests
{
    private sealed class Journals
    {
        public string Directory { get; } = TestProcesses.TempDir("content-index");
        public string Root { get; }
        public string IndexPath { get; }
        public ConcurrentBag<string> Opens { get; } = [];

        public Journals()
        {
            Root = System.IO.Directory.CreateDirectory(Path.Combine(Directory, "sessions")).FullName;
            IndexPath = SessionContentIndex.PathFor(Path.Combine(Directory, "profile.json"));
        }

        public SessionContentIndex Index(Func<string, Stream>? open = null) => new(IndexPath, path =>
        {
            Opens.Add(path);
            return open is null ? SessionCatalog.OpenSummary(path) : open(path);
        });

        public string Add(string id, params string[] lines)
        {
            var project = System.IO.Directory.CreateDirectory(Path.Combine(Root, "project")).FullName;
            var path = Path.Combine(project, id + ".jsonl");
            File.WriteAllText(path, Journal(id, lines));
            return path;
        }

        public static SessionSummary Summary(string path) =>
            new(path, Path.GetFileNameWithoutExtension(path), "/work/project", "Unrelated title", DateTimeOffset.UtcNow);
    }

    private static string Journal(string id, string[] lines) =>
        string.Join('\n', [JsonSerializer.Serialize(new { type = "title", title = "Unrelated title" }),
            JsonSerializer.Serialize(new { type = "session", version = 3, id, cwd = "/work/project" }), .. lines]) + "\n";

    private static string Message(string role, object content) =>
        JsonSerializer.Serialize(new { type = "message", message = new { role, content, timestamp = 1700000000000L } });

    private static string User(string text) => Message("user", text);
    private static string Assistant(string text) => Message("assistant", new object[] { new { type = "text", text } });

    private static Task<SessionContentSearchResult> Search(SessionContentIndex index, Journals j, string query, params string[] paths) =>
        index.SearchAsync(j.Root, [.. paths.Select(Journals.Summary)], query);

    [Fact]
    public async Task Messages_are_found_beyond_the_title_with_the_newest_match_cut_around_it()
    {
        var j = new Journals();
        var found = j.Add("found",
            User("How do I configure the zebra parser?"),
            Message("assistant", new object[]
            {
                new { type = "thinking", thinking = "pondering quietly" },
                new { type = "text", text = "Use the   Zebra\nflag." },
                new { type = "toolCall", id = "t1", name = "read", arguments = new { path = "giraffe.txt" } },
            }),
            Message("toolResult", new object[] { new { type = "text", text = "giraffe output from a tool" } }));
        var other = j.Add("other", User("Nothing to see here"));
        var index = j.Index();

        var result = await Search(index, j, "zebra", found, other);
        Assert.Empty(result.Errors);
        var match = Assert.Single(result.Matches);
        Assert.Equal(found, match.Path);
        // The newest message with the query, its whitespace as one line, the match as the message writes it
        Assert.Equal("Use the ", match.Before);
        Assert.Equal("Zebra", match.Match);
        Assert.Equal(" flag.", match.After);
        Assert.Equal("Use the Zebra flag.", match.Text);
        Assert.Equal("parser", (await Search(index, j, "  PARSER ", found, other)).Matches.Single().Match);
        // Not thinking, not tool calls or tool output
        Assert.Empty((await Search(index, j, "pondering", found, other)).Matches);
        Assert.Empty((await Search(index, j, "giraffe", found, other)).Matches);
    }

    [Fact]
    public async Task Long_messages_are_cut_into_a_snippet_with_ellipses()
    {
        var j = new Journals();
        var file = j.Add("long", User(new string('a', 300) + " needle " + new string('b', 300)));
        var match = Assert.Single((await Search(j.Index(), j, "needle", file)).Matches);
        Assert.StartsWith("…", match.Before);
        Assert.EndsWith("…", match.After);
        Assert.True(match.Text.Length < 200, match.Text);
    }

    [Fact]
    public async Task Edited_journals_are_read_again_and_unchanged_ones_never_are()
    {
        var j = new Journals();
        var edited = j.Add("edited", User("first question"));
        var resized = j.Add("resized", User("about lions"));
        var same = j.Add("same", User("about tigers"));
        var all = new[] { edited, resized, same };
        var index = j.Index();
        Assert.Empty((await Search(index, j, "okapi", all)).Matches);
        Assert.Equal(all.Order(), j.Opens.Order());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(j.IndexPath));

        // Nothing changed: a new query reads no journal, in this instance or a new one (the private sidecar)
        j.Opens.Clear();
        Assert.Single((await Search(index, j, "tigers", all)).Matches);
        Assert.Single((await Search(j.Index(), j, "lions", all)).Matches);
        Assert.Empty(j.Opens);

        // An appended message (size and time) and a same-size rewrite (time only) are read again; the third is not
        File.AppendAllText(edited, Assistant("an okapi appears") + "\n");
        var time = File.GetLastWriteTimeUtc(resized);
        File.WriteAllText(resized, Journal("resized", [User("about llama")]));
        File.SetLastWriteTimeUtc(resized, time.AddSeconds(5));
        var result = await Search(j.Index(), j, "okapi", all);
        Assert.Equal(edited, Assert.Single(result.Matches).Path);
        Assert.Equal(new[] { edited, resized }.Order(), j.Opens.Order());
        Assert.Equal(resized, Assert.Single((await Search(index, j, "llama", all)).Matches).Path);
        Assert.Empty((await Search(index, j, "lions", all)).Matches);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("version")]
    [InlineData("entry")]
    [InlineData("foreign-path")]
    [InlineData("oversized")]
    public async Task A_damaged_index_is_rebuilt_from_the_journals(string damage)
    {
        var j = new Journals();
        var file = j.Add("one", User("the walrus speaks"));
        await Search(j.Index(), j, "walrus", file);
        var document = JsonNode.Parse(File.ReadAllText(j.IndexPath))!;
        switch (damage)
        {
            case "version": document["Version"] = 999; break;
            case "entry": document["Entries"]![0] = null; break;
            case "foreign-path": document["Entries"]![0]!["Path"] = Path.Combine(j.Directory, "elsewhere.jsonl"); break;
            case "oversized": document["Entries"]![0]!["Messages"] = new JsonArray(JsonValue.Create(new string('x', 3000))); break;
        }
        File.WriteAllText(j.IndexPath, damage == "truncated" ? "{\"Version\":" : document.ToJsonString());
        j.Opens.Clear();

        var rebuilt = await Search(j.Index(), j, "walrus", file);
        Assert.Equal(file, Assert.Single(rebuilt.Matches).Path);
        Assert.Equal(file, Assert.Single(j.Opens));
        // The rebuilt sidecar serves the next instance without reading the journal
        j.Opens.Clear();
        Assert.Single((await Search(j.Index(), j, "speaks", file)).Matches);
        Assert.Empty(j.Opens);
    }

    [Fact]
    public async Task Oversized_lines_are_skipped_and_reading_resumes_after_them()
    {
        var j = new Journals();
        var file = j.Add("big",
            User("needle " + new string('x', SessionContentIndex.MaxLineBytes)),
            Assistant("after the blob comes the haystack"));
        var index = j.Index();
        Assert.Empty((await Search(index, j, "needle", file)).Matches);
        Assert.Single((await Search(index, j, "haystack", file)).Matches);
    }

    private sealed class GatedStream(string path, Task gate)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await gate.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class ObservedStream(string path, CountdownEvent done)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true)
    {
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            done.Signal();
        }
    }

    [Fact]
    public async Task Cancellation_keeps_the_journals_already_read_and_releases_the_index()
    {
        var j = new Journals();
        var first = j.Add("first", User("a quiet heron"));
        var slow = j.Add("slow", User("a slow heron"));
        var third = j.Add("third", User("a third heron"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var done = new CountdownEvent(2);
        GatedStream? gated = null;
        var index = j.Index(path => path == slow ? (Stream)(gated = new GatedStream(path, gate.Task)) : new ObservedStream(path, done));
        using var cts = new CancellationTokenSource();

        var running = index.SearchAsync(j.Root, [.. new[] { first, slow, third }.Select(Journals.Summary)], "heron", cts.Token);
        await Task.Run(() => Assert.True(done.Wait(TimeSpan.FromSeconds(20)), "the other journals read"));
        while (gated is null) await Task.Delay(5);
        await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        cts.Cancel(); // the next keystroke
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        gate.SetResult();
        j.Opens.Clear();
        var result = await index.SearchAsync(j.Root, [.. new[] { first, slow, third }.Select(Journals.Summary)], "heron")
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(3, result.Matches.Count);
        Assert.Equal(slow, Assert.Single(j.Opens)); // only the journal the cancelled search had not finished
    }

    [Fact]
    public async Task An_unreadable_journal_keeps_its_last_text_and_is_reported()
    {
        var j = new Journals();
        var locked = j.Add("locked", User("the badger digs"));
        var fine = j.Add("fine", User("the badger sleeps"));
        Assert.Equal(2, (await Search(j.Index(), j, "badger", locked, fine)).Matches.Count);

        File.AppendAllText(locked, User("more digging") + "\n");
        var failing = j.Index(path => path == locked ? throw new UnauthorizedAccessException("Permission denied") : SessionCatalog.OpenSummary(path));
        var partial = await Search(failing, j, "badger", locked, fine);
        Assert.Equal(new[] { locked, fine }, partial.Matches.Select(m => m.Path));
        var error = Assert.Single(partial.Errors);
        Assert.Equal(locked, error.Path);
        Assert.Contains("Permission denied", error.Message);
        // Read again once it can be
        Assert.Equal(locked, Assert.Single((await Search(j.Index(), j, "digging", locked, fine)).Matches).Path);
    }

    [Fact]
    public async Task Another_root_never_answers_from_this_ones_index()
    {
        var j = new Journals();
        var file = j.Add("one", User("a private ferret"));
        var index = j.Index();
        Assert.Single((await Search(index, j, "ferret", file)).Matches);
        var otherRoot = Directory.CreateDirectory(Path.Combine(j.Directory, "other-root")).FullName;
        Assert.Empty((await index.SearchAsync(otherRoot, [Journals.Summary(file)], "ferret")).Matches);
    }
}
