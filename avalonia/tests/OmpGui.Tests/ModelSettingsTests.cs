using OmpGui.ClientCore;
using OmpGui.Rpc;

namespace OmpGui.Tests;

/// <summary>Models, thinking, approval mode (restart on the same session), recovery, sign-in, client settings.</summary>
public sealed class ModelSettingsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static async Task<SessionController> StartAsync(string scenario = "normal")
    {
        var s = new SessionController(TestProcesses.FakeFactory(scenario, TestProcesses.TempDir("ms-sessions")),
            new LaunchRequest(TestProcesses.TempDir("ms-project"), ApprovalMode: "write"));
        await s.StartAsync();
        return s;
    }

    private static async Task RunAsync(SessionController s, string text)
    {
        var before = s.Snapshot().MessagesCompleted;
        await s.PromptAsync(text);
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.MessagesCompleted > before, Wait, "run end");
    }

    [Fact]
    public async Task Lists_models_switches_model_and_thinking()
    {
        await using var s = await StartAsync();
        Assert.Equal(("fake/model", "medium"), (s.Snapshot().Model, s.Snapshot().ThinkingLevel));
        var models = await s.GetModelsAsync();
        Assert.Equal(["fake/model", "fake/vision"], models.Select(m => m.Key));
        Assert.True(models[0].Reasoning);
        Assert.True(models[1].AcceptsImages);

        await s.SetThinkingLevelAsync("high");
        Assert.Equal("high", s.Snapshot().ThinkingLevel);
        await s.SetModelAsync(models[1]);
        Assert.Equal("fake/vision", s.Snapshot().Model);
        Assert.Null(s.Snapshot().ThinkingLevel);

        await s.SetModelAsync(new OmpModel("fake", "missing", "Missing", false, false, 0));
        Assert.Equal("fake/vision", s.Snapshot().Model);
        Assert.Contains(s.Snapshot().Items, i => i is NoticeItem n && n.Text.StartsWith("Could not switch to fake/missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changing_the_approval_mode_restarts_omp_on_the_same_session()
    {
        await using var s = await StartAsync();
        await RunAsync(s, "before the change");
        var file = s.Snapshot().SessionFile;
        var pid = s.ProcessId;
        Assert.Equal("write", s.Snapshot().ApprovalMode);

        await s.SetApprovalModeAsync("always-ask");
        var after = s.Snapshot();
        Assert.NotEqual(pid, s.ProcessId);
        Assert.Equal("always-ask", after.ApprovalMode);
        Assert.Equal(file, after.SessionFile);
        Assert.Contains(after.Items, i => i is UserItem { Text: "before the change" });
        Assert.Equal(SessionPhase.Ready, after.Phase);
    }

    [Fact]
    public async Task Recover_after_a_crash_resumes_the_session()
    {
        await using var s = await StartAsync();
        await RunAsync(s, "remember me");
        var file = s.Snapshot().SessionFile;
        System.Diagnostics.Process.GetProcessById(s.ProcessId!.Value).Kill();
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Faulted, Wait, "fault");

        await s.RecoverAsync();
        var back = s.Snapshot();
        Assert.Equal(SessionPhase.Ready, back.Phase);
        Assert.Equal(file, back.SessionFile);
        Assert.Contains(back.Items, i => i is UserItem { Text: "remember me" });
        Assert.DoesNotContain(back.Items, i => i is NoticeItem { Level: NoticeLevel.Error });
        await RunAsync(s, "and continue");
    }

    [Fact]
    public async Task Sign_in_flow_shows_the_link_asks_for_the_code_and_completes()
    {
        await using var s = await StartAsync();
        var providers = await s.GetLoginProvidersAsync();
        Assert.False(Assert.Single(providers).Authenticated);

        var login = s.LoginAsync("fakeauth");
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.OpenUrl is not null && x.Dialogs.Count == 1, Wait, "link and code prompt");
        Assert.Equal("https://example.invalid/oauth?state=abc", snap.OpenUrl!.Url);
        Assert.Equal(DialogKind.Input, snap.Dialogs[0].Kind);
        Assert.True(await s.AnswerDialogAsync(snap.Dialogs[0].Id, new DialogAnswer.Value("1234")));
        Assert.True(await login.WaitAsync(Wait));
        Assert.True(Assert.Single(await s.GetLoginProvidersAsync()).Authenticated);
    }

    [Fact]
    public async Task Sign_out_removes_the_stored_account_once()
    {
        await using var s = await StartAsync();
        Assert.Empty(await s.GetLogoutAccountsAsync("fakeauth"));
        var login = s.LoginAsync("fakeauth");
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1, Wait, "code prompt");
        await s.AnswerDialogAsync(snap.Dialogs[0].Id, new DialogAnswer.Value("1234"));
        Assert.True(await login.WaitAsync(Wait));

        var account = Assert.Single(await s.GetLogoutAccountsAsync("fakeauth"));
        Assert.Equal((1L, "me@example.com", true), (account.CredentialId, account.Label, account.Active));
        Assert.Null(await s.LogoutAsync("fakeauth", account));
        Assert.False(Assert.Single(await s.GetLoginProvidersAsync()).Authenticated);
        var again = await Assert.ThrowsAsync<RpcCommandException>(() => s.LogoutAsync("fakeauth", account));
        Assert.Equal("Credential 1 is not stored for fakeauth", again.Error);
    }

    [Fact]
    public async Task A_cancelled_sign_in_is_reported_not_thrown()
    {
        await using var s = await StartAsync();
        var login = s.LoginAsync("fakeauth");
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1, Wait, "code prompt");
        await s.AnswerDialogAsync(snap.Dialogs[0].Id, new DialogAnswer.Cancelled());
        Assert.False(await login.WaitAsync(Wait));
        Assert.Contains(s.Snapshot().Items, i => i is NoticeItem n && n.Text.StartsWith("Sign-in to fakeauth failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Launch_request_sets_folder_resume_and_approval_mode()
    {
        var o = new OmpRuntimeOptions { Command = "omp", Profile = "harness", ApprovalMode = "write", WorkingDirectory = "/configured" };
        var plain = o.ToLaunchSpec();
        Assert.Equal(["--mode", "rpc-ui", "--approval-mode", "write"], plain.Arguments);
        Assert.Equal("/configured", plain.WorkingDirectory);
        Assert.Equal("harness", plain.Environment!["OMP_PROFILE"]);

        var spec = o.ToLaunchSpec(new LaunchRequest("/other", "/s/x.jsonl", "always-ask"));
        Assert.Equal(["--mode", "rpc-ui", "--approval-mode", "always-ask", "--resume", "/s/x.jsonl"], spec.Arguments);
        Assert.Equal("/other", spec.WorkingDirectory);
    }

    [Fact]
    public void Settings_store_keeps_external_edits_backs_up_and_refuses_broken_files()
    {
        var dir = TestProcesses.TempDir("settings");
        var path = Path.Combine(dir, "omp-gui.local.json");
        var store = new ClientSettingsStore(path);
        Assert.Null(store.Load().Command);

        store.Update(o => o with { Command = "omp", Theme = "dark" });
        Assert.False(File.Exists(store.BackupPath));
        Assert.Contains("\"command\": \"omp\"", File.ReadAllText(path));

        // Someone edits the file by hand; the next client update keeps that edit.
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"theme\": \"dark\"", "\"theme\": \"dark\", \"profile\": \"harness\""));
        store.Update(o => o with { LastWorkingDirectory = "/p" });
        var now = store.Load();
        Assert.Equal(("omp", "harness", "/p"), (now.Command, now.Profile, now.LastWorkingDirectory));
        Assert.Contains("\"profile\": \"harness\"", File.ReadAllText(store.BackupPath));

        // Environment keys keep their case; secrets are the user's to put there, never written by the client itself.
        store.Update(o => o with { Environment = new Dictionary<string, string?> { ["MY_VAR"] = "1" } });
        Assert.Contains("\"MY_VAR\"", File.ReadAllText(path));

        // Unknown keys and comments in a hand-written file: automatic writes keep the keys; the hand-written
        // version stays in .bak however many automatic writes follow.
        const string handWritten = "{\n  // mine\n  \"command\": \"omp\",\n  \"futureOption\": {\"a\": 1}\n}\n";
        File.WriteAllText(path, handWritten);
        store.Update(o => o with { LastWorkingDirectory = "/q" });
        store.Update(o => o with { LastWorkingDirectory = "/r" });
        Assert.Contains("\"futureOption\"", File.ReadAllText(path));
        Assert.Equal("/r", store.Load().LastWorkingDirectory);
        Assert.Equal(handWritten, File.ReadAllText(store.BackupPath));

        File.WriteAllText(path, "{ not json");
        Assert.Throws<InvalidDataException>(() => store.Update(o => o with { Theme = "light" }));
        Assert.Equal("{ not json", File.ReadAllText(path));
    }
}
