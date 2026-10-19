using System.Diagnostics;
using System.Text.Json;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

/// <summary><c>queue_update</c> / <c>get_state.queuedMessages</c> drive the chips; <c>messageId</c> routes message frames to rows.</summary>
public sealed class EngineQueueAndMessageIdTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-10-18T12:00:00Z");
    private static long s_read = Stopwatch.GetTimestamp();

    private static RpcFrame F(string json)
    {
        var e = JsonDocument.Parse(json).RootElement.Clone();
        return new RpcFrame(e.GetProperty("type").GetString()!, null, e, Interlocked.Increment(ref s_read), json.Length + 1);
    }

    private static RpcFrame Queue(string[] steering, string[] followUp) =>
        F(JsonSerializer.Serialize(new { type = "queue_update", steering, followUp }));

    private static ImageAttachment Image(string name) => new(name, "image/png", [1, 2, 3]);

    private static string Delta(string id, string text) =>
        $$$"""{"type":"message_update","messageId":"{{{id}}}","message":{"role":"assistant"},"assistantMessageEvent":{"type":"text_delta","delta":"{{{text}}}"}}""";

    [Fact]
    public void A_delta_for_message_A_arriving_after_message_B_started_goes_to_As_row()
    {
        var s = new ConversationState();
        s.Apply(F("""{"type":"message_start","messageId":"m-a","message":{"role":"assistant","content":[]}}"""), T);
        s.Apply(F(Delta("m-a", "Hello ")), T);
        s.Apply(F("""{"type":"message_start","messageId":"m-b","message":{"role":"assistant","content":[]}}"""), T);
        s.Apply(F(Delta("m-b", "second")), T);
        s.Apply(F(Delta("m-a", "world")), T);

        var rows = s.Snapshot().Items.OfType<AssistantItem>().ToArray();
        Assert.Equal(["Hello world", "second"], rows.Select(r => r.Text));
        Assert.All(rows, r => Assert.True(r.Streaming));

        s.Apply(F("""{"type":"message_end","messageId":"m-a","message":{"role":"assistant","content":[{"type":"text","text":"Hello world"}],"stopReason":"stop"}}"""), T);
        rows = s.Snapshot().Items.OfType<AssistantItem>().ToArray();
        Assert.False(rows[0].Streaming);
        Assert.True(rows[1].Streaming);
        s.Apply(F(Delta("m-b", " reply")), T);
        s.Apply(F("""{"type":"message_end","messageId":"m-b","message":{"role":"assistant","content":[{"type":"text","text":"second reply"}],"stopReason":"stop"}}"""), T);
        Assert.Equal(["Hello world", "second reply"], s.Snapshot().Items.OfType<AssistantItem>().Select(r => r.Text));
        Assert.Equal(0, s.Snapshot().StreamMismatches);
    }

    [Fact]
    public void Frames_without_messageId_keep_the_single_streaming_row()
    {
        var s = new ConversationState();
        s.Apply(F("""{"type":"message_start","message":{"role":"assistant","content":[]}}"""), T);
        s.Apply(F("""{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"one"}}"""), T);
        s.Apply(F("""{"type":"message_start","message":{"role":"assistant","content":[]}}"""), T);
        s.Apply(F("""{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"two"}}"""), T);
        var rows = s.Snapshot().Items.OfType<AssistantItem>().ToArray();
        Assert.Equal(["one", "two"], rows.Select(r => r.Text));
        Assert.False(rows[0].Streaming);
    }

    [Fact]
    public void Queue_chips_mirror_queue_update_including_removal_by_the_engine()
    {
        var s = new ConversationState();
        s.SetPhase(SessionPhase.Running, T);
        var local = s.Enqueue(QueueKind.FollowUp, "mine", [Image("shot.png")]);

        // A snapshot from before omp admitted it never drops the optimistic echo.
        s.Apply(Queue([], []), T);
        Assert.Equal(["mine"], s.Snapshot().Queued.Select(q => q.Text));

        // omp lists it (keeps the local images) and an entry queued outside this client appears.
        s.Apply(Queue(["from extension"], ["mine"]), T);
        var queued = s.Snapshot().Queued;
        Assert.Equal([("mine", QueueKind.FollowUp), ("from extension", QueueKind.Steer)], queued.Select(q => (q.Text, q.Kind)));
        Assert.Equal(local.Seq, queued[0].Seq);
        Assert.Equal("shot.png", Assert.Single(queued[0].Images).Name);

        // omp promoted it to steering: the same chip follows.
        s.Apply(Queue(["from extension", "mine"], []), T);
        Assert.Equal(QueueKind.Steer, s.Snapshot().Queued.Single(q => q.Seq == local.Seq).Kind);

        // omp removed both (another client withdrew them): the chips go.
        s.Apply(Queue([], []), T);
        Assert.Empty(s.Snapshot().Queued);
    }

    [Fact]
    public void A_delivered_chip_is_not_resurrected_by_a_snapshot_written_before_its_removal()
    {
        var s = new ConversationState();
        s.SetPhase(SessionPhase.Running, T);
        s.Apply(F("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":"first"}]}}"""), T);
        s.Enqueue(QueueKind.FollowUp, "next", []);
        s.Enqueue(QueueKind.FollowUp, "after", []);
        s.Apply(Queue([], ["next", "after"]), T);
        s.Apply(F("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":"next"}]}}"""), T);
        Assert.Equal(["after"], s.Snapshot().Queued.Select(q => q.Text));
        s.Apply(Queue([], ["next", "after", "third"]), T);
        Assert.Equal(["after", "third"], s.Snapshot().Queued.Select(q => q.Text));
    }

    [Fact]
    public void An_image_sent_without_text_is_the_entry_omp_lists_as_image()
    {
        var s = new ConversationState();
        s.SetPhase(SessionPhase.Running, T);
        s.Apply(F("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":"first"}]}}"""), T);
        var local = s.Enqueue(QueueKind.FollowUp, "", [Image("shot.png")]);

        // omp names it "[Image]" (queueChipText): that is the local entry, with its image, not a second chip
        s.Apply(Queue([], ["[Image]"]), T);
        var chip = Assert.Single(s.Snapshot().Queued);
        Assert.Equal((local.Seq, ""), (chip.Seq, chip.Text));
        Assert.Equal("shot.png", Assert.Single(chip.Images).Name);

        // promoted by omp: the same chip follows
        s.Apply(Queue(["[Image]"], []), T);
        chip = Assert.Single(s.Snapshot().Queued);
        Assert.Equal((local.Seq, QueueKind.Steer), (chip.Seq, chip.Kind));

        // delivered: a snapshot still listing it does not bring it back as an "[Image]" chip
        s.Apply(F("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":""},{"type":"image","mimeType":"image/png","data":"AQID"}]}}"""), T);
        Assert.Empty(s.Snapshot().Queued);
        s.Apply(Queue(["[Image]"], []), T);
        Assert.Empty(s.Snapshot().Queued);
        var delivered = s.Snapshot().Items.OfType<UserItem>().Last();
        Assert.Equal("", delivered.Text);
        Assert.Equal("shot.png", Assert.Single(delivered.Images).Name);

        // Text typed as "[Image]" is matched as text; an "[Image]" queued elsewhere stays a chip of its own
        s.Apply(Queue([], []), T);
        var typed = s.Enqueue(QueueKind.FollowUp, "[Image]", []);
        var image = s.Enqueue(QueueKind.FollowUp, "", [Image("b.png")]);
        s.Apply(Queue([], ["[Image]", "[Image]", "[Image]"]), T);
        var queued = s.Snapshot().Queued;
        Assert.Equal(3, queued.Count);
        Assert.Equal([typed.Seq, image.Seq], queued.Take(2).Select(q => q.Seq));
        Assert.Equal(("[Image]", 0), (queued[2].Text, queued[2].Images.Count));
    }

    [Fact]
    public void An_expanded_delivery_echo_removes_only_the_chip_the_engine_dropped()
    {
        var s = new ConversationState();
        s.SetPhase(SessionPhase.Running, T);
        s.Apply(F("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":"first"}]}}"""), T);
        s.Enqueue(QueueKind.FollowUp, "/tmpl", []);
        s.Enqueue(QueueKind.FollowUp, "keep me", []);
        s.Apply(Queue([], ["/tmpl", "keep me"]), T);
        s.Apply(Queue([], ["keep me"]), T);
        s.Apply(F("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":"expanded template"}]}}"""), T);
        Assert.Equal(["keep me"], s.Snapshot().Queued.Select(q => q.Text));
    }

    [Fact]
    public void A_stop_that_takes_the_queue_back_holds_removals_until_the_answer()
    {
        var s = new ConversationState();
        s.SetPhase(SessionPhase.Running, T);
        s.Enqueue(QueueKind.FollowUp, "keep", [Image("a.png")]);
        s.Apply(Queue([], ["keep"]), T);
        s.HoldQueueRemovals = true;
        s.Apply(Queue([], []), T);
        Assert.Equal(["keep"], s.Snapshot().Queued.Select(q => q.Text));
        s.RestoreQueue([new QueuedMessage(0, QueueKind.FollowUp, "keep", [Image("a.png")])], truncated: false);
        s.HoldQueueRemovals = false;
        Assert.True(Assert.Single(s.Snapshot().Queued).Recovered);
    }

    [Fact]
    public void Get_state_reports_queued_messages_and_an_older_read_is_ignored()
    {
        var state = OmpSessionState.Parse(JsonDocument.Parse(
            """{"isStreaming":true,"queuedMessages":{"steering":["s"],"followUp":["f1","f2"]}}""").RootElement);
        var (steering, followUp) = state.QueuedMessages!.Value;
        Assert.Equal(["s"], steering);
        Assert.Equal(["f1", "f2"], followUp);
        Assert.Null(OmpSessionState.Parse(JsonDocument.Parse("""{"isStreaming":false}""").RootElement).QueuedMessages);

        var s = new ConversationState();
        s.ApplyEngineQueue(steering, followUp, readAt: 10);
        Assert.Equal(["s", "f1", "f2"], s.Snapshot().Queued.Select(q => q.Text));
        s.ApplyEngineQueue([], [], readAt: 5);
        Assert.Equal(3, s.Snapshot().Queued.Count);
        s.ApplyEngineQueue([], ["f2"], readAt: 11);
        Assert.Equal(["f2"], s.Snapshot().Queued.Select(q => q.Text));
    }
}
