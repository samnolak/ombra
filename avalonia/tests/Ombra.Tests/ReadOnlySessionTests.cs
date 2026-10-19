using System.Text.Json;
using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class ReadOnlySessionTests
{
    private static string Message(int index) => JsonSerializer.Serialize(new
    {
        type = "message", id = $"m{index}", parentId = index == 0 ? null : $"m{index - 1}",
        timestamp = "2026-01-01T00:00:00Z",
        message = new { role = "user", content = $"saved {index}", timestamp = index },
    }) + "\n";

    [Fact]
    public async Task Read_only_pages_without_opening_a_logical_session_and_never_replays_input()
    {
        var root = TestProcesses.TempDir("read-only");
        var file = Path.Combine(root, "saved.jsonl");
        var missing = Path.Combine(root, "deleted-project");
        var log = Path.Combine(root, "logical-opens.jsonl");
        var original = JsonSerializer.Serialize(new { type = "session", version = 3, id = "saved", cwd = missing,
            timestamp = "2026-01-01T00:00:00Z" }) + "\n" + string.Concat(Enumerable.Range(0, 205).Select(Message));
        await File.WriteAllTextAsync(file, original);
        var factory = TestProcesses.FakeFactory("normal", Path.Combine(root, "sessions"));
        await using var reader = new SessionController(request =>
        {
            var spec = factory(request);
            return spec with { Environment = new Dictionary<string, string?>(spec.Environment) { ["FAKE_OMP_SESSION_LOG"] = log } };
        }, new LaunchRequest(missing, file) { ReadOnly = true });
        var preparations = 0;
        var checkpoints = 0;
        reader.PrepareLaunch = _ => preparations++;
        reader.QueueCheckpoint = _ => checkpoints++;
        await reader.StartAsync();
        var initial = reader.Snapshot();
        Assert.True(initial.IsReadOnly);
        Assert.Equal(SessionPhase.Stopped, initial.Phase);
        Assert.Null(reader.Connection);
        Assert.Null(reader.ProcessId);
        Assert.Equal(file, initial.SessionFile);
        Assert.Equal(missing, initial.Cwd);
        Assert.Equal(100, initial.Items.OfType<UserItem>().Count());
        Assert.Equal(205, initial.History.TotalMessages);
        Assert.True(initial.History.HasOlder);
        await reader.LoadOlderMessagesAsync();
        await reader.LoadOlderMessagesAsync();
        Assert.Equal(205, reader.Snapshot().Items.OfType<UserItem>().Count());
        Assert.False(reader.Snapshot().History.HasOlder);
        await Assert.ThrowsAsync<OmpNotRunningException>(() => reader.PromptAsync("must not send"));
        await Assert.ThrowsAsync<OmpNotRunningException>(() => reader.QueueAsync(QueueKind.FollowUp, "must not queue", []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.RestartAsync(new LaunchRequest(root, file)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.RecoverAsync());
        Assert.False(await reader.RunShellCommandAsync("touch must-not-exist"));
        Assert.Empty(reader.Snapshot().Queued);
        Assert.Equal(0, preparations);
        Assert.Equal(0, checkpoints);
        Assert.False(File.Exists(log));
        Assert.False(Directory.Exists(missing));
        Assert.Equal(original, await File.ReadAllTextAsync(file));

        await reader.ReloadHistoryAsync();
        var kept = reader.Snapshot().Items.OfType<UserItem>().Select(i => i.Text).ToArray();
        await File.AppendAllTextAsync(file, Message(205));
        await reader.LoadOlderMessagesAsync();
        Assert.True(reader.Snapshot().History.NeedsReload);
        Assert.Equal(kept, reader.Snapshot().Items.OfType<UserItem>().Select(i => i.Text));
        await reader.ReloadHistoryAsync();
        Assert.False(reader.Snapshot().History.NeedsReload);
        Assert.Equal(206, reader.Snapshot().History.TotalMessages);
        Assert.Equal("saved 205", reader.Snapshot().Items.OfType<UserItem>().Last().Text);
        Assert.Equal(original + Message(205), await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Missing_source_and_cancellation_do_not_create_a_logical_chat()
    {
        var root = TestProcesses.TempDir("read-only-errors");
        var file = Path.Combine(root, "missing.jsonl");
        await using var reader = new SessionController(TestProcesses.FakeFactory("normal", root),
            new LaunchRequest(root, file) { ReadOnly = true });
        await Assert.ThrowsAsync<Ombra.Rpc.RpcCommandException>(() => reader.StartAsync());
        Assert.True(reader.Snapshot().IsReadOnly);
        Assert.Equal(SessionPhase.Faulted, reader.Snapshot().Phase);
        Assert.Null(reader.Connection);
        Assert.False(File.Exists(file));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.StartAsync(cancel.Token));
        Assert.Null(reader.Connection);
    }

    [Fact]
    public async Task Hosted_reader_preserves_the_live_chat_connection_and_private_transcript()
    {
        var root = TestProcesses.TempDir("read-only-peer");
        var file = Path.Combine(root, "saved.jsonl");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { type = "session", version = 3,
            id = "saved", cwd = Path.Combine(root, "deleted"), timestamp = "2026-01-01T00:00:00Z" }) + "\n" + Message(0));
        var live = new SessionController(TestProcesses.FakeFactory("slow-stream", root), new LaunchRequest(root));
        await using var host = new SessionHost(live);
        await live.StartAsync();
        await live.PromptAsync("keep working");
        var connection = live.Connection;
        var pid = host.EngineProcessId;
        var reader = host.Open(new LaunchRequest(Path.Combine(root, "deleted"), file) { ReadOnly = true });
        await reader.StartAsync();
        Assert.Same(connection, live.Connection);
        Assert.Equal(pid, host.EngineProcessId);
        Assert.Null(reader.Connection);
        Assert.Equal("saved 0", Assert.Single(reader.Snapshot().Items.OfType<UserItem>()).Text);
        Assert.Contains(live.Snapshot().Items.OfType<UserItem>(), row => row.Text == "keep working");
        host.Activate(live);
        await host.CloseAsync(reader);
        Assert.Same(connection, live.Connection);
        Assert.Null(connection!.CloseReason);
    }
}
