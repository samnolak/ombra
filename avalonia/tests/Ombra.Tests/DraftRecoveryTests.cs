using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ombra.App;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

public sealed class DraftRecoveryTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static ImageAttachment Image(string name = "draft.png") => new(name, "image/png",
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jf9sAAAAASUVORK5CYII="));

    private static (SessionController Session, MainViewModel Vm) Create(string project, ClientSettingsStore settings, string? resume = null, string scenario = "slow-stream")
    {
        var session = new SessionController(TestProcesses.FakeFactory(scenario, Path.Combine(project, "sessions")), new LaunchRequest(project, resume));
        return (session, new MainViewModel(session, new AppArgs(), settings: settings));
    }

    [Fact]
    public async Task Debounced_checkpoint_can_be_opened_without_disposing_the_writer_and_is_private()
    {
        var root = TestProcesses.TempDir("draft-store");
        var path = DraftStore.PathFor(Path.Combine(root, "isolated.json"));
        using var writer = new DraftStore(path);
        writer.Update("one", d => d with { Composer = new("latest edit", [Image()], ["long paste"], [new("m1", "change this", Code: new CodeAnchor("app.cs", 4, false, "source"))]) }, root);
        await TestProcesses.Eventually(() => File.Exists(path), yes => yes, Wait, "debounced draft saved");
        var reopened = DraftStore.ReadSnapshot(path);
        var draft = Assert.Single(reopened.All);
        Assert.Equal("latest edit", draft.Composer.Text);
        Assert.Equal(Image().Data, Assert.Single(draft.Composer.Images).Data);
        Assert.Equal("change this", Assert.Single(draft.Composer.Marks).Note);
        Assert.Empty(Directory.GetFiles(root, "*.tmp-*"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.NotEqual(path, DraftStore.PathFor(Path.Combine(root, "another.json")));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"Version\":1,\"Drafts\":null}")]
    [InlineData("{\"Version\":1,\"Drafts\":[null]}")]
    [InlineData("{\"Version\":1,\"Drafts\":[{\"Id\":\"one\",\"Project\":\"/project\",\"Composer\":null}]}")]
    public void Corrupt_journal_is_reported_without_replacing_original_bytes(string json)
    {
        var path = Path.Combine(TestProcesses.TempDir("bad-drafts"), "drafts.json");
        File.WriteAllText(path, json);
        var store = new DraftStore(path);
        Assert.NotNull(store.LastError);
        Assert.Empty(store.All);
        store.Update("new", d => d with { Composer = DraftContent.Empty with { Text = "new work" } }, Path.GetDirectoryName(path)!);
        Assert.Throws<IOException>(store.Flush);
        Assert.Equal(json, File.ReadAllText(path));
        Assert.Throws<IOException>(store.Dispose);
        store.Flush(); // a delayed callback after failed Dispose is harmless
    }

    [Theory]
    [InlineData("Images")]
    [InlineData("Pastes")]
    [InlineData("Marks")]
    public void Incomplete_nested_content_does_not_crash_startup(string field)
    {
        var root = TestProcesses.TempDir("incomplete-drafts");
        var path = Path.Combine(root, "drafts.json");
        using (var store = new DraftStore(path))
            store.Update("one", d => d with { Composer = DraftContent.Empty with { Text = "draft" } }, root, flush: true);
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["Drafts"]![0]!["Composer"]![field] = null;
        var broken = document.ToJsonString();
        File.WriteAllText(path, broken);
        var reopened = new DraftStore(path);
        Assert.NotNull(reopened.LastError);
        Assert.Empty(reopened.All);
        Assert.Throws<IOException>(reopened.Dispose);
        Assert.Equal(broken, File.ReadAllText(path));
    }

    /// <summary>A version-1 journal kept code comments structured and page comments as their request text; both become
    /// local marks without losing a field, and the page text is sent exactly as it was saved.</summary>
    [Fact]
    public void Version_one_comments_and_page_comments_migrate_to_marks_losslessly()
    {
        var root = TestProcesses.TempDir("legacy-marks");
        var path = Path.Combine(root, "drafts.json");
        const string page = "Comments on the page http://localhost:5173/ (\"Shop\"), left in the preview. Each marks a point or an area of the page; the element details are quoted from the page:\n\n1. Bigger button\n   Point: 10,20 in a 800×600 viewport\n   On: <button> with the text \"Buy\"\n   Selector: main > button\n   Element box: 80×30 at 5,15\n\nFind these places in the project's source and make the changes the comments ask for.";
        var legacy = new JsonObject
        {
            ["Version"] = 1,
            ["Drafts"] = new JsonArray(new JsonObject
            {
                ["Id"] = "old", ["Project"] = root, ["SessionFile"] = null,
                ["Composer"] = new JsonObject
                {
                    ["Text"] = "", ["Images"] = new JsonArray(), ["Pastes"] = new JsonArray(),
                    ["Comments"] = new JsonArray(new JsonObject
                    {
                        ["Path"] = "src/a.cs", ["Line"] = 3, ["RemovedLine"] = true, ["LineText"] = "  value", ["Comment"] = "rename",
                        ["Anchor"] = new JsonObject { ["Uri"] = "file:///src/a.cs", ["ObservedDocumentVersion"] = 7, ["BufferUnsaved"] = true, ["ReviewSnapshotSha256"] = "hash", ["HunkHeader"] = "@@ -1 +1 @@" },
                    }),
                    ["PageComments"] = page,
                },
                ["Queue"] = new JsonArray(), ["Pending"] = new JsonArray(), ["QuestionReply"] = "", ["RecoveredSequences"] = new JsonArray(),
            }),
        };
        File.WriteAllText(path, legacy.ToJsonString());
        using var store = new DraftStore(path);
        Assert.Null(store.LastError);
        var marks = Assert.Single(store.All).Composer.Marks;
        Assert.Equal(2, marks.Length);
        Assert.Equal("rename", marks[0].Note);
        Assert.Equal(MarkStatus.Open, marks[0].Status);
        Assert.Equal(new CodeAnchor("src/a.cs", 3, true, "  value", Snapshot: new CodeCommentAnchor("file:///src/a.cs", 7, true, "hash", "@@ -1 +1 @@")),
            marks[0].Code);
        Assert.Equal(page, marks[1].Legacy);
        var request = LocalMarks.BuildRequest("", marks);
        Assert.StartsWith(LocalMarks.AllOnlyLine + "\n\n" + LocalMarks.CodeMarker, request);
        Assert.Contains("1. src/a.cs, line 3 of the last commit (a removed line): rename\n   > value\n", request);
        Assert.EndsWith("\n\n" + page, request);
        // Saved again as version 2 with the marks only, and read back the same
        store.Update("old", d => d with { QuestionReply = "" }, flush: true);
        var saved = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(2, saved["Version"]!.GetValue<int>());
        Assert.Null(saved["Drafts"]![0]!["Composer"]!["PageComments"]);
        Assert.Null(saved["Drafts"]![0]!["Composer"]!["Comments"]);
        Assert.Equal(marks, DraftStore.ReadSnapshot(path).All.Single().Composer.Marks, new MarkComparer());
    }

    private sealed class MarkComparer : IEqualityComparer<LocalMark>
    {
        public bool Equals(LocalMark? a, LocalMark? b) => a is not null && b is not null
            && System.Text.Json.JsonSerializer.Serialize(a) == System.Text.Json.JsonSerializer.Serialize(b);
        public int GetHashCode(LocalMark m) => m.Id.GetHashCode(StringComparison.Ordinal);
    }

    [Fact]
    public void Flush_after_dispose_and_queued_debounce_are_safe()
    {
        var root = TestProcesses.TempDir("disposed-drafts");
        var store = new DraftStore(Path.Combine(root, "drafts.json"));
        store.Update("one", d => d with { Composer = DraftContent.Empty with { Text = "save at exit" } }, root);
        store.Dispose();
        store.Flush();
        store.Update("one", d => d with { Composer = DraftContent.Empty });
        using var reopened = new DraftStore(store.Path);
        Assert.Equal("save at exit", Assert.Single(reopened.All).Composer.Text);
    }

    [Fact]
    public void Typing_is_journaled_within_the_debounce_bound_measured_on_a_fake_clock()
    {
        var root = TestProcesses.TempDir("debounce-drafts");
        var clock = new ManualClock();
        using var store = new DraftStore(Path.Combine(root, "drafts.json"), clock);
        Assert.True(DraftStore.Debounce <= TimeSpan.FromMilliseconds(500));
        store.Update("one", d => d with { Composer = DraftContent.Empty with { Text = "a" } }, root);
        clock.Advance(DraftStore.Debounce - TimeSpan.FromMilliseconds(1));
        Assert.False(File.Exists(store.Path));
        // Each keystroke restarts the window; the last edit is on disk one window after it.
        store.Update("one", d => d with { Composer = DraftContent.Empty with { Text = "ab" } }, root);
        clock.Advance(DraftStore.Debounce);
        Assert.Equal("ab", Assert.Single(DraftStore.ReadSnapshot(store.Path!).All).Composer.Text);
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly List<Pending> _timers = [];
        private TimeSpan _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Pending(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var t in _timers.ToArray())
                if (t.Due is { } due && due <= _now) { t.Due = null; t.Fire(); }
        }
        private sealed class Pending(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan? Due;
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                return true;
            }
            public void Dispose() => Due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [AvaloniaFact]
    public async Task Exit_and_reopen_preserve_latest_text_images_pastes_and_code_comments_without_sending()
    {
        var project = TestProcesses.TempDir("draft-project");
        var settings = new ClientSettingsStore(Path.Combine(project, "config.json"));
        var (_, first) = Create(project, settings);
        first.ComposerText = "old text";
        first.ComposerText = "latest user edit";
        Assert.True(first.TryAddImage(Image(), "test"));
        Assert.True(first.TryAddPastedText("one\ntwo\nthree\nfour"));
        Assert.True(first.AddCodeMark(new CodeAnchor("code.cs", 7, true, "removed source"), "keep this comment"));
        await first.DisposeAsync();
        var (session, second) = Create(project, settings);
        try
        {
            Assert.Equal("latest user edit", second.ComposerText);
            Assert.Equal(Image().Data, Assert.Single(second.Attachments).Model.Data);
            Assert.Equal("one\ntwo\nthree\nfour", Assert.Single(second.PastedTexts).Text);
            Assert.Equal("keep this comment", Assert.Single(second.Marks.Items).Note);
            Assert.Equal(SessionPhase.NotStarted, session.Snapshot().Phase);
            Assert.Empty(session.Snapshot().Items.OfType<UserItem>());
            second.ComposerText = "edited after recovery";
            second.Attachments[0].RemoveCommand.Execute(null);
        }
        finally { await second.DisposeAsync(); }
        var (_, third) = Create(project, settings);
        Assert.Equal("edited after recovery", third.ComposerText);
        Assert.Empty(third.Attachments);
        await third.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Rpc_size_rejection_does_not_clear_any_composer_content_or_create_pending_delivery()
    {
        var project = TestProcesses.TempDir("large-draft");
        var settings = new ClientSettingsStore(Path.Combine(project, "config.json"));
        var (session, vm) = Create(project, settings);
        await using var _ = vm;
        await session.StartAsync();
        vm.Apply(session.Snapshot());
        var text = new string('"', 600_000); // JSON escaping exceeds the frame limit without hidden controls.
        vm.ComposerText = text;
        vm.TryAddImage(Image(), "test");
        vm.AddCodeMark(new CodeAnchor("a.cs", 1, false, "a"), "keep");
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(text, vm.ComposerText);
        Assert.Single(vm.Attachments);
        Assert.Single(vm.Marks.Items);
        Assert.Contains("at most", vm.ComposerMessage);
        Assert.Empty(session.Snapshot().Items.OfType<UserItem>());
    }

    [AvaloniaFact]
    public async Task Disk_failure_before_send_keeps_input_and_never_calls_runtime()
    {
        var project = TestProcesses.TempDir("draft-save-failure");
        var settings = new ClientSettingsStore(Path.Combine(project, "config.json"));
        Directory.CreateDirectory(DraftStore.PathFor(settings.Path)); // replacement cannot overwrite a directory
        var (session, vm) = Create(project, settings);
        await using var _ = vm;
        await session.StartAsync();
        vm.Apply(session.Snapshot());
        vm.ComposerText = "must not disappear";
        vm.TryAddImage(Image(), "test");
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal("must not disappear", vm.ComposerText);
        Assert.Single(vm.Attachments);
        Assert.NotEmpty(vm.ComposerMessage);
        Assert.Empty(session.Snapshot().Items.OfType<UserItem>());
    }

    [AvaloniaFact]
    public async Task Stop_returns_all_queued_images_beside_new_edits_and_does_not_redeliver_them()
    {
        var project = TestProcesses.TempDir("stop-drafts");
        var settings = new ClientSettingsStore(Path.Combine(project, "config.json"));
        var (session, vm) = Create(project, settings);
        await using var _ = vm;
        await session.StartAsync();
        vm.Apply(session.Snapshot());
        vm.ComposerText = "long task";
        await vm.SendCommand.ExecuteAsync(null);
        await TestProcesses.Eventually(session.Snapshot, s => s.Items.OfType<UserItem>().Any(u => u.Confirmed), Wait, "prompt delivered");
        vm.Apply(session.Snapshot());
        for (var i = 0; i < 2; i++)
        {
            vm.ComposerText = "queued " + i;
            for (var n = 0; n < 3; n++) vm.TryAddImage(Image($"{i}-{n}.png"), "test");
            await vm.SendCommand.ExecuteAsync(null);
        }
        vm.ComposerText = "new edit while running";
        await vm.AbortCommand.ExecuteAsync(null);
        Assert.StartsWith("new edit while running", vm.ComposerText);
        Assert.Contains("queued 0", vm.ComposerText);
        Assert.Contains("queued 1", vm.ComposerText);
        Assert.Equal(6, vm.Attachments.Count);
        Assert.Empty(vm.QueuedMessages);
        Assert.Equal(0, (await session.Connection!.GetStateAsync()).QueuedMessageCount);
        Assert.DoesNotContain(session.Snapshot().Items, i => i is UserItem u && u.Text.StartsWith("queued", StringComparison.Ordinal));
        var persisted = DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path));
        var saved = Assert.Single(persisted.All);
        Assert.Empty(saved.Queue);
        Assert.Equal(6, saved.Composer.Images.Length);
    }

    [AvaloniaFact]
    public async Task Runtime_crash_restores_queue_once_and_survives_application_reopen()
    {
        var project = TestProcesses.TempDir("crash-drafts");
        var settings = new ClientSettingsStore(Path.Combine(project, "config.json"));
        var (session, vm) = Create(project, settings);
        await session.StartAsync();
        vm.Apply(session.Snapshot());
        vm.ComposerText = "long task";
        await vm.SendCommand.ExecuteAsync(null);
        await TestProcesses.Eventually(session.Snapshot, s => s.Items.OfType<UserItem>().Any(u => u.Confirmed), Wait, "prompt delivered");
        vm.Apply(session.Snapshot());
        vm.ComposerText = "undelivered";
        vm.TryAddImage(Image(), "test");
        await vm.SendCommand.ExecuteAsync(null);
        vm.ComposerText = "in-progress edit";
        using (var process = Process.GetProcessById(session.ProcessId!.Value)) process.Kill();
        var crashed = await TestProcesses.Eventually(session.Snapshot, s => s.Phase == SessionPhase.Faulted, Wait, "runtime crashed");
        vm.Apply(crashed);
        vm.Apply(session.Snapshot());
        Assert.Equal("in-progress edit\n\nundelivered", vm.ComposerText);
        Assert.Single(vm.Attachments);
        var file = File.Exists(session.SessionFile) ? session.SessionFile : null;
        await session.RestartAsync(new LaunchRequest(project, file));
        vm.Apply(session.Snapshot());
        Assert.Equal(SessionPhase.Ready, session.Snapshot().Phase);
        Assert.Equal("in-progress edit\n\nundelivered", vm.ComposerText);
        Assert.Empty(session.Snapshot().Queued);
        Assert.DoesNotContain(session.Snapshot().Items, i => i is UserItem { Text: "undelivered" });
        await vm.DisposeAsync();
        var (restarted, recovered) = Create(project, settings, file);
        Assert.Equal("in-progress edit\n\nundelivered", recovered.ComposerText);
        Assert.Single(recovered.Attachments);
        Assert.Empty(restarted.Snapshot().Queued);
        Assert.Equal(SessionPhase.NotStarted, restarted.Snapshot().Phase);
        await recovered.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Application_restart_reads_checkpointed_queue_without_graceful_shutdown_or_resend()
    {
        var project = TestProcesses.TempDir("app-crash-drafts");
        var settings = new ClientSettingsStore(Path.Combine(project, "live.json"));
        var (session, vm) = Create(project, settings);
        await session.StartAsync();
        vm.Apply(session.Snapshot());
        vm.ComposerText = "long task";
        await vm.SendCommand.ExecuteAsync(null);
        await TestProcesses.Eventually(session.Snapshot, s => s.Items.OfType<UserItem>().Any(u => u.Confirmed), Wait, "prompt delivered");
        vm.Apply(session.Snapshot());
        vm.ComposerText = "queued at crash";
        vm.TryAddImage(Image(), "test");
        await vm.SendCommand.ExecuteAsync(null);
        // Copy only the on-disk checkpoint, with the original application still running. No Dispose/flush contributes.
        var restartedSettings = new ClientSettingsStore(Path.Combine(project, "restarted.json"));
        File.Copy(DraftStore.PathFor(settings.Path), DraftStore.PathFor(restartedSettings.Path));
        var resume = File.Exists(session.SessionFile) ? session.SessionFile : null;
        var (after, recovered) = Create(project, restartedSettings, resume);
        try
        {
            Assert.Equal("queued at crash", recovered.ComposerText);
            Assert.Single(recovered.Attachments);
            Assert.Empty(after.Snapshot().Queued);
            Assert.Empty(after.Snapshot().Items.OfType<UserItem>());
            Assert.Equal(SessionPhase.NotStarted, after.Snapshot().Phase);
        }
        finally { await recovered.DisposeAsync(); await vm.DisposeAsync(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoked_question_quotes_context_before_slash_or_bang_reply_and_keeps_current_edits(bool runtimeRevoked)
    {
        var project = TestProcesses.TempDir("reply-drafts");
        var (session, vm) = Create(project, new ClientSettingsStore(Path.Combine(project, "config.json")), scenario: "ask");
        await using var _ = vm;
        await session.StartAsync();
        vm.Apply(session.Snapshot());
        vm.ComposerText = "question";
        await vm.SendCommand.ExecuteAsync(null);
        var select = await TestProcesses.Eventually(session.Snapshot, s => s.Dialogs.Count == 1, Wait, "select question");
        await session.AnswerDialogAsync(select.Dialogs[0].Id, new DialogAnswer.Value("Blue"));
        var input = await TestProcesses.Eventually(session.Snapshot, s => s.Dialogs.Any(d => d.Kind == DialogKind.Input), Wait, "input question");
        vm.Apply(input);
        var question = vm.CurrentDialog!.Headline;
        vm.CurrentDialog.InputText = "/clear\n!rm important";
        vm.ComposerText = "existing composer edit";
        if (runtimeRevoked)
        {
            session.Mutate(s => s.CloseAllDialogs("revoked by omp"));
            vm.Apply(session.Snapshot());
        }
        else await vm.CurrentDialog.DismissCommand.ExecuteAsync(null);
        Assert.StartsWith("existing composer edit\n\n> ", vm.ComposerText);
        Assert.Contains(question, vm.ComposerText);
        Assert.Contains("\n/clear\n!rm important", vm.ComposerText);
        var once = vm.ComposerText;
        vm.Apply(session.Snapshot());
        Assert.Equal(once, vm.ComposerText);
        Assert.DoesNotContain(session.Snapshot().Items, i => i is UserItem { Text: "/clear\n!rm important" });
    }

    [AvaloniaFact]
    public async Task Saved_sessions_in_same_project_keep_distinct_drafts_and_new_chat_does_not_claim_them()
    {
        var project = TestProcesses.TempDir("separate-drafts");
        var settings = new ClientSettingsStore(Path.Combine(project, "config.json"));
        var firstFile = Path.Combine(project, "one.jsonl");
        var secondFile = Path.Combine(project, "two.jsonl");
        var (_, first) = Create(project, settings, firstFile);
        first.ComposerText = "draft one";
        await first.DisposeAsync();
        var (_, second) = Create(project, settings, secondFile);
        Assert.Equal("", second.ComposerText);
        second.ComposerText = "draft two";
        await second.DisposeAsync();
        var (_, reopened) = Create(project, settings, firstFile);
        Assert.Equal("draft one", reopened.ComposerText);
        await reopened.DisposeAsync();
        var (_, fresh) = Create(project, settings);
        Assert.Equal("", fresh.ComposerText);
        await fresh.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Authentication_input_is_not_written_into_the_draft_journal()
    {
        var project = TestProcesses.TempDir("auth-not-drafts");
        var settings = new ClientSettingsStore(Path.Combine(project, "config.json"));
        var (_, vm) = Create(project, settings);
        vm.ComposerText = "ordinary draft";
        var request = new PendingDialog("auth", DialogKind.Input, "Authentication code", null, [], null, null, DateTimeOffset.UtcNow, null);
        vm.CurrentDialog = new DialogViewModel(request, (_, _) => Task.CompletedTask)
        {
            PreserveReply = false, InputText = "secret-auth-code-not-a-draft",
        };
        await vm.DisposeAsync();
        var json = File.ReadAllText(DraftStore.PathFor(settings.Path));
        Assert.Contains("ordinary draft", json);
        Assert.DoesNotContain("secret-auth-code-not-a-draft", json);
    }

    [Fact]
    public void Truncated_stop_response_keeps_local_images_and_omitted_messages()
    {
        var state = new ConversationState();
        state.Enqueue(QueueKind.Steer, "same", [Image("first.png")]);
        state.Enqueue(QueueKind.Steer, "same", [Image("second.png")]);
        state.Enqueue(QueueKind.FollowUp, "omitted", [Image("third.png")]);
        state.RestoreQueue([new QueuedMessage(0, QueueKind.Steer, "same", [])], truncated: true, imagesDropped: true);
        Assert.Equal(["same", "same", "omitted"], state.Queued.Select(q => q.Text));
        Assert.Equal(["first.png", "second.png", "third.png"], state.Queued.Select(q => Assert.Single(q.Images).Name));
        Assert.All(state.Queued, q => Assert.True(q.Recovered));
        state.ResetTranscript();
        Assert.Equal(3, state.Queued.Count);
    }

    [Fact]
    public async Task Taking_back_identical_text_uses_the_actual_withdrawn_images_not_the_clicked_chips_sequence()
    {
        var project = TestProcesses.TempDir("duplicate-queue");
        await using var session = new SessionController(TestProcesses.FakeFactory("slow-stream", Path.Combine(project, "sessions")), new LaunchRequest(project));
        await session.StartAsync();
        await session.PromptAsync("long task");
        await session.QueueAsync(QueueKind.FollowUp, "same text", [new ImageAttachment("first.png", "image/png", [1])]);
        await session.QueueAsync(QueueKind.FollowUp, "same text", [new ImageAttachment("second.png", "image/png", [2])]);
        var clicked = session.Snapshot().Queued[1];
        var actual = await session.RemoveQueuedAsync(clicked, recover: true);
        Assert.Equal("first.png", Assert.Single(actual!).Name);
        var queued = session.Snapshot().Queued;
        Assert.Equal("first.png", Assert.Single(queued.Single(q => q.Recovered).Images).Name);
        Assert.Equal("second.png", Assert.Single(queued.Single(q => !q.Recovered).Images).Name);
        Assert.Equal(1, (await session.Connection!.GetStateAsync()).QueuedMessageCount);
        await session.AbortAsync();
        Assert.Equal(2, session.Snapshot().Queued.Count);
        Assert.All(session.Snapshot().Queued, q => Assert.True(q.Recovered));
    }

    [AvaloniaFact]
    public async Task Failed_saved_session_launch_releases_draft_for_same_window_retry_without_duplicate_identity_or_send()
    {
        var project = SessionCatalog.ResolveLinks(TestProcesses.TempDir("failed-draft-retry"));
        var settings = new ClientSettingsStore(Path.Combine(project, "config.json"));
        var file = Path.Combine(project, "2026-01-01T00-00-00-000Z_retry.jsonl");
        var title = System.Text.Json.JsonSerializer.Serialize(new { type = "title", v = 1, title = "Retry chat" }) + "\n";
        var damaged = title + System.Text.Json.JsonSerializer.Serialize(new { type = "session", version = 3, cwd = project }) + "\n";
        File.WriteAllText(file, damaged);
        const string draftId = "saved-draft-before-failed-launch";
        using (var store = new DraftStore(DraftStore.PathFor(settings.Path)))
            store.Update(draftId, d => d with { Composer = new DraftContent("saved reply", [Image()], [], []) }, project, file, flush: true);

        var (initial, vm) = Create(project, settings, scenario: "normal");
        try
        {
            await initial.StartAsync();
            vm.Apply(initial.Snapshot());
            vm.ComposerText = "keep the chat we leave";
            var target = new SessionItemViewModel(new SessionSummary(file, "retry", project, "Retry chat", DateTimeOffset.UtcNow));

            await vm.OpenSessionCommand.ExecuteAsync(target);
            Assert.Same(initial, vm.Session);
            Assert.Single(vm.OpenChats);
            Assert.Equal(SessionCardState.Failed, vm.SessionCard?.State);
            Assert.Equal("keep the chat we leave", vm.ComposerText);
            Assert.Equal(damaged, File.ReadAllText(file));
            {
                var afterFailure = DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path));
                var saved = Assert.Single(afterFailure.All, d => SessionCatalog.SamePath(d.SessionFile, file));
                Assert.Equal(draftId, saved.Id);
                Assert.Equal("saved reply", saved.Composer.Text);
                Assert.Equal(Image().Data, Assert.Single(saved.Composer.Images).Data);
            }

            // Repair the runtime prerequisite, then retry through the same sidebar command and the same window.
            File.WriteAllText(file, title + System.Text.Json.JsonSerializer.Serialize(new { type = "session", version = 3, id = "retry", cwd = project }) + "\n");
            await vm.OpenSessionCommand.ExecuteAsync(target);
            Assert.NotSame(initial, vm.Session);
            Assert.Equal(file, vm.Session.SessionFile);
            Assert.Equal(SessionPhase.Ready, vm.Phase);
            Assert.Equal("saved reply", vm.ComposerText);
            Assert.Equal(Image().Data, Assert.Single(vm.Attachments).Model.Data);
            Assert.Empty(vm.Session.Snapshot().Items.OfType<UserItem>());
            Assert.Empty(vm.Session.Snapshot().Queued);
            vm.ComposerText = "edited after retry";
        }
        finally { await vm.DisposeAsync(); }

        var afterRetry = DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path));
        var recovered = Assert.Single(afterRetry.All, d => SessionCatalog.SamePath(d.SessionFile, file));
        Assert.Equal(draftId, recovered.Id);
        Assert.Equal("edited after retry", recovered.Composer.Text);
        Assert.Equal(Image().Data, Assert.Single(recovered.Composer.Images).Data);
        Assert.Empty(recovered.Queue);
        Assert.Empty(recovered.Pending);
    }

    [AvaloniaFact]
    public async Task Opening_saved_chip_only_draft_in_untouched_chat_updates_bound_chips_and_send_button()
    {
        var project = SessionCatalog.ResolveLinks(TestProcesses.TempDir("restored-chip-bindings"));
        var settings = new ClientSettingsStore(Path.Combine(project, "config.json"));
        var file = Path.Combine(project, "2026-01-01T00-00-00-000Z_chips.jsonl");
        File.WriteAllText(file,
            System.Text.Json.JsonSerializer.Serialize(new { type = "title", v = 1, title = "Chip draft" }) + "\n" +
            System.Text.Json.JsonSerializer.Serialize(new { type = "session", version = 3, id = "chips", cwd = project }) + "\n");
        // A journal saved before marks (version 1): code comments structured, page comments as their request text
        File.WriteAllText(DraftStore.PathFor(settings.Path), new JsonObject
        {
            ["Version"] = 1,
            ["Drafts"] = new JsonArray(new JsonObject
            {
                ["Id"] = "chips-draft", ["Project"] = project, ["SessionFile"] = file,
                ["Composer"] = new JsonObject
                {
                    ["Text"] = "", ["Images"] = new JsonArray(), ["Pastes"] = new JsonArray("pasted\ncontent\nin\nfull"),
                    ["Comments"] = new JsonArray(new JsonObject { ["Path"] = "source.cs", ["Line"] = 7, ["RemovedLine"] = false, ["LineText"] = "source line", ["Comment"] = "code comment" }),
                    ["PageComments"] = "page comment with its context",
                },
                ["Queue"] = new JsonArray(), ["Pending"] = new JsonArray(), ["QuestionReply"] = "", ["RecoveredSequences"] = new JsonArray(),
            }),
        }.ToJsonString());

        var (initial, vm) = Create(project, settings, scenario: "normal");
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        window.Show();
        try
        {
            await initial.StartAsync();
            vm.Apply(initial.Snapshot());
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.FindControl<ItemsControl>("PastedTextList")!.IsVisible);
            Assert.False(window.FindControl<ItemsControl>("MarksList")!.IsVisible);
            Assert.False(window.FindControl<Button>("SendButton")!.IsEffectivelyEnabled);

            await vm.OpenSessionCommand.ExecuteAsync(new SessionItemViewModel(
                new SessionSummary(file, "chips", project, "Chip draft", DateTimeOffset.UtcNow)));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Same(initial, vm.Session); // exercises SyncDraftIdentity merge, not constructor resume
            Assert.Equal(file, vm.Session.SessionFile);
            Assert.Equal("", vm.ComposerText);
            // The page comments are a restored mark, not a paste: one paste, two marks, sent as they were saved
            Assert.Single(vm.PastedTexts);
            Assert.Equal(2, vm.Marks.Items.Count);
            Assert.Equal("code comment", vm.Marks.Items[0].Note);
            Assert.Equal("page comment with its context", vm.Marks.Items[1].Legacy);
            Assert.True(vm.HasPastedTexts);
            Assert.True(vm.Marks.HasMarks);
            Assert.True(vm.SendCommand.CanExecute(null));
            Assert.True(window.FindControl<ItemsControl>("PastedTextList")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<ItemsControl>("MarksList")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Button>("SendButton")!.IsEffectivelyEnabled);
            Assert.Empty(vm.Session.Snapshot().Items.OfType<UserItem>());
            Assert.Empty(vm.Session.Snapshot().Queued);
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }
}
