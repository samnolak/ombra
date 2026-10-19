using System.Text.Json;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

/// <summary>omp's <c>fork</c> (from a reply, or the whole session) and host tools (<c>set_host_tools</c>, <c>host_tool_*</c>).</summary>
public sealed class ForkHostToolsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static (SessionController Session, string Log) Start(string scenario)
    {
        var project = TestProcesses.TempDir("fork-project");
        var sessions = TestProcesses.TempDir("fork-sessions");
        var log = Path.Combine(project, "commands.log");
        var factory = TestProcesses.FakeFactory(scenario, sessions);
        var s = new SessionController(r =>
        {
            var spec = factory(r);
            return spec with { Environment = new Dictionary<string, string?>(spec.Environment) { ["FAKE_OMP_COMMAND_LOG"] = log, ["FAKE_OMP_HOST_TOOLS_LOG"] = log } };
        }, new LaunchRequest(project));
        return (s, log);
    }

    private static string Log(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    private static async Task PromptAndWaitAsync(SessionController s, string text, int replies)
    {
        await s.PromptAsync(text);
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.Items.OfType<AssistantItem>().Count(a => !a.Streaming) == replies, Wait, "reply " + replies);
    }

    private static JsonElement Entries(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void The_fork_point_is_the_message_on_the_current_path_by_time_else_by_place_from_the_end()
    {
        // e1 was abandoned by a rewind: the path is e0 → e2 → e3 → e4
        var data = Entries("""
            {"leafId":"e4","entries":[
              {"type":"message","id":"e0","parentId":null,"message":{"role":"user","content":[{"type":"text","text":"hi"}],"timestamp":1000}},
              {"type":"message","id":"e1","parentId":"e0","message":{"role":"assistant","content":[{"type":"text","text":"same"}],"timestamp":1500}},
              {"type":"message","id":"e2","parentId":"e0","message":{"role":"assistant","content":[{"type":"text","text":"same"}],"timestamp":2000}},
              {"type":"label","id":"l1","parentId":"e2"},
              {"type":"message","id":"e3","parentId":"l1","message":{"role":"user","content":"again","timestamp":3000}},
              {"type":"message","id":"e4","parentId":"e3","message":{"role":"assistant","content":[{"type":"thinking","thinking":"x"},{"type":"text","text":"same"}],"timestamp":4000}}
            ]}
            """);
        var path = ForkPoints.ActivePath(data);
        Assert.Equal(["e0", "e2", "e3", "e4"], path.Select(p => p.Id));
        // The row's own time picks the message, wherever it is
        Assert.Equal("e2", ForkPoints.Find(path, "assistant", "same", DateTimeOffset.FromUnixTimeMilliseconds(2000), occurrenceFromEnd: 0));
        // Without a matching time: the same place counted from the end
        Assert.Equal("e4", ForkPoints.Find(path, "assistant", "same", DateTimeOffset.FromUnixTimeMilliseconds(99), 0));
        Assert.Equal("e2", ForkPoints.Find(path, "assistant", "same", null, 1));
        Assert.Null(ForkPoints.Find(path, "assistant", "same", null, 2));
        Assert.Null(ForkPoints.Find(path, "assistant", "hi", null, 0)); // the role counts
        Assert.Equal("e3", ForkPoints.Find(path, "user", "again\n", null, 0));

        IReadOnlyList<TranscriptItem> items =
        [
            new UserItem(1, "hi", true), new AssistantItem(2, "same", "", false, "stop", null),
            new UserItem(3, "again", true), new AssistantItem(4, "same", "", false, "stop", null),
            new AssistantItem(5, "", "", true, null, null),
        ];
        Assert.Equal(("assistant", "same", (DateTimeOffset?)null, 1), ForkPoints.Target(items, 2));
        Assert.Null(ForkPoints.Target(items, 5)); // still streaming
        Assert.Null(ForkPoints.Target(items, 42));
    }

    [Fact]
    public async Task Fork_from_a_reply_goes_on_in_a_new_session_that_ends_there()
    {
        var (s, log) = Start("normal");
        await using var _ = s;
        await s.StartAsync();
        await PromptAndWaitAsync(s, "first question", 1);
        await PromptAndWaitAsync(s, "second question", 2);
        var original = s.SessionFile;
        // Both replies have the same text: the first one is told apart by its place
        var first = s.Snapshot().Items.OfType<AssistantItem>().First();

        Assert.Equal(ForkOutcome.Forked, await s.ForkAsync(first.Key));
        Assert.Contains("fork e1\n", Log(log));
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.SessionFile != original, Wait, "forked session shown");
        Assert.Equal(["first question"], snap.Items.OfType<UserItem>().Select(u => u.Text));
        Assert.Single(snap.Items.OfType<AssistantItem>());
        Assert.True(File.Exists(original)); // the original stays as it was

        // The session menu's Fork: all of it, in another new file
        var forked = s.SessionFile;
        Assert.Equal(ForkOutcome.Forked, await s.ForkAsync());
        Assert.Contains("fork (latest)\n", Log(log));
        snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.SessionFile != forked, Wait, "second fork shown");
        Assert.Equal(["first question"], snap.Items.OfType<UserItem>().Select(u => u.Text));
    }

    [Fact]
    public async Task A_busy_omp_refuses_the_fork_and_the_chat_stays_where_it_was()
    {
        var (s, log) = Start("fork-busy");
        await using var _ = s;
        await s.StartAsync();
        await PromptAndWaitAsync(s, "question", 1);
        var original = s.SessionFile;

        Assert.Equal(ForkOutcome.Busy, await s.ForkAsync(s.Snapshot().Items.OfType<AssistantItem>().Single().Key));
        var snap = s.Snapshot();
        Assert.Equal(original, snap.SessionFile);
        Assert.Equal(SessionPhase.Ready, snap.Phase);
        Assert.DoesNotContain(snap.Items, i => i is NoticeItem { Level: NoticeLevel.Error });
        Assert.DoesNotContain("fork ", Log(log));
        Assert.Contains(snap.DebugTail, d => d.Type == "fork" && d.Summary.StartsWith("refused", StringComparison.Ordinal)); // omp's session_busy, not a local guess
        Assert.Equal(ForkOutcome.NotFound, await s.ForkAsync(424242));
    }

    private static HostTool Echo(Func<JsonElement, CancellationToken, Task<HostToolResult>> run) =>
        new(new HostToolDefinition("terminal_read", "Read terminal", "test", """{"type":"object","properties":{}}"""), run);

    [Fact]
    public async Task Host_tools_are_registered_on_every_start_and_answer_omps_calls()
    {
        var (s, log) = Start("normal");
        await using var _ = s;
        var calls = 0;
        s.HostTools = [Echo((_, _) => Task.FromResult(new HostToolResult($"screen line {Interlocked.Increment(ref calls)}")))];
        await s.StartAsync();
        Assert.Contains("set_host_tools terminal_read\n", Log(log));

        await PromptAndWaitAsync(s, "TERMREAD what does it say", 1);
        Assert.Contains(s.Snapshot().Items.OfType<AssistantItem>(), a => a.Text.Contains("terminal says: screen line 1", StringComparison.Ordinal));
        Assert.Contains(s.Snapshot().Items.OfType<ToolItem>(), t => t.Name == "terminal_read" && t.Status == ToolStatus.Succeeded);

        // A restart is a new connection: the tools are registered again before anything runs
        await s.RestartAsync(s.CurrentLaunch with { ResumeSessionFile = s.SessionFile });
        Assert.Equal(2, Log(log).Split('\n').Count(l => l == "set_host_tools terminal_read"));
        await s.PromptAsync("TERMREAD again");
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready
            && x.Items.OfType<AssistantItem>().Any(a => a.Text.Contains("terminal says: screen line 2", StringComparison.Ordinal)), Wait, "second read after the restart");
    }

    [Fact]
    public async Task An_aborted_turn_cancels_the_running_host_tool()
    {
        var (s, _) = Start("normal");
        await using var __ = s;
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        s.HostTools = [Echo(async (_, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return new HostToolResult("never");
        })];
        await s.StartAsync();
        await s.PromptAsync("TERMREAD and wait");
        await started.Task.WaitAsync(Wait);
        await s.AbortAsync();
        await cancelled.Task.WaitAsync(Wait);
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "turn ended");
        Assert.Contains(snap.DebugTail, d => d.Type == "host_tool_cancel");
        Assert.DoesNotContain(snap.Items.OfType<AssistantItem>(), a => a.Text.Contains("never", StringComparison.Ordinal));
    }
}
