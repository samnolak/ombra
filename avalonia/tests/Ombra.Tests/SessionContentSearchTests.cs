using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ombra.App;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>The sidebar's search also finds sessions by what was said in them: those rows come under the title matches
/// with the words around the match, typing cancels the search before, and a failing search never empties the list.</summary>
public sealed class SessionContentSearchTests
{
    private static void Frames(int n = 3)
    {
        for (var i = 0; i < n; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Frames(1);
            await Task.Delay(15);
        }
        Frames();
    }

    /// <summary>A saved session as omp writes it: title, header, one user message and the reply.</summary>
    private static string WriteSession(string root, string cwd, string title, string question, string answer, DateTimeOffset at)
    {
        var id = Guid.NewGuid().ToString();
        var project = Path.Combine(root, "-" + string.Concat(cwd.Select(c => char.IsLetterOrDigit(c) ? c : '-')));
        Directory.CreateDirectory(project);
        var file = Path.Combine(project, id + ".jsonl");
        File.WriteAllLines(file, [
            JsonSerializer.Serialize(new { type = "title", v = 1, title }),
            JsonSerializer.Serialize(new { type = "session", version = 3, id, cwd }),
            JsonSerializer.Serialize(new { type = "message", message = new { role = "user", content = question, timestamp = at.ToUnixTimeMilliseconds() } }),
            JsonSerializer.Serialize(new
            {
                type = "message",
                message = new { role = "assistant", content = new object[] { new { type = "text", text = answer } }, timestamp = at.ToUnixTimeMilliseconds() + 1 },
            }),
        ]);
        return file;
    }

    private sealed record Setup(MainWindow Window, MainViewModel Vm, string Root, string Project, string ByTitle, string ByContent);

    /// <summary>Two sessions in the open project: one titled "Zebra notes", one that only mentions zebras inside.</summary>
    private static async Task<Setup> OpenAsync()
    {
        var root = TestProcesses.TempDir("content-search");
        var project = TestProcesses.TempDir("content-search-project");
        var now = DateTimeOffset.UtcNow;
        var byContent = WriteSession(root, project, "Weekend plans", "Where can we see a zebra crossing the river?", "At the reserve.", now.AddMinutes(-5));
        var byTitle = WriteSession(root, project, "Zebra notes", "Stripes", "Black and white.", now.AddMinutes(-20));
        var s = new SessionController(TestProcesses.FakeFactory("normal", root), new LaunchRequest(project));
        var vm = new MainViewModel(s, new AppArgs()) { ContentSearchDelay = TimeSpan.Zero };
        var w = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        vm.RequestCatalogRefresh();
        await Until(() => vm.Sessions.Any(r => r.Model.Path == byTitle) && vm.Sessions.Any(r => r.Model.Path == byContent), "listed");
        await vm.CatalogRefreshTask;
        Frames();
        return new(w, vm, root, project, byTitle, byContent);
    }

    private static SessionItemViewModel? Row(MainViewModel vm, string path) => vm.Sessions.FirstOrDefault(r => r.Model.Path == path);

