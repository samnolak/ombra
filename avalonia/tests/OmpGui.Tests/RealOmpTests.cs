using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// End to end against a real omp. Runs only when OMPGUI_TEST_CONFIG points at a local runtime options file.
/// The scripted keywords (TOOLCALL, SLOW, BIGREPLY, BASHCALL) are understood by tools/mock-model/server.mjs;
/// with a real model they are ordinary prompts and only the generic assertions hold.
/// </summary>
public sealed class RealOmpTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(120);

    private static async Task<SessionController?> StartAsync()
    {
        if (TestProcesses.RealOmp() is not { } options)
        {
            Assert.Skip("OMPGUI_TEST_CONFIG is not set");
            return null;
        }
        var s = new SessionController(options.ToLaunchSpec(), TimeSpan.FromSeconds(90));
        await s.StartAsync();
        return s;
    }

    [Fact]
    public async Task Negotiates_v2_and_reports_the_profile_model()
    {
        await using var s = await StartAsync();
        var snap = s!.Snapshot();
        Assert.Equal(2, snap.ProtocolVersion);
        Assert.Equal(SessionPhase.Ready, snap.Phase);
        Assert.Equal("qwen-local/flash-next-w4a16", snap.Model);
        // omp 18.8.0 names the thinking levels of the live model ("off" first); the thinking menu shows them
        var levels = await s.GetThinkingLevelsAsync();
        Assert.NotNull(levels);
        Assert.Equal("off", levels![0]);
    }

    [Fact]
    public async Task Prompt_with_a_tool_call_streams_and_completes()
    {
        await using var s = await StartAsync();
        await s!.PromptAsync("TOOLCALL please read the readme");
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.MessagesCompleted >= 2, Wait, "run end");
        Assert.Contains(snap.Items, i => i is ToolItem { Name: "read", Status: ToolStatus.Succeeded });
        Assert.Contains(snap.DebugTail, d => d.Type == "message_update");
        Assert.Equal(0, snap.StreamMismatches);
    }

    [Fact]
    public async Task Abort_interrupts_a_slow_stream()
    {
        await using var s = await StartAsync();
        await s!.PromptAsync("SLOW reply please");
        await TestProcesses.Eventually(s.Snapshot, x => x.Items.OfType<AssistantItem>().Any(a => a.Text.Length > 100), Wait, "streaming");
        await s.AbortAsync();
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "abort settles");
        var a = snap.Items.OfType<AssistantItem>().Last();
        Assert.False(a.Streaming);
        Assert.Equal("aborted", a.StopReason);
    }

    [Fact]
    public async Task Oversized_reply_arrives_losslessly_over_v2_chunks()
    {
        await using var s = await StartAsync();
        await s!.PromptAsync("BIGREPLY 1500");
        SessionSnapshot snap;
        try
        {
            snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.MessagesCompleted >= 1, Wait, "run end");
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException(e.Message + "\nread side: " + s.Connection!.ReadDiagnostics, e);
        }
        var a = snap.Items.OfType<AssistantItem>().Last();
        Assert.True(a.Text.Length >= 1500 * 1024, $"reply was {a.Text.Length} chars");
        Assert.Contains(snap.DebugTail, d => d.Summary.Contains("bytes"));
        Assert.Equal(0, snap.StreamMismatches);
        Assert.DoesNotContain(snap.Items, i => i is NoticeItem { Level: NoticeLevel.Error });
    }

    /// <summary>omp runs with --approval-mode write: the exec tier (bash) asks. Never yolo in tests.</summary>
    private static async Task<PendingDialog> BashApprovalAsync(SessionController s)
    {
        await s.PromptAsync("BASHCALL now");
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count > 0 || x.Phase != SessionPhase.Running, Wait, "approval request");
        var d = Assert.Single(snap.Dialogs);
        Assert.Equal(DialogKind.Approval, d.Kind);
        Assert.Equal("Allow tool: bash", d.Headline);
        Assert.Contains("echo hi", d.Details);
        return d;
    }

    [Fact]
    public async Task Approving_an_exec_tool_runs_it()
    {
        await using var s = await StartAsync();
        var d = await BashApprovalAsync(s!);
        Assert.True(await s!.AnswerDialogAsync(d.Id, new DialogAnswer.Value("Approve")));
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.MessagesCompleted >= 2, Wait, "run end");
        var tool = snap.Items.OfType<ToolItem>().Single(t => t.Name == "bash");
        Assert.Equal(ToolStatus.Succeeded, tool.Status);
        Assert.Contains("hi", tool.Output);
        Assert.Contains(snap.Items, i => i is NoticeItem { Text: "Approved by you — bash" });
    }

    [Fact]
    public async Task Denying_an_exec_tool_blocks_it()
    {
        await using var s = await StartAsync();
        var d = await BashApprovalAsync(s!);
        Assert.True(await s!.AnswerDialogAsync(d.Id, new DialogAnswer.Value("Deny")));
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.MessagesCompleted >= 1, Wait, "run end");
        var tool = snap.Items.OfType<ToolItem>().Single(t => t.Name == "bash");
        Assert.Equal(ToolStatus.Failed, tool.Status);
        Assert.Contains("denied", tool.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stopping_while_an_approval_is_pending_does_not_hang()
    {
        await using var s = await StartAsync();
        await BashApprovalAsync(s!);
        await s!.AbortAsync().WaitAsync(TimeSpan.FromSeconds(60));
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "abort settles");
        Assert.Empty(snap.Dialogs);
        Assert.DoesNotContain(snap.Items, i => i is ToolItem { Name: "bash", Status: ToolStatus.Succeeded or ToolStatus.Running });
    }

    private static async Task RunAsync(SessionController s, string text)
    {
        var before = s.Snapshot().MessagesCompleted;
        await s.PromptAsync(text);
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.MessagesCompleted > before, Wait, "run end");
    }

    [Fact]
    public async Task Sessions_new_switch_rename_and_resume_in_another_folder()
    {
        if (TestProcesses.RealOmp() is not { } options)
        {
            Assert.Skip("OMPGUI_TEST_CONFIG is not set");
            return;
        }
        await using var s = new SessionController(options.ToLaunchSpec, new LaunchRequest(options.WorkingDirectory), TimeSpan.FromSeconds(90));
        await s.StartAsync();
        await RunAsync(s, "session one marker");
        var one = s.Snapshot();
        Assert.NotNull(one.SessionFile);
        await s.RenameSessionAsync("Session One");

        await s.NewSessionAsync();
        var two = s.Snapshot();
        Assert.NotEqual(one.SessionFile, two.SessionFile);
        Assert.DoesNotContain(two.Items, i => i is UserItem);
        await RunAsync(s, "session two marker");

        // omp writes the session files; the catalog reads them back (read-only).
        var catalog = await SessionCatalog.ScanAsync(SessionCatalog.SessionsRootOf(s.Snapshot().SessionFile)!);
        var entry = Assert.Single(catalog, c => c.Path == one.SessionFile);
        Assert.Equal("Session One", entry.Title);
        Assert.True(SessionCatalog.SamePath(one.Cwd, entry.Cwd));

        // Same folder: switch_session in place.
        var pid = s.ProcessId;
        await s.OpenSessionAsync(entry.Path, entry.Cwd);
        Assert.Equal(pid, s.ProcessId);
        Assert.Contains(s.Snapshot().Items, i => i is UserItem { Text: "session one marker" });

        // Another folder: omp restarts there; opening the old session restarts it back with --resume.
        var elsewhere = TestProcesses.TempDir("real-other-project");
        await s.OpenFolderAsync(elsewhere);
        Assert.True(SessionCatalog.SamePath(elsewhere, s.Snapshot().Cwd), s.Snapshot().Cwd);
        Assert.Equal(SessionPhase.Ready, s.Snapshot().Phase);
        await s.OpenSessionAsync(entry.Path, entry.Cwd);
        var resumed = s.Snapshot();
        Assert.Equal(SessionPhase.Ready, resumed.Phase);
        Assert.Equal(one.SessionFile, resumed.SessionFile);
        Assert.Contains(resumed.Items, i => i is UserItem { Text: "session one marker" });
        await RunAsync(s, "continued after resume");
    }

    [Fact]
    public async Task Models_approval_mode_and_recovery()
    {
        if (TestProcesses.RealOmp() is not { } options)
        {
            Assert.Skip("OMPGUI_TEST_CONFIG is not set");
            return;
        }
        await using var s = new SessionController(options.ToLaunchSpec, new LaunchRequest(options.WorkingDirectory, ApprovalMode: "write"), TimeSpan.FromSeconds(90));
        await s.StartAsync();
        var models = await s.GetModelsAsync();
        var current = Assert.Single(models, m => m.Key == "qwen-local/flash-next-w4a16");
        await s.SetModelAsync(current);
        Assert.Equal("qwen-local/flash-next-w4a16", s.Snapshot().Model);
        Assert.DoesNotContain(s.Snapshot().Items, i => i is NoticeItem { Level: NoticeLevel.Error });

        // The mode is a launch flag: omp restarts on the same session. In 18.2.0 always-ask still runs the read
        // tier without asking and asks for write and exec (tools/approval.ts APPROVAL_MODE_MAX_TIER).
        await RunAsync(s, "before the mode change");
        await s.SetApprovalModeAsync("always-ask");
        Assert.Equal("always-ask", s.Snapshot().ApprovalMode);
        Assert.Contains(s.Snapshot().Items, i => i is UserItem { Text: "before the mode change" });
        await RunAsync(s, "TOOLCALL please");
        Assert.Contains(s.Snapshot().Items, i => i is ToolItem { Name: "read", Status: ToolStatus.Succeeded });
        await s.PromptAsync("BASHCALL now");
        var ask = await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count > 0 || x.Phase == SessionPhase.Ready, Wait, "bash approval");
        var d = Assert.Single(ask.Dialogs);
        Assert.Equal("Allow tool: bash", d.Headline);
        await s.AnswerDialogAsync(d.Id, new DialogAnswer.Value("Deny"));
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "denied run ends");

        // Crash and recover on the same session.
        var file = s.Snapshot().SessionFile;
        System.Diagnostics.Process.GetProcessById(s.ProcessId!.Value).Kill(entireProcessTree: true);
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Faulted, Wait, "fault");
        await s.RecoverAsync();
        Assert.Equal(SessionPhase.Ready, s.Snapshot().Phase);
        Assert.Equal(file, s.Snapshot().SessionFile);
        Assert.Contains(s.Snapshot().Items, i => i is UserItem { Text: "before the mode change" });
        Assert.Equal("always-ask", s.Snapshot().ApprovalMode);
    }

    [Fact]
    public async Task Follow_up_and_steer_while_running_are_delivered()
    {
        await using var s = await StartAsync();
        await s!.PromptAsync("SLOWSHORT reply please");
        await TestProcesses.Eventually(s.Snapshot, x => x.Items.OfType<AssistantItem>().Any(a => a.Text.Length > 20), Wait, "streaming");
        await s.QueueAsync(QueueKind.FollowUp, "follow-up marker", []);
        Assert.Single(s.Snapshot().Queued);
        var done = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.Queued.Count == 0
            && x.Items.OfType<UserItem>().Any(u => u.Text == "follow-up marker"), Wait, "follow-up delivered");
        Assert.True(done.Items.OfType<AssistantItem>().Count() >= 2);
        Assert.DoesNotContain(done.Items, i => i is NoticeItem n && n.Text.StartsWith("Not delivered", StringComparison.Ordinal));

        await s.PromptAsync("SLOWSHORT reply again");
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Running && x.Items.OfType<AssistantItem>().Count() >= 3, Wait, "second run streaming");
        await s.QueueAsync(QueueKind.Steer, "steer marker", []);
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.Items.OfType<UserItem>().Any(u => u.Text == "steer marker"), Wait, "steer delivered");
    }

    [Fact]
    public async Task A_png_attachment_reaches_omp_with_the_prompt()
    {
        await using var s = await StartAsync();
        // 1×1 transparent PNG
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        await s!.PromptAsync("describe this image", [new ImageAttachment("dot.png", "image/png", png)]);
        var done = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.MessagesCompleted >= 1, Wait, "run end");
        var user = Assert.Single(done.Items.OfType<UserItem>());
        Assert.True(user.Confirmed);
        Assert.DoesNotContain(done.Items, i => i is NoticeItem { Level: NoticeLevel.Error });
        Assert.Equal(png, Assert.Single(user.Images).Data);
        // omp returns the image's bytes with the session's messages (it may re-encode it): history shows it again
        var history = new ConversationState();
        history.Hydrate(await OmpGui.Rpc.OmpCommands.GetMessagesAsync(s.Connection!));
        Assert.NotEmpty(Assert.Single(history.Snapshot().Items.OfType<UserItem>().Single().Images).Data);
    }

    [Fact]
    public async Task Slash_commands_from_omps_catalog_run_locally()
    {
        await using var s = await StartAsync();
        var cmds = s!.Snapshot().Commands;
        Assert.Contains(cmds, c => c.Name == "compact");
        Assert.Contains(cmds, c => c.Source == "skill");
        Assert.DoesNotContain(cmds, c => c.Name == "plan"); // TUI-only in 18.2.0: not offered over RPC

        await s.PromptAsync("/context");
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.Items.OfType<CommandOutputItem>().Any(), Wait, "command output");
        Assert.DoesNotContain(snap.Items, i => i is AssistantItem); // local-only: no model call
        await s.PromptAsync("/rename Renamed by command");
        await TestProcesses.Eventually(s.Snapshot, x => x.SessionName == "Renamed by command" && x.Phase == SessionPhase.Ready, Wait, "rename");
        Assert.NotNull(s.Snapshot().ContextPercent);
    }

    [Fact]
    public async Task History_survives_a_restart_of_the_view_via_get_messages()
    {
        await using var s = await StartAsync();
        await s!.PromptAsync("remember this");
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.MessagesCompleted >= 1, Wait, "run end");
        var messages = await OmpGui.Rpc.OmpCommands.GetMessagesAsync(s.Connection!);
        var rebuilt = new ConversationState();
        rebuilt.Hydrate(messages);
        var items = rebuilt.Snapshot().Items;
        Assert.Contains(items, i => i is UserItem u && u.Text == "remember this");
        Assert.Contains(items, i => i is AssistantItem);
    }
}
