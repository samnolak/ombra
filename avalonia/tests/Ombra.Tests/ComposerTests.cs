using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

/// <summary>Steer / follow-up while running, image attachments, the inbound size limit.</summary>
public sealed class ComposerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static async Task<SessionController> StartAsync(string scenario) =>
        await Start(new SessionController(TestProcesses.FakeFactory(scenario, TestProcesses.TempDir("c-sessions")), new LaunchRequest(TestProcesses.TempDir("c-project"))));

    private static async Task<SessionController> Start(SessionController s)
    {
        await s.StartAsync();
        return s;
    }

    [Fact]
    public async Task Follow_up_is_queued_then_joins_the_transcript_when_delivered()
    {
        await using var s = await StartAsync("queue-stream");
        await s.PromptAsync("long task");
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Running && x.Items.OfType<AssistantItem>().Any(), Wait, "running");
        await s.QueueAsync(QueueKind.FollowUp, "then also this", []);
        Assert.Equal("then also this", Assert.Single(s.Snapshot().Queued).Text);
        Assert.DoesNotContain(s.Snapshot().Items, i => i is UserItem { Text: "then also this" });

        var done = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.Queued.Count == 0, Wait, "delivered");
        Assert.Equal(["long task", "then also this"], done.Items.OfType<UserItem>().Select(u => u.Text));
        Assert.Equal("followed: then also this", done.Items.OfType<AssistantItem>().Last().Text);
        // The queued row sits after the answer it followed, not in the middle of it.
        var users = done.Items.Select((it, i) => (it, i)).Where(p => p.it is UserItem).Select(p => p.i).ToList();
        Assert.True(users[1] > done.Items.ToList().FindIndex(i => i is AssistantItem));
    }

    [Fact]
    public async Task Steer_is_queued_and_delivered_too()
    {
        await using var s = await StartAsync("queue-stream");
        await s.PromptAsync("long task");
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Running, Wait, "running");
        await s.QueueAsync(QueueKind.Steer, "change of plan", []);
        Assert.Equal(QueueKind.Steer, Assert.Single(s.Snapshot().Queued).Kind);
        var done = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.Queued.Count == 0, Wait, "delivered");
        Assert.Contains(done.Items, i => i is UserItem { Text: "change of plan", Confirmed: true });
    }

    [Theory]
    [InlineData(QueueKind.FollowUp)]
    [InlineData(QueueKind.Steer)]
    public async Task Input_queued_after_a_local_command_has_settled_starts_its_own_turn(QueueKind kind)
    {
        await using var s = await StartAsync("normal");
        var command = await s.RunSlashCommandAsync("/stats");
        Assert.True(command.Ok);
        Assert.False(command.AgentInvoked);
        Assert.Equal(SessionPhase.Ready, s.Snapshot().Phase);
        Assert.Equal(0, s.Snapshot().RunsEnded);

        // A UI snapshot can still show /context (or another local read) as Running when it sends.
        // Admission must drain this idle queue rather than wait for another prompt to wake the agent.
        foreach (var text in new[] { "first queued task", "second queued task" })
        {
            var runs = s.Snapshot().RunsEnded;
            Assert.True(await s.QueueAsync(kind, text, []));
            var done = await TestProcesses.Eventually(s.Snapshot,
                x => x.Phase == SessionPhase.Ready && x.RunsEnded == runs + 1 && x.Queued.Count == 0,
                Wait, "idle queued input delivered and its turn ended");
            Assert.Contains(done.Items, item => item is UserItem { Confirmed: true } user && user.Text == text);
            Assert.Equal(0, (await s.Connection!.GetStateAsync()).QueuedMessageCount);
        }

        var final = s.Snapshot();
        Assert.Equal(["first queued task", "second queued task"], final.Items.OfType<UserItem>().Select(user => user.Text));
        Assert.Equal(2, final.RunsEnded);
        Assert.Equal(2, final.Items.OfType<AssistantItem>().Count());
    }

    [Fact]
    public async Task Stop_withdraws_runtime_queue_and_preserves_it_for_explicit_recovery()
    {
        await using var s = await StartAsync("slow-stream");
        await s.PromptAsync("long task");
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Running, Wait, "running");
        await s.QueueAsync(QueueKind.FollowUp, "later", []);
        await s.AbortAsync();
        await Task.Delay(1500);
        var snap = s.Snapshot();
        Assert.True(snap.Queued.Count == 1, string.Join("\n", snap.Items.Select(i => i.ToString())) + "\n" + string.Join("\n", snap.DebugTail.TakeLast(15).Select(d => d.Type + " " + d.Summary)));
        Assert.Equal("later", snap.Queued[0].Text);
        Assert.True(snap.Queued[0].Recovered);
        Assert.Equal(0, (await s.Connection!.GetStateAsync()).QueuedMessageCount);
        Assert.DoesNotContain(snap.Items, i => i is UserItem { Text: "later" });
    }

    [Fact]
    public async Task Images_are_sent_as_image_content_and_kept_on_the_row()
    {
        await using var s = await StartAsync("normal");
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        await s.PromptAsync("what is in these", [new ImageAttachment("a.png", "image/png", png), new ImageAttachment("b.jpg", "image/jpeg", [0xFF, 0xD8, 9])]);
        var done = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.MessagesCompleted == 1, Wait, "run end");
        var user = Assert.Single(done.Items.OfType<UserItem>());
        Assert.Equal(["a.png", "b.jpg"], user.Images.Select(i => i.Name));
        Assert.Equal(png, user.Images[0].Data);
        Assert.True(user.Confirmed);

        // History restores the images (omp's stored message carries them, base64).
        var rebuilt = new ConversationState();
        rebuilt.Hydrate((await Ombra.Rpc.OmpCommands.GetMessagesPageAsync(s.Connection!)).Messages);
        var restored = rebuilt.Snapshot().Items.OfType<UserItem>().Single().Images;
        Assert.Equal(["image/png", "image/jpeg"], restored.Select(i => i.MimeType));
        Assert.Equal(png, restored[0].Data);
    }

    [Fact]
    public async Task Attachments_over_omps_line_limit_are_refused_with_a_message()
    {
        await using var s = await StartAsync("normal");
        await s.PromptAsync("too big", [new ImageAttachment("huge.png", "image/png", new byte[900_000])]);
        var snap = s.Snapshot();
        Assert.Equal(SessionPhase.Ready, snap.Phase);
        Assert.Contains(snap.Items, i => i is NoticeItem { Level: NoticeLevel.Error } n && n.Text.StartsWith("Prompt failed", StringComparison.Ordinal));
        await s.PromptAsync("still works");
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.MessagesCompleted == 1, Wait, "next run");
    }
}
