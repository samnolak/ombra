using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

public sealed class QueueRecoveryTests
{
    private static ImageAttachment Image(string name, byte data, string mime = "image/png") => new(name, mime, [data]);
    private static QueuedMessage Returned(QueueKind kind, string text, params ImageAttachment[] images) => new(0, kind, text, images);

    [Theory]
    [InlineData(QueueKind.Steer)]
    [InlineData(QueueKind.FollowUp)]
    public void Stop_matches_second_duplicate_by_image_payload_not_first_text_or_filename(QueueKind kind)
    {
        var state = new ConversationState();
        state.Enqueue(kind, "same", [Image("same.png", 1)]);
        var second = state.Enqueue(kind, "same", [Image("same.png", 2)], "second-submission");

        // The first was delivered but its event has not reached this client. Only the second was withdrawn.
        state.RestoreQueue([Returned(kind, "same", Image("image 1", 2))], truncated: false);

        var recovered = Assert.Single(state.Queued);
        Assert.Equal(second.Seq, recovered.Seq);
        Assert.Equal("second-submission", recovered.SubmissionId);
        Assert.Same(second.Images, recovered.Images);
        Assert.True(recovered.Recovered);
    }

    [Fact]
    public void Identical_bytes_with_different_local_names_preserve_both_legitimate_entries()
    {
        var state = new ConversationState();
        var first = state.Enqueue(QueueKind.FollowUp, "same", [Image("first.png", 1)]);
        var second = state.Enqueue(QueueKind.FollowUp, "same", [Image("second.png", 1)]);

        state.RestoreQueue([
            Returned(QueueKind.FollowUp, "same", Image("image 1", 1)),
            Returned(QueueKind.FollowUp, "same", Image("image 1", 1)),
        ], truncated: false);

        Assert.Equal([first.Seq, second.Seq], state.Queued.Select(q => q.Seq));
        Assert.Same(first.Images, state.Queued[0].Images);
        Assert.Same(second.Images, state.Queued[1].Images);
        Assert.All(state.Queued, q => Assert.True(q.Recovered));
    }

    [Theory]
    [InlineData("image/png", 2)]
    [InlineData("image/jpeg", 1)]
    public void Unmatched_returned_images_are_not_replaced_with_local_payload(string mime, byte data)
    {
        var state = new ConversationState();
        state.Enqueue(QueueKind.FollowUp, "same", [Image("local.png", 1)]);
        var returned = Returned(QueueKind.FollowUp, "same", Image("image 1", data, mime));

        state.RestoreQueue([returned], truncated: false);

        var recovered = Assert.Single(state.Queued);
        Assert.Same(returned.Images, recovered.Images);
        Assert.True(recovered.Recovered);
    }

    [Fact]
    public void Image_less_return_matches_image_less_duplicate_without_attaching_delivered_images()
    {
        var state = new ConversationState();
        state.Enqueue(QueueKind.Steer, "same", [Image("delivered.png", 1)]);
        var empty = state.Enqueue(QueueKind.Steer, "same", []);

        state.RestoreQueue([Returned(QueueKind.Steer, "same")], truncated: false);

        var recovered = Assert.Single(state.Queued);
        Assert.Equal(empty.Seq, recovered.Seq);
        Assert.Empty(recovered.Images);
    }

    [Fact]
    public void Image_less_unknown_payload_and_duplicate_text_entries_remain_distinct()
    {
        var state = new ConversationState();
        state.Enqueue(QueueKind.Steer, "same", [Image("delivered.png", 1)]);

        state.RestoreQueue([Returned(QueueKind.Steer, "same"), Returned(QueueKind.Steer, "same")], truncated: false);

        Assert.Equal(2, state.Queued.Count);
        Assert.NotEqual(state.Queued[0].Seq, state.Queued[1].Seq);
        Assert.All(state.Queued, q => { Assert.Empty(q.Images); Assert.True(q.Recovered); });
    }

    [Fact]
    public void Dropped_images_preserve_all_ambiguous_originals_including_image_less_duplicate()
    {
        var state = new ConversationState();
        var first = state.Enqueue(QueueKind.FollowUp, "same", [Image("first.png", 1)]);
        var empty = state.Enqueue(QueueKind.FollowUp, "same", []);
        var second = state.Enqueue(QueueKind.FollowUp, "same", [Image("second.png", 2)]);
        state.Enqueue(QueueKind.Steer, "same", [Image("different-kind.png", 3)]);
        state.Enqueue(QueueKind.FollowUp, "delivered other text", []);

        state.RestoreQueue([Returned(QueueKind.FollowUp, "same")], truncated: false, imagesDropped: true);

        Assert.Equal([first.Seq, empty.Seq, second.Seq], state.Queued.Select(q => q.Seq));
        Assert.Same(first.Images, state.Queued[0].Images);
        Assert.Empty(state.Queued[1].Images);
        Assert.Same(second.Images, state.Queued[2].Images);
        Assert.All(state.Queued, q => Assert.True(q.Recovered));
    }

