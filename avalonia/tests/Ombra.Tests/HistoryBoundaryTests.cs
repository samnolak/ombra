using System.Text.Json;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

public sealed class HistoryBoundaryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");

    [Fact]
    public void Irc_arriving_after_the_tail_snapshot_is_not_erased_by_its_later_application()
    {
        var state = new ConversationState();
        state.Hydrate(Json("""[{"role":"user","content":"already visible"}]"""));
        Apply(state, """{"type":"irc_message","message":{"role":"custom","customType":"irc:incoming","display":true,"details":{"from":"peer","message":"late answer"}}}""", 101);
        var before = state.Snapshot();
        Assert.False(state.TryReplaceHistory(Json("""[{"role":"assistant","content":"older snapshot"}]"""), 100));
        var after = state.Snapshot();
        Assert.Equal(before.TranscriptEpoch, after.TranscriptEpoch);
        Assert.Equal(before.Items, after.Items);
        Assert.Equal("late answer", Assert.Single(after.Items.OfType<AgentMessageItem>()).Text);
    }

    [Theory]
    [InlineData("agent_end")]
    [InlineData("session_settled")]
    [InlineData("prompt_result")]
    public void Terminal_events_after_snapshot_keep_the_final_reply_and_turn_summary(string type)
    {
        var clock = new HistoryClock();
        var state = new ConversationState(clock);
        Apply(state, """{"type":"agent_start"}""", 90);
        Apply(state, """{"type":"message_start","message":{"role":"assistant","content":[]}}""", 91);
        Apply(state, """{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"completed live reply"}}""", 92);
        var terminal = JsonSerializer.Serialize(new { type, agentInvoked = false, isTerminal = true });
        clock.Timestamp = TimeSpan.FromSeconds(5).Ticks;
        Apply(state, terminal, 101, Now.AddSeconds(5));
        var ended = state.Snapshot();
        Assert.Equal(SessionPhase.Ready, ended.Phase);
        Assert.Single(ended.Items.OfType<TurnEndItem>());
        Assert.False(state.TryReplaceHistory(Json("[]"), 100));
        Assert.Equal(ended.Items, state.Snapshot().Items);
        Assert.Equal("completed live reply", Assert.Single(state.Snapshot().Items.OfType<AssistantItem>()).Text);
    }

    [Fact]
    public void Newer_phase_and_metadata_events_win_over_get_state_while_history_loads()
    {
        var state = new ConversationState();
        Apply(state, """{"type":"agent_start"}""", 101);
        Apply(state, """{"type":"config_update","model":{"provider":"new","id":"model"},"thinkingLevel":"high"}""", 102);
        Apply(state, """{"type":"session_info_update","title":"new title"}""", 103);
        state.ApplyHistorySessionMetadata(StateAt(100), Now);
        var snapshot = state.Snapshot();
        Assert.Equal(SessionPhase.Running, snapshot.Phase);
        Assert.Equal("new/model", snapshot.Model);
        Assert.Equal("high", snapshot.ThinkingLevel);
        Assert.Equal("new title", snapshot.SessionName);
        // The session_busy branch only settles Starting, so the sole already-applied agent_start remains authoritative.
        state.SetHistory(new SessionHistory(NeedsReload: true, Error: "History is paused while omp is working."));
        Assert.Equal(SessionPhase.Running, state.Snapshot().Phase);
    }

    [Fact]
    public void Metadata_events_older_than_get_state_do_not_block_its_authoritative_values()
    {
        var state = new ConversationState();
        Apply(state, """{"type":"config_update","model":{"provider":"older","id":"event"},"thinkingLevel":"high"}""", 90);
        Apply(state, """{"type":"session_info_update","title":"older event title"}""", 91);
        state.ApplyHistorySessionMetadata(StateAt(100), Now);
        var snapshot = state.Snapshot();
        Assert.Equal("snapshot/model", snapshot.Model);
        Assert.Equal("low", snapshot.ThinkingLevel);
        Assert.Equal("snapshot title", snapshot.SessionName);
    }

    [Fact]
    public void Busy_session_switch_discards_old_rows_but_retains_new_run_rows_and_builders()
    {
        var state = new ConversationState();
        state.Hydrate(Json("""[{"role":"user","content":"old session"},{"role":"assistant","content":"old reply"}]"""));
        var boundary = state.LastTranscriptKey;
        Apply(state, """{"type":"agent_start"}""", 101);
        Apply(state, """{"type":"message_start","message":{"role":"user","content":"new session"}}""", 102);
        Apply(state, """{"type":"message_start","message":{"role":"assistant","content":[]}}""", 103);
        Apply(state, """{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"new "}}""", 104);
        var liveKey = state.Snapshot().Items[^1].Key;
        state.ResetHistorySession(boundary);
        state.ApplyHistorySessionMetadata(StateAt(100), Now);
        Apply(state, """{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"reply"}}""", 105);
        var snapshot = state.Snapshot();
        Assert.Equal(SessionPhase.Running, snapshot.Phase);
        Assert.Equal("new session", Assert.Single(snapshot.Items.OfType<UserItem>()).Text);
        var reply = Assert.Single(snapshot.Items.OfType<AssistantItem>());
        Assert.Equal(liveKey, reply.Key);
        Assert.Equal("new reply", reply.Text);
        Assert.True(reply.Streaming);
    }

    [Fact]
    public void Malformed_tail_is_rejected_before_rows_epoch_or_queue_are_changed()
    {
        var state = new ConversationState { Cwd = Environment.CurrentDirectory };
        state.Hydrate(Json("""[{"role":"user","content":"keep me"}]"""));
        state.Enqueue(QueueKind.FollowUp, "keep queued", []);
        var before = state.Snapshot();
        var malformed = JsonSerializer.SerializeToElement(new object[] { new { role = "user", content = "uncommitted" }, BadToolMessage() });
        Assert.Throws<InvalidDataException>(() => state.ReplaceHistory(malformed));
        var after = state.Snapshot();
        Assert.Equal(before.TranscriptEpoch, after.TranscriptEpoch);
        Assert.Equal(before.Queued, after.Queued);
        Assert.Same(before.Items[0], Assert.Single(after.Items));
        state.ReplaceHistory(Json("""[{"role":"user","content":"retry worked"}]"""));
        Assert.Equal("retry worked", Assert.IsType<UserItem>(Assert.Single(state.Snapshot().Items)).Text);
    }

    [Fact]
    public void Failed_older_page_cannot_partially_reconcile_an_existing_orphan_tool()
    {
        var state = new ConversationState { Cwd = Environment.CurrentDirectory };
        state.Hydrate(Json("""[{"role":"toolResult","toolCallId":"call","toolName":"read","content":"kept output","isError":false}]"""));
        var before = state.Snapshot();
        var call = new { role = "assistant", content = new[] { new { type = "toolCall", id = "call", name = "read", arguments = new { path = "file.cs" } } } };
        Assert.Throws<InvalidDataException>(() => state.PrependHistory(JsonSerializer.SerializeToElement(new object[] { call, BadToolMessage() })));
        var failed = Assert.IsType<ToolItem>(Assert.Single(state.Snapshot().Items));
        Assert.Same(before.Items[0], failed);
        Assert.Equal("", failed.Args);
        Assert.Equal(before.TranscriptEpoch, state.Snapshot().TranscriptEpoch);
        state.PrependHistory(JsonSerializer.SerializeToElement(new[] { call }));
        var retry = Assert.IsType<ToolItem>(Assert.Single(state.Snapshot().Items));
        Assert.Equal(failed.Key, retry.Key);
        Assert.Contains("file.cs", retry.Args);
        Assert.Equal("kept output", retry.Output);
        Assert.Equal(ToolStatus.Succeeded, retry.Status);
    }

    [Theory]
    [InlineData(2049)]
    [InlineData(1048577)]
    public void Oversized_received_cursor_is_rejected_before_it_can_poison_an_outgoing_request(int length)
    {
        Assert.Throws<InvalidDataException>(() => PageWithCursor(new string('a', length)));
    }

    [Theory]
    [InlineData("with padding=")]
    [InlineData("cursor/with/slash")]
    [InlineData("non-ascii-漢")]
    public void Received_cursor_must_have_the_owned_opaque_cursor_alphabet(string cursor)
    {
        Assert.Throws<InvalidDataException>(() => PageWithCursor(cursor));
    }

    private static OmpMessagesPage PageWithCursor(string cursor)
    {
        var data = JsonSerializer.SerializeToElement(new { messages = new[] { new { role = "user", content = "one" } },
            direction = "backward", startOffset = 1, endOffset = 2, totalMessages = 2, nextCursor = cursor,
            sessionId = "session", snapshotId = "snapshot" });
        return OmpMessagesPage.Parse(new RpcResponse("get_messages_page", true, null, data, new RpcTimings()));
    }

    private static object BadToolMessage() => new { role = "assistant", content = new[]
    {
        new { type = "toolCall", id = "bad", name = "write", arguments = new
        {
            path = Environment.CurrentDirectory + Path.DirectorySeparatorChar + "bad\0path", content = "invalid",
        } },
    } };

    private static OmpSessionState StateAt(long timestamp) =>
        new("snapshot", "model", "Snapshot", false, "session", "session.jsonl", 2, "low", SessionName: "snapshot title")
        { Timings = new RpcTimings { ResponseRead = timestamp } };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class HistoryClock : TimeProvider
    {
        public long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
    }

    private static void Apply(ConversationState state, string json, long timestamp, DateTimeOffset? now = null)
    {
        var data = Json(json);
        state.Apply(new RpcFrame(data.GetProperty("type").GetString()!, null, data, timestamp, json.Length), now ?? Now);
    }
}
