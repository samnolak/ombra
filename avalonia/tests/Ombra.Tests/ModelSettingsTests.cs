using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

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
        Assert.Equal(pid, s.ProcessId);
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
    public void Launch_request_sets_folder_resume_approval_mode_and_thinking()
    {
        var o = new OmpRuntimeOptions { Command = "bun", PrefixArgs = ["host.ts"], ManagementPrefixArgs = ["cli.ts"], Profile = "harness", ApprovalMode = "write", WorkingDirectory = "/configured" };
        var plain = o.ToLaunchSpec();
        // A new session starts at medium, not omp's own default (high)
        Assert.Equal(["host.ts"], plain.Arguments);
        Assert.Equal(["--approval-mode", "write", "--thinking", "medium"], plain.SessionArguments);
        Assert.Equal(["cli.ts", "plugin", "list"], o.ToCliLaunchSpec(["plugin", "list"]).Arguments);
        Assert.Empty(o.ToCliLaunchSpec(["plugin", "list"]).SessionArguments);
        Assert.Equal("cli.ts", o.ToTuiLaunchSpec("/configured").Arguments[0]);
        Assert.Equal("/configured", plain.WorkingDirectory);
        Assert.Equal("harness", plain.Environment!["OMP_PROFILE"]);

        // A resumed session keeps the level saved with it
        var spec = o.ToLaunchSpec(new LaunchRequest("/other", "/s/x.jsonl", "always-ask"));
        Assert.Equal(["host.ts"], spec.Arguments);
        Assert.Equal(["--approval-mode", "always-ask", "--resume", "/s/x.jsonl"], spec.SessionArguments);
        Assert.Equal("/other", spec.WorkingDirectory);

        // The settings file's level when omp knows it; the user's own --thinking wins
        Assert.Equal("high", (o with { DefaultThinkingLevel = "high" }).ToLaunchSpec().SessionArguments[^1]);
        Assert.Equal("medium", (o with { DefaultThinkingLevel = "turbo" }).ToLaunchSpec().SessionArguments[^1]);
        Assert.Equal(["--approval-mode", "write", "--thinking", "low"],
            (o with { ExtraArgs = ["--thinking", "low"] }).ToLaunchSpec().SessionArguments);
    }

    [Fact]
    public void Settings_store_keeps_external_edits_backs_up_and_refuses_broken_files()
    {
        var dir = TestProcesses.TempDir("settings");
        var path = Path.Combine(dir, "ombra.local.json");
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

    [Fact]
    public void Settings_store_uses_defaults_for_invalid_fields_and_preserves_them_until_edited()
    {
        var path = Path.Combine(TestProcesses.TempDir("settings-invalid-field"), "ombra.local.json");
        const string json = """{"command":"omp","profile":"harness","theme":42,"futureOption":true}""";
        File.WriteAllText(path, json);
        var store = new ClientSettingsStore(path);

        var options = store.Load();
        Assert.Equal("omp", options.Command);
        Assert.Equal("harness", options.Profile);
        Assert.Null(options.Theme);
        Assert.Equal(["theme"], store.InvalidFields);

        store.Update(o => o with { LastWorkingDirectory = "/project" });
        Assert.Contains("\"theme\": 42", File.ReadAllText(path));
        Assert.Contains("\"futureOption\": true", File.ReadAllText(path));
        Assert.Equal("/project", store.Load().LastWorkingDirectory);
        Assert.Equal(json, File.ReadAllText(store.BackupPath));
        store.Update(o => o with { Theme = "light" });
        Assert.Empty(store.InvalidFields);
        Assert.Equal("light", store.Load().Theme);
    }

    [Fact]
    public void Thinking_level_round_trips_without_serializing_computed_settings()
    {
        var path = Path.Combine(TestProcesses.TempDir("settings-thinking"), "ombra.local.json");
        var store = new ClientSettingsStore(path);
        store.Update(o => o with { DefaultThinkingLevel = "high" });
        Assert.Equal("high", store.Load().EffectiveThinkingLevel);
        Assert.Equal("high", new ClientSettingsStore(path).Load().EffectiveThinkingLevel);
        Assert.Equal("high", OmpRuntimeOptions.Load(path).ToLaunchSpec().SessionArguments[^1]);
        Assert.DoesNotContain("effectiveThinkingLevel", File.ReadAllText(path));
        Assert.DoesNotContain("approvalModeWarning", File.ReadAllText(path));

        // Older versions emitted computed properties. They are harmless unknown raw data now.
        File.WriteAllText(path, """{"defaultThinkingLevel":"low","effectiveThinkingLevel":"high","approvalModeWarning":{"private":"keep"}}""");
        Assert.Equal("low", store.Load().EffectiveThinkingLevel);
        Assert.Empty(store.InvalidFields);
        store.Update(o => o with { Theme = "dark" });
        Assert.Contains("\"effectiveThinkingLevel\": \"high\"", File.ReadAllText(path));
        Assert.Equal("low", store.Load().EffectiveThinkingLevel);
    }

    [Fact]
    public void Null_required_collections_use_defaults_and_launch_without_losing_raw_values()
    {
        var path = Path.Combine(TestProcesses.TempDir("settings-null"), "ombra.local.json");
        const string json = """{"command":"omp","prefixArgs":null,"managementPrefixArgs":null,"extraArgs":null,"environment":null,"notifications":"private-invalid-value","theme":"dark"}""";
        File.WriteAllText(path, json);
        var store = new ClientSettingsStore(path);
        var options = store.Load();
        Assert.Equal(["environment", "extraArgs", "managementPrefixArgs", "notifications", "prefixArgs"], store.InvalidFields);
        Assert.Empty(options.PrefixArgs);
        Assert.Empty(options.ManagementPrefixArgs);
        Assert.Empty(options.ExtraArgs);
        Assert.Empty(options.Environment);
        Assert.Null(options.Notifications);
        Assert.Equal("dark", options.Theme);
        Assert.Equal("omp", options.ToLaunchSpec().FileName);
        Assert.Equal("omp", options.ToTuiLaunchSpec(null).FileName);
        Assert.Equal("omp", options.ToCliLaunchSpec(["plugin", "list"]).FileName);
        Assert.Empty(OmpRuntimeOptions.Load(path).Environment);

        store.Update(o => o with { DefaultThinkingLevel = "high" });
        Assert.Equal(json, File.ReadAllText(store.BackupPath));
        Assert.Contains("\"environment\": null", File.ReadAllText(path));
        Assert.Contains("private-invalid-value", File.ReadAllText(path));
        Assert.DoesNotContain("private-invalid-value", string.Join(", ", store.InvalidFields));
    }

    [Fact]
    public void Read_only_directory_keeps_changes_in_memory_then_merges_external_edits_on_recovery()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix directory permissions; Windows persistence failures are covered by the backup failure regression."); return; }
        var dir = TestProcesses.TempDir("settings-read-only");
        var path = Path.Combine(dir, "ombra.local.json");
        const string original = """{"command":"omp","theme":"dark","futureOption":{"keep":true}}""";
        File.WriteAllText(path, original);
        var mode = File.GetUnixFileMode(dir);
        var store = new ClientSettingsStore(path);
        var notifications = 0;
        store.WarningsChanged += () => notifications++;
        try
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.Equal("dark", store.Load().Theme);
            if (!store.IsPersistenceUnavailable) { Assert.Skip("The test account bypasses directory write permissions."); return; }
            Assert.Equal(1, notifications);
            Assert.Equal("high", store.Update(o => o with { DefaultThinkingLevel = "high" }).EffectiveThinkingLevel);
            store.Update(o => o with { Theme = "light" });
            Assert.Equal(("high", "light"), (store.Load().EffectiveThinkingLevel, store.Load().Theme));
            Assert.Equal(original, File.ReadAllText(path));
            Assert.Equal("dark", new ClientSettingsStore(path).Load().Theme);
            Assert.False(File.Exists(store.BackupPath));
        }
        finally { File.SetUnixFileMode(dir, mode); }

        // An external valid edit after permissions recover must not be replaced by a stale snapshot.
        const string external = """{"command":"custom-omp","theme":"dark","model":"external/model","futureOption":{"keep":false}}""";
        File.WriteAllText(path, external);
        var loaded = store.Load();
        Assert.Equal(("custom-omp", "external/model", "light", "high"), (loaded.Command, loaded.Model, loaded.Theme, loaded.EffectiveThinkingLevel));
        Assert.True(store.IsPersistenceUnavailable); // Nothing has saved the pending changes yet.
        store.Update(o => o);
        Assert.False(store.IsPersistenceUnavailable);
        Assert.Equal(2, notifications);
        Assert.Equal(external, File.ReadAllText(store.BackupPath));
        var restarted = new ClientSettingsStore(path).Load();
        Assert.Equal(("custom-omp", "external/model", "light", "high"), (restarted.Command, restarted.Model, restarted.Theme, restarted.EffectiveThinkingLevel));
        Assert.Contains("\"keep\": false", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        Assert.Empty(Directory.GetFiles(dir, "*.probe-*"));
    }

    [Fact]
    public void Explicit_missing_runtime_settings_remain_an_error()
    {
        var path = Path.Combine(TestProcesses.TempDir("settings-missing-explicit"), "missing.json");
        Assert.Throws<FileNotFoundException>(() => OmpRuntimeOptions.Load(path));
    }

    [Fact]
    public void Failed_backup_retains_pending_edits_and_still_refuses_malformed_external_json()
    {
        var path = Path.Combine(TestProcesses.TempDir("settings-backup-failure"), "ombra.local.json");
        const string original = """{"command":"omp","theme":"dark"}""";
        File.WriteAllText(path, original);
        var store = new ClientSettingsStore(path);
        Directory.CreateDirectory(store.BackupPath);
        Assert.Null(store.Update(o => o with { Command = null, DefaultThinkingLevel = "high" }).Command);
        Assert.True(store.IsPersistenceUnavailable);
        Assert.Null(store.Load().Command);
        Assert.Equal("high", store.Load().EffectiveThinkingLevel);
        Assert.Equal(original, File.ReadAllText(path));

        File.WriteAllText(path, "{ private-broken-value");
        Assert.Throws<InvalidDataException>(() => store.Load());
        var error = Assert.Throws<InvalidDataException>(() => store.Update(o => o with { Theme = "light" }));
        Assert.DoesNotContain("private-broken-value", error.Message);
        Assert.Equal("{ private-broken-value", File.ReadAllText(path));
        Directory.Delete(store.BackupPath);
        const string repaired = """{"command":"external","theme":"light","futureOption":42}""";
        File.WriteAllText(path, repaired);
        Assert.Null(store.Load().Command);
        Assert.Equal("light", store.Load().Theme);
        store.Update(o => o);
        Assert.False(store.IsPersistenceUnavailable);
        Assert.Equal(repaired, File.ReadAllText(store.BackupPath));
        Assert.Null(new ClientSettingsStore(path).Load().Command);
        Assert.Equal("high", new ClientSettingsStore(path).Load().EffectiveThinkingLevel);
        Assert.Contains("\"futureOption\": 42", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task Settings_warning_stays_visible_when_other_feedback_changes_and_clears_after_recovery()
    {
        var path = Path.Combine(TestProcesses.TempDir("settings-warning"), "ombra.local.json");
        File.WriteAllText(path, """{"theme":{"secret":"private-invalid-value"},"command":"omp"}""");
        var store = new ClientSettingsStore(path);
        store.Load();
        await using var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs(), settings: store);
        var fieldWarning = Loc.F("Some settings could not be read; defaults are in use: {0}", "theme");
        Assert.Equal(fieldWarning, vm.SettingsWarning); // No duplicated wrapping or exception text.
        Assert.DoesNotContain("private-invalid-value", vm.SettingsWarning);

        Directory.CreateDirectory(store.BackupPath);
        store.Update(o => o with { DefaultThinkingLevel = "high" });
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(fieldWarning, vm.SettingsWarning);
        Assert.Contains(Loc.T("Settings cannot be saved. Changes are kept for this run only and will be lost when the app closes. Check write permissions for the settings folder, then change a setting to retry."), vm.SettingsWarning);
        var warning = vm.SettingsWarning;
        vm.SettingsMessage = "Another operation completed.";
        Assert.Equal(warning, vm.SettingsWarning);

        Directory.Delete(store.BackupPath);
        File.WriteAllText(path, """{"command":"external-omp","theme":"light"}""");
        Assert.Equal("external-omp", store.Load().Command);
        store.Update(o => o);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.SettingsWarning);
        Assert.Equal("high", new ClientSettingsStore(path).Load().EffectiveThinkingLevel);
    }
}
