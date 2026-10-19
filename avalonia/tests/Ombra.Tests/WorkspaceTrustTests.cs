using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

/// <summary>
/// Workspace trust, fail-closed: every folder not trusted starts restricted (<c>--untrusted-project</c> reaches the
/// engine) and the chat's card lists what omp reported it left out; Trust and restart reopens that chat with full
/// loading and is remembered; Revoke restricts again (a working chat when its run ends); a changed configuration asks
/// again; a chat moved to an untrusted folder restarts restricted; CLI subcommands that load extensions run outside an
/// untrusted folder; a folder omp reports nothing for shows no card.
/// </summary>
public sealed class WorkspaceTrustTests
{
    private const string Flag = WorkspaceTrust.UntrustedProjectFlag;

    /// <summary>A project with an MCP server running a script of its own, a pre-tool hook and a language server.</summary>
    private static string ProjectWithTools()
    {
        var project = TestProcesses.TempDir("trust-project");
        Directory.CreateDirectory(Path.Combine(project, "scripts"));
        File.WriteAllText(Path.Combine(project, "scripts", "server.js"), "console.log('mcp');\n");
        File.WriteAllText(Path.Combine(project, ".mcp.json"),
            """{ "mcpServers": { "github": { "command": "node", "args": ["scripts/server.js"] } } }""");
        Directory.CreateDirectory(Path.Combine(project, ".omp", "hooks", "pre"));
        File.WriteAllText(Path.Combine(project, ".omp", "hooks", "pre", "check.ts"), "export default () => {};\n");
        File.WriteAllText(Path.Combine(project, "AGENTS.md"), "Be brief.\n");
        return project;
    }

    /// <summary>What the fake engine reports it left out under the flag (Ombra's engine computes it from the folder).</summary>
    private static string ItemsFile(string project, bool empty = false)
    {
        var file = Path.Combine(TestProcesses.TempDir("trust-items"), "items.json");
        File.WriteAllText(file, empty ? "[]" : JsonSerializer.Serialize(new object[]
        {
            new { kind = "mcp", name = "github", source = Path.Combine(project, ".mcp.json") },
            new { kind = "hook", name = ".omp/hooks/pre/check.ts", source = Path.Combine(project, ".omp", "hooks", "pre", "check.ts") },
            new { kind = "lsp", name = "gopls (bin/gopls)", source = Path.Combine(project, "bin", "gopls") },
        }));
        return file;
    }

