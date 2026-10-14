using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>The window's omp processes (<see cref="SessionHost"/>): one per open chat, never two on one file, idle ones
/// closed over the limit (the one used longest ago first), never the shown one or one that may not go.</summary>
public sealed class SessionHostTests
{
    private static SessionController Chat(string? resume = null) =>
        new(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("host-sessions")), new LaunchRequest(TestProcesses.TempDir("host-project"), resume));

    [Fact]
    public async Task A_chat_being_resumed_is_found_by_its_file_before_omp_reports_it()
    {
        await using var host = new SessionHost(Chat());
        var file = Path.Combine(TestProcesses.TempDir("host-files"), "a.jsonl");
        var opened = host.Open(new LaunchRequest(null, file));
        Assert.Same(opened, host.Find(file));
        Assert.Null(host.Find(file + ".other"));
        Assert.Same(opened, host.Active);
    }

    [Fact]
    public async Task Over_the_limit_the_chat_used_longest_ago_that_may_go_closes()
    {
        var first = Chat();
        var busy = new HashSet<SessionController>();
        await using var host = new SessionHost(first, c => !busy.Contains(c)) { MaxProcesses = 2 };
        var closed = new List<SessionController>();
        host.Closed += closed.Add;
        busy.Add(first); // working: never closed
        var second = host.Open(new LaunchRequest());
        var third = host.Open(new LaunchRequest());
        Assert.Equal([second], closed);
        Assert.Equal([first, third], host.All);

        // The shown chat stays even when it is the only one that may go
        busy.Clear();
        busy.Add(first);
        host.Activate(first);
        host.MaxProcesses = 1;
        host.Trim();
        Assert.Equal([second, third], closed);
        Assert.Equal([first], host.All);
        Assert.Throws<InvalidOperationException>(() => { _ = host.CloseAsync(first); });
    }

    [Fact]
    public async Task The_spare_is_no_open_chat_and_serves_only_a_new_chat_launched_like_it()
    {
        await using var host = new SessionHost(Chat()) { MaxProcesses = 1 };
        var stopped = new List<SessionController>();
        host.Stopped += c => { lock (stopped) stopped.Add(c); };
        var spareFolder = TestProcesses.TempDir("host-chat");
        await host.PrepareSpareAsync(new LaunchRequest(spareFolder, ApprovalMode: "write"), CancellationToken.None);
        var spare = host.Spare!;
        Assert.Equal(SessionPhase.Ready, spare.Snapshot().Phase);
        Assert.DoesNotContain(spare, host.All); // not open, not counted against the limit

        // Another permission mode: dropped (its omp stops), and nothing is served
        Assert.Null(host.TakeSpare(new LaunchRequest(ApprovalMode: "always-ask")));
        Assert.Null(host.Spare);
        await TestProcesses.Eventually(() => { lock (stopped) return stopped.Contains(spare); }, x => x, TimeSpan.FromSeconds(10), "the stale spare stopped");

        // The same launch in another folder: the ready spare itself, shown as a new chat
        await host.PrepareSpareAsync(new LaunchRequest(TestProcesses.TempDir("host-chat"), ApprovalMode: "write"), CancellationToken.None);
        var ready = host.Spare!;
        Assert.Same(ready, host.TakeSpare(new LaunchRequest(TestProcesses.TempDir("host-chat-elsewhere"), ApprovalMode: "write")));
        Assert.Same(ready, host.Adopt(ready));
        Assert.Same(ready, host.Active);

        // A change to how omp starts: the spare started before it is not served
        await host.PrepareSpareAsync(new LaunchRequest(TestProcesses.TempDir("host-chat"), ApprovalMode: "write"), CancellationToken.None);
        var old = host.Spare!;
        host.InvalidateSpare();
        Assert.Null(host.Spare);
        await TestProcesses.Eventually(() => { lock (stopped) return stopped.Contains(old); }, x => x, TimeSpan.FromSeconds(10), "the old spare stopped");
    }
}