    [AvaloniaFact]
    public async Task Sessions_found_in_their_messages_list_under_title_matches_with_the_match_and_open_at_it()
    {
        var (w, vm, _, _, byTitle, byContent) = await OpenAsync();
        vm.SessionFilter = "zebra";
        // Title matches at once, the message search after
        Assert.NotNull(Row(vm, byTitle));
        await Until(() => Row(vm, byContent) is { HasSnippet: true }, "the session found in its messages");
        Assert.False(vm.IsSearchingContent);
        var found = Row(vm, byContent)!;
        Assert.Equal("zebra", found.SnippetMatch);
        Assert.Equal("Where can we see a ", found.SnippetBefore);
        Assert.Equal(" crossing the river?", found.SnippetAfter);
        Assert.False(Row(vm, byTitle)!.HasSnippet);
        // Under the title match, though it is newer
        Assert.True(vm.Sessions.IndexOf(Row(vm, byTitle)!) < vm.Sessions.IndexOf(found));
        Assert.False(vm.ShowNoSessionMatch);

        // Opening it shows that session with the chat's find on the query
        await vm.OpenSessionCommand.ExecuteAsync(found);
        await Until(() => SessionCatalog.SamePath(vm.Session.SessionFile ?? vm.Session.CurrentLaunch.ResumeSessionFile, byContent), "the found session shown");
        await Until(() => vm.IsFindOpen && vm.FindQuery == "zebra", "find in chat on the match");

        // Clearing the search drops the snippets
        vm.SessionFilter = "";
        Frames();
        Assert.All(vm.Sessions, r => Assert.False(r.HasSnippet));
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_new_keystroke_cancels_the_message_search_before_it_and_pauses_are_waited_for()
    {
        var (w, vm, _, _, _, byContent) = await OpenAsync();
        var calls = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.SearchSessionContentAsync = async (root, sessions, query, ct) =>
        {
            lock (calls) calls.Add(query);
            if (query == "riv")
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { if (ct.IsCancellationRequested) cancelled.TrySetResult(); }
            }
            return new([new(byContent, "crossing the ", query, "")], []);
        };
        vm.SessionFilter = "riv";
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(vm.IsSearchingContent);
        Assert.False(vm.ShowNoSessionMatch); // no "no match" before the messages answered
        vm.SessionFilter = "river";
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Until(() => Row(vm, byContent) is { SnippetMatch: "river" }, "the newest query's matches");
        Assert.False(vm.IsSearchingContent);

        // While typing, only the query after the pause is searched
        vm.ContentSearchDelay = TimeSpan.FromMilliseconds(300);
        lock (calls) calls.Clear();
        vm.SessionFilter = "r";
        vm.SessionFilter = "re";
        vm.SessionFilter = "res";
        await Until(() => Row(vm, byContent) is { SnippetMatch: "res" }, "the paused query's matches");
        await vm.ContentSearchTask;
        // Only the query typed before the pause reached the search (a rescan may search it again, never "r" or "re")
        lock (calls) Assert.All(calls, q => Assert.Equal("res", q));
        lock (calls) Assert.NotEmpty(calls);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_failing_message_search_keeps_the_rows_and_says_what_could_not_be_searched()
    {
        var (w, vm, _, _, byTitle, byContent) = await OpenAsync();
        vm.SearchSessionContentAsync = (root, sessions, query, ct) => query == "zebra"
            ? Task.FromResult(new SessionContentSearchResult([new(byContent, "a ", "zebra", " crossing")], [new(byTitle, "Permission denied")]))
            : Task.FromException<SessionContentSearchResult>(new IOException("Disk unavailable"));
        vm.SessionFilter = "zebra";
        await Until(() => Row(vm, byContent) is { HasSnippet: true }, "partial results");
        Assert.True(vm.HasSessionSearchNotice);
        Assert.Contains("Permission denied", vm.SessionSearchNotice);
        Assert.NotNull(Row(vm, byTitle));

        vm.SessionFilter = "zebra ";
        vm.SessionFilter = "zebras";
        await Until(() => vm.SessionSearchNotice?.Contains("Disk unavailable") == true, "the failure said");
        // The last answer's rows stay listed; the list never empties because the search failed
        Assert.NotNull(Row(vm, byContent));
        Assert.False(vm.IsSearchingContent);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task An_edited_journal_is_searched_again_after_the_list_is_read_again()
    {
        var (w, vm, root, project, byTitle, byContent) = await OpenAsync();
        vm.SessionFilter = "pelican";
        await Until(() => !vm.IsSearchingContent, "first search");
        Assert.Null(Row(vm, byContent));
        File.AppendAllText(byContent, JsonSerializer.Serialize(new
        {
            type = "message",
            message = new { role = "user", content = "Did you see the pelican?", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
        }) + "\n");
        vm.RequestCatalogRefresh();
        await Until(() => Row(vm, byContent) is { SnippetMatch: "pelican" }, "the edited session found");
        Assert.Null(Row(vm, byTitle));
        await vm.DisposeAsync();
        w.Close();
    }
}
