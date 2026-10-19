using System.Diagnostics;
using System.Text.Json;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

public sealed class PagedHistoryTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static SessionController Controller(string scenario = "history", int delay = 0)
    {
        var root = TestProcesses.TempDir("paged-history");
        return new SessionController(TestProcesses.Fake(scenario) with
        {
            Environment = new Dictionary<string, string?>
            {
                ["FAKE_SESSION_DIR"] = root,
                ["FAKE_HISTORY_PAGE_DELAY_MS"] = delay.ToString(),
            },
        });
    }

    [Fact]
    public async Task Startup_loads_only_recent_tail_and_older_pages_are_contiguous_without_duplicate_keys()
    {
        await using var controller = Controller();
        await controller.StartAsync();
        var tail = controller.Snapshot();
        Assert.Equal(new SessionHistory(100, 350, true), tail.History);
        Assert.Equal("history-250", Assert.IsType<UserItem>(tail.Items[0]).Text);
        Assert.Equal("history-349", Assert.IsType<AssistantItem>(tail.Items[^1]).Text);
        var original = tail.Items.ToDictionary(item => item.Key);

        await controller.LoadOlderMessagesAsync();
        var older = controller.Snapshot();
        Assert.Equal(200, older.History.LoadedMessages);
        Assert.Equal("history-150", Assert.IsType<UserItem>(older.Items[0]).Text);
        foreach (var item in older.Items.Skip(100)) Assert.Same(original[item.Key], item);
        await controller.LoadOlderMessagesAsync();
        await controller.LoadOlderMessagesAsync();
        var all = controller.Snapshot();
        Assert.False(all.History.HasOlder);
        Assert.Equal(350, all.Items.Count);
        Assert.Equal(350, all.Items.Select(item => item.Key).Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 350).Select(index => "history-" + index),
            all.Items.Select(item => item is UserItem user ? user.Text : ((AssistantItem)item).Text));
        await controller.LoadOlderMessagesAsync();
        Assert.Equal(all.Items, controller.Snapshot().Items);
        Assert.Equal(all.History, controller.Snapshot().History);
    }

    [Fact]
    public async Task Events_already_in_snapshot_are_drained_before_tail_hydration_and_not_replayed_as_duplicate_rows()
    {
        await using var controller = Controller("history-replay");
        await controller.StartAsync();
        var snapshot = controller.Snapshot();
        Assert.Equal(100, snapshot.Items.Count);
        Assert.Single(snapshot.Items.OfType<UserItem>(), item => item.Text == "history-348");
        Assert.Single(snapshot.Items.OfType<AssistantItem>(), item => item.Text == "history-349");
        Assert.Equal(0, snapshot.StreamMismatches);
        Assert.False(snapshot.History.IsLoading);
    }

    [Fact]
    public async Task Busy_does_not_discard_cursor_or_rows_and_next_explicit_attempt_can_continue()
    {
        await using var controller = Controller("history-busy");
        await controller.StartAsync();
        var original = controller.Snapshot();
        await controller.LoadOlderMessagesAsync();
        var busy = controller.Snapshot();
        Assert.Equal(original.Items, busy.Items);
        Assert.True(busy.History.HasOlder);
        Assert.False(busy.History.NeedsReload);
        Assert.False(busy.History.IsLoading);
        Assert.Contains("paused", busy.History.Error);
        await controller.LoadOlderMessagesAsync();
        Assert.Equal(200, controller.Snapshot().History.LoadedMessages);
    }

    [Fact]
    public async Task Busy_initial_history_is_explicit_and_idle_reload_fetches_only_the_tail()
    {
        await using var controller = Controller("history-start-busy");
        await controller.StartAsync();
        var busy = controller.Snapshot();
        Assert.Equal(SessionPhase.Ready, busy.Phase);
        Assert.Empty(busy.Items);
        Assert.True(busy.History.NeedsReload);
        Assert.False(busy.History.IsLoading);
        Assert.NotNull(busy.History.Error);
        await controller.ReloadHistoryAsync();
        Assert.Equal(new SessionHistory(100, 350, true), controller.Snapshot().History);
        Assert.Equal(100, controller.Snapshot().Items.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_and_metadata_after_get_state_survive_busy_start_or_session_switch(bool switching)
    {
        await using var controller = Controller(switching ? "history-live-switch" : "history-live-start");
        if (switching) await controller.StartAsync();
        var loading = switching ? controller.NewSessionAsync() : controller.StartAsync();
        await TestProcesses.Eventually(controller.Snapshot,
            snapshot => snapshot.DebugTail.Any(entry => entry.Type == "history_catalog_held"), Wait, "catalog barrier");
        // The fake cannot finish the catalog query until this explicit release, so the later events have
        // deterministically overtaken get_state while LoadSessionAsync is still awaiting preparation.
        Assert.Equal(SessionPhase.Running, controller.Snapshot().Phase);
        Assert.True((await controller.Connection!.RequestAsync("history_release_start")).Success);
        await loading.WaitAsync(Wait);
        var busy = controller.Snapshot();
        Assert.Equal(SessionPhase.Running, busy.Phase);
        Assert.Equal("new/model", busy.Model);
        Assert.Equal("high", busy.ThinkingLevel);
        Assert.Equal("new title", busy.SessionName);
        Assert.True(busy.History.NeedsReload);
        Assert.Equal("new streaming user", Assert.Single(busy.Items.OfType<UserItem>()).Text);
        var reply = Assert.Single(busy.Items.OfType<AssistantItem>());
        Assert.Equal("streaming response", reply.Text);
        Assert.True(reply.Streaming);
        Assert.True((await controller.Connection.RequestAsync("history_finish_start")).Success);
        await TestProcesses.Eventually(controller.Snapshot, snapshot => snapshot.Phase == SessionPhase.Ready, Wait, "run end");
        await controller.ReloadHistoryAsync();
        var reloaded = controller.Snapshot();
        Assert.False(reloaded.History.NeedsReload);
        Assert.Equal(switching ? 2 : 100, reloaded.History.LoadedMessages);
        Assert.Single(reloaded.Items.OfType<UserItem>(), user => user.Text == "new streaming user");
        Assert.Single(reloaded.Items.OfType<AssistantItem>(), assistant => assistant.Text == "streaming response");
    }

    [Fact]
    public async Task Stale_keeps_visible_rows_until_explicit_bounded_tail_reload_restores_browsing()
    {
        await using var controller = Controller("history-stale");
        await controller.StartAsync();
        var original = controller.Snapshot();
        await controller.LoadOlderMessagesAsync();
        var stale = controller.Snapshot();
        Assert.True(stale.History.NeedsReload);
        Assert.Equal(original.Items, stale.Items);
        await controller.LoadOlderMessagesAsync();
        Assert.Equal(stale.Items, controller.Snapshot().Items);
        Assert.True(controller.Snapshot().History.NeedsReload);
        await controller.ReloadHistoryAsync();
        var reloaded = controller.Snapshot();
        Assert.False(reloaded.History.NeedsReload);
        Assert.Equal(100, reloaded.History.LoadedMessages);
        Assert.NotEqual(original.TranscriptEpoch, reloaded.TranscriptEpoch);
        await controller.LoadOlderMessagesAsync();
        Assert.Equal(200, controller.Snapshot().History.LoadedMessages);
    }

    [Fact]
    public async Task A_live_turn_during_older_request_keeps_each_live_and_historical_row_once_then_exposes_stale_reload()
    {
        await using var controller = Controller(delay: 250);
        await controller.StartAsync();
        var first = controller.LoadOlderMessagesAsync();
        await TestProcesses.Eventually(controller.Snapshot, state => state.History.IsLoading, Wait, "older request starts");
        var duplicate = controller.LoadOlderMessagesAsync();
        await controller.PromptAsync("new live message");
        await Task.WhenAll(first, duplicate);
        var live = await TestProcesses.Eventually(controller.Snapshot, state => state.Phase == SessionPhase.Ready && state.MessagesCompleted == 1, Wait, "live turn completes");
        Assert.Equal(200, live.History.LoadedMessages);
        Assert.Equal(200, live.Items.Count(item => item is UserItem { Text: var text } && text.StartsWith("history-") ||
            item is AssistantItem { Text: var reply } && reply.StartsWith("history-")));
        Assert.Single(live.Items.OfType<UserItem>(), item => item.Text == "new live message");
        Assert.Single(live.Items.OfType<AssistantItem>(), item => item.Text.StartsWith("tok0"));
        await controller.LoadOlderMessagesAsync();
        Assert.True(controller.Snapshot().History.NeedsReload);
        await controller.ReloadHistoryAsync();
        var recent = controller.Snapshot();
        Assert.Equal(100, recent.History.LoadedMessages);
        Assert.Single(recent.Items.OfType<UserItem>(), item => item.Text == "new live message");
        await controller.LoadOlderMessagesAsync();
        Assert.Equal(200, controller.Snapshot().History.LoadedMessages);
    }

    [Fact]
    public async Task A_live_turn_during_reload_is_never_replaced_by_the_earlier_tail_snapshot()
    {
        await using var controller = Controller(delay: 250);
        await controller.StartAsync();
        var epoch = controller.Snapshot().TranscriptEpoch;
        var reload = controller.ReloadHistoryAsync();
        await TestProcesses.Eventually(controller.Snapshot, state => state.History.IsLoading, Wait, "reload starts");
        await controller.PromptAsync("keep this live message");
        await reload;
        var final = await TestProcesses.Eventually(controller.Snapshot, state => state.Phase == SessionPhase.Ready && state.MessagesCompleted == 1, Wait, "live turn completes");
        Assert.Equal(epoch, final.TranscriptEpoch);
        Assert.Single(final.Items.OfType<UserItem>(), item => item.Text == "keep this live message");
        Assert.Single(final.Items.OfType<AssistantItem>(), item => item.Text.StartsWith("tok0"));
        Assert.False(final.History.IsLoading);
        Assert.NotNull(final.History.Error);
    }

    [Fact]
    public async Task Unrelated_metadata_updates_do_not_make_idle_history_reload_unavailable()
    {
        await using var controller = Controller(delay: 250);
        await controller.StartAsync();
        var epoch = controller.Snapshot().TranscriptEpoch;
        var reload = controller.ReloadHistoryAsync();
        await TestProcesses.Eventually(controller.Snapshot, state => state.History.IsLoading, Wait, "reload starts");
        await controller.RenameSessionAsync("renamed while loading");
        await reload;
        var final = controller.Snapshot();
        Assert.NotEqual(epoch, final.TranscriptEpoch);
        Assert.Equal("renamed while loading", final.SessionName);
        Assert.Equal(new SessionHistory(100, 350, true), final.History);
    }

    [Fact]
    public async Task Switch_waits_for_inflight_page_and_never_inserts_old_rows_into_new_session()
    {
        await using var controller = Controller(delay: 250);
        await controller.StartAsync();
        var saved = controller.Snapshot();
        var older = controller.LoadOlderMessagesAsync();
        await TestProcesses.Eventually(controller.Snapshot, state => state.History.IsLoading, Wait, "older request starts");
        var fresh = controller.NewSessionAsync();
        await Task.WhenAll(older, fresh);
        var empty = controller.Snapshot();
        Assert.NotEqual(saved.SessionId, empty.SessionId);
        Assert.Empty(empty.Items);
        Assert.Equal(new SessionHistory(), empty.History);
        await controller.OpenSessionAsync(saved.SessionFile!, saved.Cwd!);
        Assert.Equal(saved.SessionId, controller.Snapshot().SessionId);
        Assert.Equal(100, controller.Snapshot().History.LoadedMessages);
        Assert.Equal("history-250", Assert.IsType<UserItem>(controller.Snapshot().Items[0]).Text);
    }

    [Fact]
    public async Task Cancellation_releases_history_single_flight_without_clearing_existing_rows()
    {
        await using var controller = Controller(delay: 250);
        await controller.StartAsync();
        var original = controller.Snapshot();
        using var cancel = new CancellationTokenSource();
        var loading = controller.LoadOlderMessagesAsync(cancel.Token);
        await TestProcesses.Eventually(controller.Snapshot, state => state.History.IsLoading, Wait, "older request starts");
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.False(controller.Snapshot().History.IsLoading);
        Assert.Equal(original.Items, controller.Snapshot().Items);
        await controller.LoadOlderMessagesAsync();
        Assert.Equal(200, controller.Snapshot().History.LoadedMessages);
    }

    [Theory]
    [InlineData("history-malformed", false)]
    [InlineData("history-malformed", true)]
    [InlineData("history-bad-cursor", false)]
    [InlineData("history-bad-cursor", true)]
    public async Task Malformed_page_keeps_rows_and_epoch_and_does_not_wedge_the_next_attempt(string scenario, bool reload)
    {
        await using var controller = Controller(scenario);
        await controller.StartAsync();
        var original = controller.Snapshot();
        if (reload) await controller.ReloadHistoryAsync();
        else await controller.LoadOlderMessagesAsync();
        var failed = controller.Snapshot();
        Assert.Equal(original.TranscriptEpoch, failed.TranscriptEpoch);
        Assert.Equal(original.Items, failed.Items);
        Assert.False(failed.History.IsLoading);
        Assert.NotNull(failed.History.Error);
        if (reload) await controller.ReloadHistoryAsync();
        else await controller.LoadOlderMessagesAsync();
        var retried = controller.Snapshot();
        Assert.False(retried.History.IsLoading);
        Assert.Null(retried.History.Error);
        Assert.Equal(reload ? 100 : 200, retried.History.LoadedMessages);
    }

    [Fact]
    public async Task Unmodified_forward_only_runtime_fails_actionably_instead_of_downloading_full_history()
    {
        await using var controller = Controller("history-incompatible");
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => controller.StartAsync());
        Assert.Contains("managed runtime", error.Message);
        Assert.Contains("Full history was not downloaded", error.Message);
        Assert.Empty(controller.Snapshot().Items);
    }

    [Fact]
    public void Tool_result_at_page_boundary_is_visible_then_gains_older_call_arguments_without_duplicate_or_lost_live_text()
    {
        var state = new ConversationState();
        state.Hydrate(Json("""[{"role":"toolResult","toolCallId":"call","toolName":"read","content":[{"type":"text","text":"file contents"}],"isError":false},{"role":"assistant","content":"after tool"}]"""));
        var result = Assert.IsType<ToolItem>(state.Snapshot().Items[0]);
        Assert.Equal(ToolStatus.Succeeded, result.Status);
        Assert.Equal("file contents", result.Output);
        Apply(state, """{"type":"message_start","message":{"role":"assistant","content":[]}}""");
        Apply(state, """{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"live "}}""");
        var liveKey = state.Snapshot().Items[^1].Key;
        state.PrependHistory(Json("""[{"role":"user","content":"before tool"},{"role":"assistant","content":[{"type":"text","text":"reading"},{"type":"toolCall","id":"call","name":"read","arguments":{"path":"file.cs"}}]}]"""));
        Apply(state, """{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"continues"}}""");
        var snapshot = state.Snapshot();
        var tool = Assert.Single(snapshot.Items.OfType<ToolItem>());
        Assert.Equal(result.Key, tool.Key);
        Assert.Equal("file contents", tool.Output);
        Assert.Contains("file.cs", tool.Args);
        Assert.Equal(ToolStatus.Succeeded, tool.Status);
        Assert.Equal(result.Key, snapshot.Items[2].Key);
        Assert.Equal(liveKey, snapshot.Items[^1].Key);
        Assert.Equal("live continues", Assert.IsType<AssistantItem>(snapshot.Items[^1]).Text);
    }

    [Fact]
    public void Recent_tail_replacement_keeps_nonpersisted_warnings_and_recovered_input()
    {
        var state = new ConversationState();
        state.AddNotice(NoticeLevel.Warning, "startup warning");
        state.Enqueue(QueueKind.FollowUp, "kept input", []);
        var before = state.Snapshot();
        state.ReplaceHistory(Json("""[{"role":"user","content":"recent"}]"""));
        var after = state.Snapshot();
        Assert.Equal(before.Queued, after.Queued);
        Assert.Same(before.Items[0], after.Items[^1]);
        Assert.Equal("recent", Assert.IsType<UserItem>(after.Items[0]).Text);
    }

    [Theory]
    [InlineData(2, 3, 4, null)] // older rows omitted with no continuation
    [InlineData(0, 1, 4, "cursor")] // continuation after the beginning
    [InlineData(2, 2, 4, "cursor")] // no advancement and count disagrees
    [InlineData(3, 2, 4, "cursor")] // backwards range
    [InlineData(0, 1, 0, null)] // extends beyond total
    public void Invalid_page_boundaries_are_rejected_before_hydration(int start, int end, int total, string? cursor)
    {
        var data = JsonSerializer.SerializeToElement(new { messages = new[] { new { role = "user", content = "one" } },
            direction = "backward", startOffset = start, endOffset = end, totalMessages = total, nextCursor = cursor,
            sessionId = "session", snapshotId = "snapshot" });
        Assert.Throws<InvalidDataException>(() => OmpMessagesPage.Parse(new RpcResponse("get_messages_page", true, null, data, new RpcTimings())));
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void Apply(ConversationState state, string json)
    {
        var data = Json(json);
        state.Apply(new RpcFrame(data.GetProperty("type").GetString()!, null, data, Stopwatch.GetTimestamp(), json.Length), DateTimeOffset.UtcNow);
    }
}
