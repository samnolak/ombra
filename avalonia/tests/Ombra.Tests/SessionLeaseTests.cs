using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

/// <summary>
/// Session ownership over the fake engine: a chat another window or a terminal omp has open is shown read-only
/// (<c>session_lease_held</c>), "Open here" takes it over (<c>engine_take_session</c>), and a chat taken over from here
/// turns read-only (<c>session_lease_lost</c>) without counting as a crash.
/// </summary>
public sealed class SessionLeaseTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    internal sealed record Fixture(string Root, string Cwd, string File, string Log, Func<LaunchRequest, OmpLaunchSpec> Factory)
    {
        public void Hold(string kind = "omp", int pid = 4242) =>
            System.IO.File.WriteAllText(File + ".fakelease", $$"""{"id":"h1","kind":"{{kind}}","pid":{{pid}},"since":"2026-10-17T10:00:00.000Z"}""");
        public void Stick() => System.IO.File.WriteAllText(File + ".fakelease-stuck", "");
        public string[] Takes => System.IO.File.Exists(Log)
            ? [.. System.IO.File.ReadAllLines(Log).Where(l => l.Contains("engine_take_session", StringComparison.Ordinal))] : [];
    }

    internal static Fixture Create(string scenario = "normal")
    {
        var root = TestProcesses.TempDir("lease-sessions");
        var cwd = Path.Combine(TestProcesses.TempDir("lease"), "weather-cli");
        Directory.CreateDirectory(cwd);
        var file = ReadmeScreenshotTests.Write(root, cwd, "Add a --units flag to forecast", ReadmeScreenshotTests.Conversation(), DateTime.UtcNow.AddMinutes(-2));
        var log = Path.Combine(root, "engine.log");
        var spec = TestProcesses.Fake(scenario);
        return new Fixture(root, cwd, file, log, req => spec with
        {
            SessionArguments = [.. spec.SessionArguments, .. (req.ResumeSessionFile is { } r ? new[] { "--resume", r } : [])],
            WorkingDirectory = req.WorkingDirectory,
            Environment = new Dictionary<string, string?> { ["FAKE_SESSION_DIR"] = root, ["FAKE_MODEL"] = "local/coder", ["FAKE_OMP_SESSION_LOG"] = log },
        });
    }

    [Fact]
    public void The_holder_is_read_from_the_typed_error()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"sessionId":"s","holder":{"id":"x","kind":"ombra","pid":77,"label":"Ombra","since":"2026-10-17T10:00:00.000Z"}}""");
        var e = new OmpStartException("held", null, "", code: "session_lease_held", data: doc.RootElement.Clone());
        Assert.Equal(new OmpSessionLeaseHolder("ombra", 77, "Ombra", DateTimeOffset.Parse("2026-10-17T10:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture)), e.LeaseHolder);
        Assert.Null(new OmpStartException("other", null, "", code: "session_model_unavailable", data: doc.RootElement.Clone()).LeaseHolder);
        using var unknown = System.Text.Json.JsonDocument.Parse("""{"sessionId":"s","holder":null}""");
        Assert.Null(new OmpStartException("held", null, "", code: "session_lease_held", data: unknown.RootElement.Clone()).LeaseHolder);
    }

    [Fact]
    public async Task A_chat_open_elsewhere_is_shown_read_only_and_Open_here_takes_it_over()
    {
        var f = Create();
        f.Hold("omp", 4242);
        await using var s = new SessionController(f.Factory, new LaunchRequest(f.Cwd, ResumeSessionFile: f.File));
        await s.StartAsync();
        var held = s.Snapshot();
        Assert.Equal(SessionPhase.Stopped, held.Phase);
        Assert.False(held.StartFailed);
        Assert.Null(held.AutoRecover);
        Assert.Equal(new SessionLease(SessionLeaseState.Held, new OmpSessionLeaseHolder("omp", 4242, null, held.Lease!.Holder!.Since)), held.Lease);
        Assert.Null(s.Connection);
        // The saved conversation, read from its file
        Assert.Contains(held.Items, i => i is UserItem u && u.Text.StartsWith("Add a --units flag", StringComparison.Ordinal));
        Assert.Equal(f.File, held.SessionFile);

        Assert.True(await s.TakeOverSessionAsync());
        var open = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "opened here");
        Assert.Null(open.Lease);
        Assert.NotNull(s.Connection);
        Assert.Contains(f.File, Assert.Single(f.Takes));
        Assert.False(File.Exists(f.File + ".fakelease"));
    }

    [Fact]
    public async Task A_holder_that_does_not_let_go_keeps_the_chat_read_only_with_the_reason()
    {
        var f = Create();
        f.Hold("ombra", 977);
        f.Stick();
        await using var s = new SessionController(f.Factory, new LaunchRequest(f.Cwd, ResumeSessionFile: f.File));
        await s.StartAsync();
        Assert.False(await s.TakeOverSessionAsync());
        var snap = s.Snapshot();
        Assert.Equal(SessionLeaseState.Held, snap.Lease!.State);
        Assert.False(snap.Lease.IsTakingOver);
        Assert.Equal("It is still open there and did not let go. Close it there, then try again.", snap.Lease.Error);
        Assert.Equal(("ombra", 977), (snap.Lease.Holder!.Kind, snap.Lease.Holder.Pid));
        Assert.Equal(SessionPhase.Stopped, snap.Phase);
        Assert.True(File.Exists(f.File + ".fakelease")); // never taken by force
    }

    [Fact]
    public async Task Switching_to_a_chat_open_elsewhere_shows_it_read_only()
    {
        var f = Create();
        await using var s = new SessionController(f.Factory, new LaunchRequest(f.Cwd));
        await s.StartAsync();
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "ready");
        f.Hold("omp", 31337);
        await s.OpenSessionAsync(f.File, f.Cwd);
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Lease is not null, Wait, "held");
        Assert.Equal((SessionLeaseState.Held, 31337), (snap.Lease!.State, snap.Lease.Holder!.Pid));
        Assert.Equal(f.File, snap.SessionFile);
        Assert.Equal(SessionPhase.Stopped, snap.Phase);
    }

    [Fact]
    public async Task A_chat_taken_over_elsewhere_turns_read_only_without_a_crash()
    {
        var f = Create();
        await using var s = new SessionController(f.Factory, new LaunchRequest(f.Cwd, ResumeSessionFile: f.File));
        await s.StartAsync();
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "ready");
        try { await s.Connection!.RequestAsync("test_lease_lost", w => { w.WriteString("kind", "ombra"); w.WriteNumber("pid", 5150); w.WriteBoolean("interrupted", true); }); }
        catch (RpcConnectionClosedException) { }
        var lost = await TestProcesses.Eventually(s.Snapshot, x => x.Lease is not null && x.Phase == SessionPhase.Stopped, Wait, "lease lost");
        Assert.Equal((SessionLeaseState.Lost, "ombra", 5150), (lost.Lease!.State, lost.Lease.Holder!.Kind, lost.Lease.Holder.Pid));
        Assert.Null(lost.AutoRecover); // not a crash: nothing restarts it behind the other window's back
        Assert.Null(lost.LastError);
        Assert.Contains(lost.Items, i => i is UserItem u && u.Text.StartsWith("Add a --units flag", StringComparison.Ordinal)); // the conversation stays
        // ReviewLease P2: a reply stopped by the takeover is said to be stopped, never left looking finished.
        Assert.Contains(lost.Items, i => i is NoticeItem { Text: var text } && text.StartsWith("The reply in progress was stopped", StringComparison.Ordinal));
        await Task.Delay(500);
        Assert.Equal(SessionPhase.Stopped, s.Snapshot().Phase);

        Assert.True(await s.TakeOverSessionAsync());
        var open = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.Lease is null, Wait, "opened here again");
        Assert.Equal(f.File, open.SessionFile);
    }
}
