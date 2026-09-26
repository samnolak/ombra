using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>Sessions: catalog, new, switch in place, open across projects (restart with --resume), rename.</summary>
public sealed class SessionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static async Task RunAsync(SessionController s, string text)
    {
        var before = s.Snapshot().MessagesCompleted;
        await s.PromptAsync(text);
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.MessagesCompleted > before, Wait, "run end");
    }

    private static string[] UserTexts(SessionSnapshot s) => [.. s.Items.OfType<UserItem>().Select(u => u.Text)];

    [Fact]
    public async Task New_session_switch_back_and_catalog()
    {
        var root = TestProcesses.TempDir("sessions");
        var project = TestProcesses.TempDir("project-a");
        await using var s = new SessionController(TestProcesses.FakeFactory("normal", root), new LaunchRequest(project));
        await s.StartAsync();
        var first = s.Snapshot();
        Assert.NotNull(first.SessionFile);
        Assert.True(SessionCatalog.SamePath(project, first.Cwd));
        await RunAsync(s, "first session message");

        await s.NewSessionAsync();
        var second = s.Snapshot();
        Assert.NotEqual(first.SessionFile, second.SessionFile);
        Assert.Empty(second.Items);
        Assert.True(second.TranscriptEpoch > first.TranscriptEpoch);
        await RunAsync(s, "second session message");

        var catalog = await SessionCatalog.ScanAsync(SessionCatalog.SessionsRootOf(second.SessionFile)!);
        Assert.Equal(2, catalog.Count);
        var old = Assert.Single(catalog, c => c.Path == first.SessionFile);
        Assert.Equal("first session message", old.Title);
        Assert.True(SessionCatalog.SamePath(project, old.Cwd));

        var pid = s.ProcessId;
        await s.OpenSessionAsync(old.Path, old.Cwd);
        var back = s.Snapshot();
        Assert.Equal(pid, s.ProcessId); // same project: switched in place, no restart
        Assert.Equal(first.SessionFile, back.SessionFile);
        Assert.Equal(["first session message"], UserTexts(back));
        Assert.Equal(SessionPhase.Ready, back.Phase);
    }

    [Fact]
    public async Task Opening_a_session_of_another_project_restarts_omp_there_and_resumes_it()
    {
        var root = TestProcesses.TempDir("sessions");
        var projectA = TestProcesses.TempDir("project-a");
        var projectB = TestProcesses.TempDir("project-b");
        await using var s = new SessionController(TestProcesses.FakeFactory("normal", root), new LaunchRequest(projectB));
        await s.StartAsync();
        await RunAsync(s, "work in B");
        var inB = s.Snapshot();

        await s.OpenFolderAsync(projectA);
        var inA = s.Snapshot();
        Assert.True(SessionCatalog.SamePath(projectA, inA.Cwd));
        Assert.Empty(inA.Items);
        Assert.Equal(SessionPhase.Ready, inA.Phase);

        var pid = s.ProcessId;
        await s.OpenSessionAsync(inB.SessionFile!, inB.Cwd!);
        var resumed = s.Snapshot();
        Assert.NotEqual(pid, s.ProcessId);
        Assert.True(SessionCatalog.SamePath(projectB, resumed.Cwd));
        Assert.Equal(inB.SessionFile, resumed.SessionFile);
        Assert.Equal(["work in B"], UserTexts(resumed));
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid!.Value));
        await RunAsync(s, "more in B");
        Assert.Equal(["work in B", "more in B"], UserTexts(s.Snapshot()));
    }

    [Fact]
    public async Task Rename_is_saved_by_omp_and_shown_in_the_catalog()
    {
        var root = TestProcesses.TempDir("sessions");
        var project = TestProcesses.TempDir("project");
        await using var s = new SessionController(TestProcesses.FakeFactory("normal", root), new LaunchRequest(project));
        await s.StartAsync();
        await RunAsync(s, "hello");
        await s.RenameSessionAsync("Refactor the parser");
        var snap = s.Snapshot();
        Assert.Equal("Refactor the parser", snap.SessionName);
        var entry = Assert.Single(await SessionCatalog.ScanAsync(SessionCatalog.SessionsRootOf(snap.SessionFile)!));
        Assert.Equal("Refactor the parser", entry.Title);
    }

    [Fact]
    public async Task Changing_sessions_is_refused_while_a_run_is_active()
    {
        var root = TestProcesses.TempDir("sessions");
        var project = TestProcesses.TempDir("project");
        await using var s = new SessionController(TestProcesses.FakeFactory("slow-stream", root), new LaunchRequest(project));
        await s.StartAsync();
        var file = s.Snapshot().SessionFile;
        await s.PromptAsync("long");
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Running, Wait, "running");
        var pid = s.ProcessId;
        await s.NewSessionAsync();
        await s.OpenFolderAsync(TestProcesses.TempDir("other"));
        Assert.Equal(file, s.Snapshot().SessionFile);
        Assert.Equal(pid, s.ProcessId);
        Assert.Equal(SessionPhase.Running, s.Snapshot().Phase);
        Assert.Equal(2, s.Snapshot().Items.Count(i => i is NoticeItem { Text: "Stop the current run before changing sessions." }));
        await s.AbortAsync();
    }

    [Fact]
    public async Task A_missing_folder_or_session_file_keeps_the_current_omp()
    {
        var root = TestProcesses.TempDir("sessions");
        var project = TestProcesses.TempDir("project");
        await using var s = new SessionController(TestProcesses.FakeFactory("normal", root), new LaunchRequest(project));
        await s.StartAsync();
        var pid = s.ProcessId;
        await s.OpenFolderAsync(Path.Combine(project, "does-not-exist"));
        await s.OpenSessionAsync(Path.Combine(root, "gone.jsonl"), TestProcesses.TempDir("elsewhere"));
        Assert.Equal(pid, s.ProcessId);
        Assert.Equal(SessionPhase.Ready, s.Snapshot().Phase);
        Assert.Contains(s.Snapshot().Items, i => i is NoticeItem n && n.Text.StartsWith("Folder not found", StringComparison.Ordinal));
        Assert.Contains(s.Snapshot().Items, i => i is NoticeItem n && n.Text.StartsWith("Session file not found", StringComparison.Ordinal));
    }

    [Fact]
    public void SamePath_resolves_symbolic_links_like_omps_recorded_cwd()
    {
        // Regression (CI run 10, macOS): /var/folders is a link to /private/var/folders and omp records the latter.
        var real = TestProcesses.TempDir("real-dir");
        var link = real + "-link";
        try { Directory.CreateSymbolicLink(link, real); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Assert.Skip("cannot create symbolic links here: " + e.Message); }
        Assert.True(SessionCatalog.SamePath(link, real));
        Assert.True(SessionCatalog.SamePath(Path.Combine(link, "."), real + Path.DirectorySeparatorChar));
        Assert.False(SessionCatalog.SamePath(link, TestProcesses.TempDir("other-dir")));
        Assert.Equal(SessionCatalog.ResolveLinks(real), SessionCatalog.ResolveLinks(link));

        // A link whose target itself runs through a link (CI run 11–13 on macOS: the target starts with /var).
        var hop = TestProcesses.TempDir("hop");
        var chained = Path.Combine(hop, "via");
        try { Directory.CreateSymbolicLink(chained, link); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Assert.Skip("cannot create symbolic links here: " + e.Message); }
        Assert.True(SessionCatalog.SamePath(chained, real));
        Assert.Equal(SessionCatalog.ResolveLinks(real), SessionCatalog.ResolveLinks(chained));
    }

    [Fact]
    public void Catalog_parses_title_header_and_first_message_fallback()
    {
        const string withTitle = """
            {"type":"title","v":1,"title":"Named","pad":"   "}
            {"type":"session","version":3,"id":"abc","cwd":"/work/p"}
            {"type":"message","message":{"role":"user","content":[{"type":"text","text":"hello"}]}}

            """;
        var a = SessionCatalog.Parse(withTitle, "/s/p/a.jsonl", DateTime.UtcNow)!;
        Assert.Equal(("abc", "/work/p", "Named"), (a.Id, a.Cwd, a.Title));

        const string untitled = """
            {"type":"title","v":1,"title":""}
            {"type":"session","version":3,"id":"d","cwd":"/w"}
            {"type":"model_change","id":"x"}
            {"type":"message","message":{"role":"user","content":[{"type":"text","text":"first line\nsecond line"}]}}

            """;
        Assert.Equal("first line second line", SessionCatalog.Parse(untitled, "/s/p/b.jsonl", DateTime.UtcNow)!.Title);
        Assert.False(SessionCatalog.Parse(untitled, "/s/p/b.jsonl", DateTime.UtcNow)!.IsEmpty);
        Assert.True(SessionCatalog.Parse("""{"type":"title","title":""}""" + "\n" + """{"type":"session","id":"f","cwd":"/w"}""" + "\n", "/s/e.jsonl", DateTime.UtcNow)!.IsEmpty);
        Assert.Null(SessionCatalog.Parse("""{"type":"title","title":"no header"}""" + "\n", "/s/c.jsonl", DateTime.UtcNow));
        // A head cut in the middle of a line: the partial line is ignored, the rest still parses.
        Assert.Equal("New session", SessionCatalog.Parse("""{"type":"session","id":"e","cwd":"/w"}""" + "\n{\"type\":\"mess", "/s/d.jsonl", DateTime.UtcNow)!.Title);
    }
}