    [Fact]
    public void Dropped_images_match_complete_entries_before_assigning_stripped_entries()
    {
        var state = new ConversationState();
        var first = state.Enqueue(QueueKind.Steer, "same", [Image("first.png", 1)]);
        var second = state.Enqueue(QueueKind.Steer, "same", [Image("second.png", 2)]);

        state.RestoreQueue([
            Returned(QueueKind.Steer, "same"),
            Returned(QueueKind.Steer, "same", Image("image 1", 1)),
        ], truncated: false, imagesDropped: true);

        Assert.Equal([first.Seq, second.Seq], state.Queued.Select(q => q.Seq));
        Assert.Same(first.Images, state.Queued[0].Images);
        Assert.Same(second.Images, state.Queued[1].Images);
    }

    [Fact]
    public void Dropped_images_keep_unmatched_returned_payload_and_uncertain_originals()
    {
        var state = new ConversationState();
        var local = state.Enqueue(QueueKind.Steer, "same", [Image("local.png", 1)]);
        var returned = Returned(QueueKind.Steer, "same", Image("external.png", 2));

        state.RestoreQueue([returned], truncated: false, imagesDropped: true);

        Assert.Equal(2, state.Queued.Count);
        Assert.Equal(local.Seq, state.Queued[0].Seq);
        Assert.Same(local.Images, state.Queued[0].Images);
        Assert.Same(returned.Images, state.Queued[1].Images);
        Assert.All(state.Queued, q => Assert.True(q.Recovered));
    }

    [Fact]
    public void Truncated_response_retains_omitted_originals_and_does_not_invent_an_image_match()
    {
        var state = new ConversationState();
        var first = state.Enqueue(QueueKind.FollowUp, "same", [Image("first.png", 1)]);
        var omitted = state.Enqueue(QueueKind.Steer, "omitted", [Image("omitted.png", 2)]);

        state.RestoreQueue([Returned(QueueKind.FollowUp, "same")], truncated: true);

        Assert.Equal(3, state.Queued.Count);
        Assert.Equal([first.Seq, omitted.Seq], state.Queued.Take(2).Select(q => q.Seq));
        Assert.Same(first.Images, state.Queued[0].Images);
        Assert.Same(omitted.Images, state.Queued[1].Images);
        Assert.Empty(state.Queued[2].Images);
        Assert.All(state.Queued, q => Assert.True(q.Recovered));
    }

    [Theory]
    [InlineData("queue-recovery", false)]
    [InlineData("queue-recovery-images-dropped", true)]
    [InlineData("queue-recovery-truncated", true)]
    public async Task Stop_propagates_limits_preserves_local_order_and_never_resends_recovery(string scenario, bool keepUncertain)
    {
        var project = TestProcesses.TempDir("queue-recovery");
        await using var session = new SessionController(TestProcesses.FakeFactory(scenario, Path.Combine(project, "sessions")), new LaunchRequest(project));
        await session.StartAsync();
        session.Mutate(s =>
        {
            var saved = s.Enqueue(QueueKind.Steer, "already recovered", [Image("saved.png", 4)]);
            s.RecoverQueued(saved.Seq);
        });
        Assert.True(await session.QueueAsync(QueueKind.FollowUp, "same", [Image("first.png", 1)]));
        Assert.True(await session.QueueAsync(QueueKind.FollowUp, "same", [Image("second.png", 2)]));
        Assert.True(await session.QueueAsync(QueueKind.Steer, "omitted", [Image("third.png", 3)]));
        var before = session.Snapshot().Queued.ToArray();
        // Withdraw outside the controller: the runtime no longer has the first entry, but local state still does.
        var connection = session.Connection;
        Assert.NotNull(connection);
        var withdrawn = await connection.RemoveQueuedMessageAsync("same", steering: false);
        Assert.True(withdrawn.Removed);

        await session.AbortAsync();

        var expected = keepUncertain ? before : [before[0], before[2], before[3]];
        var recovered = session.Snapshot().Queued;
        Assert.Equal(expected.Select(q => q.Seq), recovered.Select(q => q.Seq));
        for (var i = 0; i < expected.Length; i++) Assert.Same(expected[i].Images, recovered[i].Images);
        Assert.All(recovered, q => Assert.True(q.Recovered));
        Assert.Equal(0, (await connection.GetStateAsync()).QueuedMessageCount);
        if (keepUncertain)
            Assert.Contains(session.Snapshot().Items, i => i is NoticeItem { Level: NoticeLevel.Warning });

        await session.PromptAsync("explicit next task");
        var done = await TestProcesses.Eventually(session.Snapshot,
            s => s.Phase == SessionPhase.Ready && s.MessagesCompleted > 0,
            TimeSpan.FromSeconds(20), "the explicit next run to finish without delivering recovered input");
        Assert.Equal(["explicit next task"], done.Items.OfType<UserItem>().Select(u => u.Text));
        Assert.Equal(expected.Select(q => q.Seq), done.Queued.Select(q => q.Seq));
        Assert.All(done.Queued, q => Assert.True(q.Recovered));
        Assert.Equal(0, (await connection.GetStateAsync()).QueuedMessageCount);
    }
}