    private sealed record Ui(MainWindow W, MainViewModel Vm, SessionController S, WorkspaceTrust Trust, string Project, string LaunchLog) : IAsyncDisposable
    {
        /// <summary>Every engine_open so far: its session arguments and folder.</summary>
        public List<(string[] Args, string Cwd)> Launches() => !File.Exists(LaunchLog) ? [] :
            [.. File.ReadAllLines(LaunchLog).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!)
                .Select(n => (n["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray(), n["cwd"]!.GetValue<string>()))];

        public async ValueTask DisposeAsync()
        {
            await Vm.DisposeAsync();
            W.Close();
        }
    }

    private static async Task<Ui> OpenAsync(string project, WorkspaceTrust? trust = null, string scenario = "normal", string? items = null,
        Dictionary<string, string>? commands = null)
    {
        var dir = TestProcesses.TempDir("trust-app");
        trust ??= new WorkspaceTrust(Path.Combine(dir, WorkspaceTrust.FileName));
        var log = Path.Combine(dir, "launches.jsonl");
        var commandFile = Path.Combine(dir, "commands.json");
        File.WriteAllText(commandFile, JsonSerializer.Serialize(commands ?? []));
        var fake = TestProcesses.FakeFactory(scenario, TestProcesses.TempDir("trust-sessions"));
        var env = new Dictionary<string, string?>
        {
            ["FAKE_OMP_LAUNCH_LOG"] = log,
            ["FAKE_UNTRUSTED_ITEMS"] = items ?? ItemsFile(project),
            ["FAKE_OMP_COMMANDS"] = commandFile,
        };
        OmpLaunchSpec Launch(LaunchRequest r)
        {
            var spec = fake(r);
            return trust.Apply(spec with { Environment = new Dictionary<string, string?>(spec.Environment.Concat(env)) });
        }
        var s = new SessionController(Launch, new LaunchRequest(project, ApprovalMode: "write"));
        var vm = new MainViewModel(s, new AppArgs()) { WorkspaceTrust = trust };
        var w = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        return new Ui(w, vm, s, trust, project, log);
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static OmpLaunchSpec ChatSpec(string folder) => new() { FileName = "omp", WorkingDirectory = folder };

    [Fact]
    public void Every_folder_not_trusted_starts_restricted_including_ones_a_file_scan_would_miss()
    {
        var trust = new WorkspaceTrust(null);
        var plain = TestProcesses.TempDir("trust-plain");
        var declaration = TestProcesses.TempDir("trust-dts");
        Directory.CreateDirectory(Path.Combine(declaration, ".omp", "extensions"));
        File.WriteAllText(Path.Combine(declaration, ".omp", "extensions", "x.d.ts"), "require('child_process');\n");
        var manifest = TestProcesses.TempDir("trust-manifest");
        Directory.CreateDirectory(Path.Combine(manifest, ".omp", "extensions", "foo"));
        File.WriteAllText(Path.Combine(manifest, ".omp", "extensions", "foo", "package.json"), """{ "omp": { "extensions": ["../../../src/util.ts"] } }""");
        var gemini = TestProcesses.TempDir("trust-gemini");
        Directory.CreateDirectory(Path.Combine(gemini, ".gemini"));
        File.WriteAllText(Path.Combine(gemini, ".gemini", "settings.json"), """{ "shellPath": "./x.sh" }""");
        var localBin = TestProcesses.TempDir("trust-gopls");
        File.WriteAllText(Path.Combine(localBin, "go.mod"), "module x\n");
        Directory.CreateDirectory(Path.Combine(localBin, "bin"));
        File.WriteAllText(Path.Combine(localBin, "bin", "gopls"), "#!/bin/sh\n");

        foreach (var folder in new[] { plain, declaration, manifest, gemini, localBin })
        {
            Assert.Equal(ProjectTrustStatus.Untrusted, trust.Check(folder).Status);
            Assert.Contains(Flag, trust.Apply(ChatSpec(folder)).SessionArguments);
            Assert.Contains(Flag, trust.ApplyToTerminal(new OmpRuntimeOptions { Command = "omp" }.ToTuiLaunchSpec(folder)).Arguments);
        }
    }

    [Fact]
    public void Trust_is_saved_privately_and_a_configuration_change_asks_again()
    {
        var project = ProjectWithTools();
        Directory.CreateDirectory(Path.Combine(project, "bin"));
        File.WriteAllText(Path.Combine(project, "bin", "gopls"), "#!/bin/sh\necho v1\n");
        var store = TestProcesses.TempDir("trust-store");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(store, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var file = Path.Combine(store, WorkspaceTrust.FileName);
        var trust = new WorkspaceTrust(file);

        trust.Trust(project, [Path.Combine(project, "bin", "gopls"), "/usr/bin/elsewhere"]);

        var again = new WorkspaceTrust(file);
        Assert.Equal(ProjectTrustStatus.Trusted, again.Check(project).Status);
        Assert.DoesNotContain(Flag, again.Apply(ChatSpec(project)).SessionArguments);
        Assert.Equal(new[] { "bin/gopls" }, Assert.Single(again.TrustedFolders).Sources);
        if (!OperatingSystem.IsWindows())
        {
            // Owner-only on every write, also for a folder that existed before with wider rights
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(store));
            var link = Path.Combine(TestProcesses.TempDir("trust-link"), "alias");
            Directory.CreateSymbolicLink(link, project);
            Assert.Equal(ProjectTrustStatus.Trusted, again.Check(link).Status);
        }
        Assert.Equal(ProjectTrustStatus.Untrusted, again.Check(ProjectWithTools()).Status);

        // Prompt text is not configuration
        File.AppendAllText(Path.Combine(project, "AGENTS.md"), "More.\n");
        Assert.Equal(ProjectTrustStatus.Trusted, again.Check(project).Status);

        // A hook, a script an MCP server names, a binary omp reported: each change restricts and asks again
        foreach (var (changed, content) in new[]
                 {
                     (Path.Combine(project, ".omp", "hooks", "pre", "check.ts"), "export default () => { fetch('x'); };\n"),
                     (Path.Combine(project, "scripts", "server.js"), "require('child_process');\n"),
                     (Path.Combine(project, "bin", "gopls"), "#!/bin/sh\necho v2\n"),
                     (Path.Combine(project, ".omp", "extensions", "y.d.ts"), "export {};\n"),
                     // A skill, slash command or subagent the folder gains after trust loads only once trusted again
                     (Path.Combine(project, ".omp", "skills", "later", "SKILL.md"), "---\nname: later\ndescription: Later\n---\nRun it.\n"),
                     (Path.Combine(project, ".claude", "commands", "deploy.md"), "Deploy now.\n"),
                     (Path.Combine(project, ".omp", "agents", "fixer.md"), "---\nname: fixer\ndescription: Fixes\n---\nFix it.\n"),
                 })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(changed)!);
            File.WriteAllText(changed, content);
            var check = again.Check(project);
            Assert.Equal(ProjectTrustStatus.Changed, check.Status);
            Assert.Contains(Flag, again.Apply(ChatSpec(project)).SessionArguments);
            again.Trust(project, [Path.Combine(project, "bin", "gopls")]);
        }

        again.Revoke(project);
        Assert.Empty(new WorkspaceTrust(file).TrustedFolders);
        Assert.Contains(Flag, again.Apply(ChatSpec(project)).SessionArguments);
    }

    [Fact]
    public void Cli_subcommands_that_load_extensions_run_outside_an_untrusted_folder()
    {
        var project = ProjectWithTools();
        var trust = new WorkspaceTrust(Path.Combine(TestProcesses.TempDir("trust-cli"), WorkspaceTrust.FileName));
        var options = new OmpRuntimeOptions { Command = "omp" };

        foreach (var args in new[] { new[] { "usage", "--json" }, new[] { "login" }, new[] { "models" } })
        {
            var spec = trust.ApplyToCli(options.ToCliLaunchSpec(args, project), args[0]);
            Assert.False(SessionCatalog.SamePath(project, spec.WorkingDirectory));
            Assert.Equal(trust.NeutralDirectory, spec.WorkingDirectory);
            Assert.Empty(Directory.GetFileSystemEntries(spec.WorkingDirectory!));
        }
        // Reading the project's settings and plugin list is data, not code: they stay in the folder
        Assert.Equal(project, trust.ApplyToCli(options.ToCliLaunchSpec(["config", "list", "--json"], project), "config").WorkingDirectory);
        Assert.Equal(project, trust.ApplyToCli(options.ToCliLaunchSpec(["plugin", "list", "--json"], project), "plugin").WorkingDirectory);

        trust.Trust(project, []);
        Assert.Equal(project, trust.ApplyToCli(options.ToCliLaunchSpec(["usage", "--json"], project), "usage").WorkingDirectory);
    }

    [AvaloniaFact]
    public async Task An_untrusted_folder_starts_restricted_and_the_card_lists_what_omp_left_out()
    {
        await using var ui = await OpenAsync(ProjectWithTools());
        Assert.Contains(Flag, Assert.Single(ui.Launches()).Args);
        await Until(() => ui.Vm.SessionCard is { Kind: "trust" }, "trust card");
        var card = ui.Vm.SessionCard!;
        Assert.Equal(Loc.T("Do you trust this folder?"), card.Title);
        Assert.True(card.IsAsking);
        Assert.Contains("github", card.Detail);
        Assert.Contains(".omp/hooks/pre/check.ts", card.Detail);
        Assert.Contains("gopls (bin/gopls)", card.Detail);
        Assert.Equal(Loc.T("Trust and restart"), card.Primary!.Label);
        Assert.Equal(Loc.T("Keep restricted"), card.Secondary!.Label);

        // Keep restricted: the card goes and the chat stays as it is
        await card.Secondary.Run();
        await Until(() => ui.Vm.SessionCard is null, "card closed");
        Assert.Single(ui.Launches());
        Assert.True(ui.S.ProjectLaunch!.Restricted);
    }

    [AvaloniaFact]
    public async Task The_card_lists_the_folders_skills_under_their_own_kind()
    {
        var project = ProjectWithTools();
        var skill = Path.Combine(project, ".omp", "skills", "probe-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skill)!);
        File.WriteAllText(skill, "---\nname: probe-skill\ndescription: Probe\n---\nRun it.\n");
        var items = Path.Combine(TestProcesses.TempDir("trust-items"), "items.json");
        File.WriteAllText(items, JsonSerializer.Serialize(new object[]
        {
            new { kind = "skill", name = "probe-skill", source = skill },
            new { kind = "command", name = "/deploy", source = Path.Combine(project, ".omp", "commands", "deploy.md") },
            new { kind = "mcp", name = "github", source = Path.Combine(project, ".mcp.json") },
            new { kind = "agent", name = "fixer", source = Path.Combine(project, ".omp", "agents", "fixer.md") },
        }));
        await using var ui = await OpenAsync(project, items: items);
        await Until(() => ui.Vm.SessionCard is { Kind: "trust" }, "trust card");
        Assert.Equal(
            new[] { Loc.T("MCP servers") + ": github", Loc.T("Commands") + ": /deploy", Loc.T("Skills") + ": probe-skill", Loc.T("Subagents") + ": fixer" },
            ui.Vm.SessionCard!.Detail!.Split('\n'));

        // Trust and restart: the skill file omp reported is part of what was trusted
        await ui.Vm.SessionCard.Primary!.Run();
        await Until(() => ui.Launches().Count == 2 && ui.Vm.Phase == SessionPhase.Ready, "restarted");
        Assert.DoesNotContain(Flag, ui.Launches()[1].Args);
        Assert.Contains(".omp/skills/probe-skill/SKILL.md", Assert.Single(ui.Trust.TrustedFolders).Sources);
    }

    [AvaloniaFact]
    public async Task A_restricted_folder_omp_reports_nothing_for_shows_no_card()
    {
        var project = TestProcesses.TempDir("trust-plain-project");
        File.WriteAllText(Path.Combine(project, "AGENTS.md"), "Be brief.\n");
        await using var ui = await OpenAsync(project, items: ItemsFile(project, empty: true));
        Assert.Contains(Flag, Assert.Single(ui.Launches()).Args);
        await Until(() => ui.S.ProjectLaunch?.LeftOut is not null, "omp's report");
        Assert.Empty(ui.S.ProjectLaunch!.LeftOut!);
        Dispatcher.UIThread.RunJobs();
        Assert.False(ui.Vm.SessionCard is { Kind: "trust" });
        Assert.Empty(ui.Trust.TrustedFolders);
    }

    [AvaloniaFact]
    public async Task Trust_restarts_only_that_chat_with_full_loading_and_is_remembered()
    {
        await using var ui = await OpenAsync(ProjectWithTools());
        await Until(() => ui.Vm.SessionCard is { Kind: "trust" }, "trust card");

        await ui.Vm.SessionCard!.Primary.Run();

        await Until(() => ui.Launches().Count == 2 && ui.Vm.Phase == SessionPhase.Ready, "restarted");
        Assert.DoesNotContain(Flag, ui.Launches()[1].Args);
        Assert.False(ui.S.ProjectLaunch!.Restricted);
        await Until(() => ui.Vm.SessionCard is not { Kind: "trust" }, "trust card gone");
        Assert.Equal(ProjectTrustStatus.Trusted, new WorkspaceTrust(ui.Trust.Path).Check(ui.Project).Status);
        // What omp reported inside the folder is part of the fingerprint from now on
        Assert.Contains(".mcp.json", Assert.Single(ui.Trust.TrustedFolders).Sources);

        // A later start of the trusted folder asks nothing
        await ui.S.RecoverAsync();
        await Until(() => ui.Launches().Count == 3 && ui.Vm.Phase == SessionPhase.Ready, "started again");
        Assert.DoesNotContain(Flag, ui.Launches()[2].Args);
        Dispatcher.UIThread.RunJobs();
        Assert.False(ui.Vm.SessionCard is { Kind: "trust" });
    }

    [Fact]
    public void Trust_binds_to_the_folder_as_shown_not_as_it_is_at_the_click()
    {
        var project = ProjectWithTools();
        var trust = new WorkspaceTrust(Path.Combine(TestProcesses.TempDir("trust-snapshot"), WorkspaceTrust.FileName));
        var shown = WorkspaceTrust.Snapshot(project, [Path.Combine(project, ".mcp.json")]);

        // Something new lands in the folder while the card is up
        Directory.CreateDirectory(Path.Combine(project, ".omp", "extensions"));
        File.WriteAllText(Path.Combine(project, ".omp", "extensions", "x.ts"), "export default () => {};\n");
        Assert.False(trust.Trust(shown));
        Assert.Empty(trust.TrustedFolders);
        Assert.Equal(ProjectTrustStatus.Untrusted, trust.Check(project).Status);

        // Unchanged since shown: trusted, with what was shown
        var again = WorkspaceTrust.Snapshot(project, [Path.Combine(project, ".mcp.json")]);
        Assert.True(trust.Trust(again));
        Assert.Equal(ProjectTrustStatus.Trusted, trust.Check(project).Status);
        Assert.Equal(new[] { ".mcp.json" }, Assert.Single(trust.TrustedFolders).Sources);
    }

    [AvaloniaFact]
    public async Task A_folder_changed_after_its_card_was_shown_is_not_trusted_and_asked_about_again()
    {
        await using var ui = await OpenAsync(ProjectWithTools());
        await Until(() => ui.Vm.SessionCard is { Kind: "trust" }, "trust card");
        var shown = ui.Vm.SessionCard!;

        // The restricted chat (or anything else) adds an extension the card never named
        Directory.CreateDirectory(Path.Combine(ui.Project, ".omp", "extensions"));
        File.WriteAllText(Path.Combine(ui.Project, ".omp", "extensions", "x.ts"), "export default () => {};\n");
        await shown.Primary!.Run();

        await Until(() => ui.Launches().Count == 2 && ui.Vm.Phase == SessionPhase.Ready, "restarted");
        Assert.Contains(Flag, ui.Launches()[1].Args);
        Assert.True(ui.S.ProjectLaunch!.Restricted);
        Assert.Empty(ui.Trust.TrustedFolders);
        // A new card with omp's report of the folder as it is now
        await Until(() => ui.Vm.SessionCard is { Kind: "trust", IsAsking: true } card && !ReferenceEquals(card, shown), "asked again");
    }

    [AvaloniaFact]
    public async Task Revoking_restricts_a_working_chat_when_its_run_ends()
    {
        var project = ProjectWithTools();
        var trust = new WorkspaceTrust(Path.Combine(TestProcesses.TempDir("trust-revoke"), WorkspaceTrust.FileName));
        trust.Trust(project, []);
        await using var ui = await OpenAsync(project, trust, scenario: "approval");
        Assert.DoesNotContain(Flag, Assert.Single(ui.Launches()).Args);

        ui.Vm.ComposerText = "run a command";
        await ui.Vm.SendCommand.ExecuteAsync(null);
        var asked = await TestProcesses.Eventually(ui.S.Snapshot, s => s.Dialogs.Count == 1, TimeSpan.FromSeconds(20), "approval request");

        var row = Assert.Single(ui.Vm.Permissions.TrustedFolders);
        await row.RevokeCommand.ExecuteAsync(null);
        Assert.Empty(ui.Vm.Permissions.TrustedFolders);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(ui.Launches()); // the run is not cut off

        Assert.True(await ui.S.AnswerDialogAsync(asked.Dialogs[0].Id, new DialogAnswer.Value("Approve")));
        await Until(() => ui.Launches().Count == 2 && ui.Vm.Phase == SessionPhase.Ready, "restarted restricted after the run");
        Assert.Contains(Flag, ui.Launches()[1].Args);
        await Until(() => ui.Vm.SessionCard is { Kind: "trust" }, "asked again");
    }

    [AvaloniaFact]
    public async Task A_hook_changed_after_trust_asks_again()
    {
        var project = ProjectWithTools();
        var trust = new WorkspaceTrust(Path.Combine(TestProcesses.TempDir("trust-changed"), WorkspaceTrust.FileName));
        trust.Trust(project, []);
        await using var ui = await OpenAsync(project, trust);
        Assert.DoesNotContain(Flag, Assert.Single(ui.Launches()).Args);

        File.WriteAllText(Path.Combine(project, ".omp", "hooks", "pre", "check.ts"), "export default () => { /* new */ };\n");
        await ui.S.RecoverAsync();

        await Until(() => ui.Launches().Count == 2 && ui.Vm.Phase == SessionPhase.Ready, "restarted");
        Assert.Contains(Flag, ui.Launches()[1].Args);
        await Until(() => ui.Vm.SessionCard is { Kind: "trust" }, "asked again");
        Assert.Equal(Loc.T("This folder's tools changed"), ui.Vm.SessionCard!.Title);
    }

    [AvaloniaFact]
    public async Task Moving_a_trusted_chat_to_an_untrusted_folder_restarts_it_restricted()
    {
        var project = ProjectWithTools();
        var target = ProjectWithTools();
        var trust = new WorkspaceTrust(Path.Combine(TestProcesses.TempDir("trust-move"), WorkspaceTrust.FileName));
        trust.Trust(project, []);
        await using var ui = await OpenAsync(project, trust, items: ItemsFile(target), commands: new() { ["/move"] = $"Moved to {target}." });
        Assert.DoesNotContain(Flag, Assert.Single(ui.Launches()).Args);

        ui.Vm.PickFolderRequested += () => Task.FromResult<string?>(target);
        await ui.Vm.MoveSessionCommand.ExecuteAsync(null);
        await ui.Vm.SessionCard!.Primary.Run();

        await Until(() => ui.Launches().Count == 2 && ui.Vm.Phase == SessionPhase.Ready, "restarted in the new folder");
        var (args, cwd) = ui.Launches()[1];
        Assert.Contains(Flag, args);
        Assert.True(SessionCatalog.SamePath(target, cwd));
        Assert.True(ui.S.ProjectLaunch!.Restricted);
        // The move card closes; the trust card that waited behind it comes back
        if (ui.Vm.SessionCard is { Kind: not "trust" }) ui.Vm.CloseSessionCard();
        await Until(() => ui.Vm.SessionCard is { Kind: "trust" }, "asked about the new folder");
    }
}
