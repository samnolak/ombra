using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views.Session;
using Ombra.ClientCore;
using Ombra.Rpc;
using static Ombra.Tests.SessionAreaTests;

namespace Ombra.Tests;

public sealed class UsageLimitTests
{
    private static OmpSessionState State(string? limit, long readAt = 100) => OmpSessionState.Parse(JsonSerializer.Deserialize<JsonElement>(
        "{\"model\":{\"provider\":\"anthropic\",\"id\":\"claude\"},\"sessionId\":\"chat-a\"" +
        (limit is null ? "" : ",\"usageLimit\":" + limit) + "}")) with { Timings = new RpcTimings { ResponseRead = readAt } };

    [Fact]
    public void Snapshot_preserves_only_reported_stage_reset_and_allowance()
    {
        var conversation = new ConversationState();
        conversation.ApplyHistorySessionMetadata(State("""{"stage":"wrap_up","extraUsage":false,"resetsAtSec":1791415200.5}"""), DateTimeOffset.UtcNow);
        var wrap = Assert.IsType<OmpUsageLimit>(conversation.Snapshot().UsageLimit);
        Assert.Equal("wrap_up", wrap.Stage);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791415200500), wrap.ResetsAt);
        Assert.False(wrap.ExtraUsage);
        Assert.Null(wrap.AllowanceLeftPercent);

        conversation.ApplyUsageLimit(State("""{"stage":"wrap_up","extraUsage":true}"""));
        Assert.True(conversation.Snapshot().UsageLimit!.ExtraUsage);
        Assert.Null(conversation.Snapshot().UsageLimit!.ResetsAt);
        conversation.ApplyUsageLimit(State("""{"stage":"low_priority","resetsAtSec":1791415200,"allowanceLeftPercent":37}"""));
        Assert.Equal(37, conversation.Snapshot().UsageLimit!.AllowanceLeftPercent);
        conversation.ApplyUsageLimit(State("""{"stage":"low_priority","resetsAtSec":1791415200}"""));
        Assert.Null(conversation.Snapshot().UsageLimit!.AllowanceLeftPercent);
        conversation.ApplyUsageLimit(State(null)); // The runtime expires its lane, then omits the field.
        Assert.Null(conversation.Snapshot().UsageLimit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{\"stage\":\"unknown\"}")]
    [InlineData("{\"stage\":\"wrap_up\"}")]
    [InlineData("{\"stage\":\"wrap_up\",\"extraUsage\":\"true\"}")]
    [InlineData("{\"stage\":\"wrap_up\",\"extraUsage\":false,\"resetsAtSec\":\"tomorrow\"}")]
    [InlineData("{\"stage\":\"low_priority\"}")]
    [InlineData("{\"stage\":\"low_priority\",\"resetsAtSec\":1e100}")]
    [InlineData("{\"stage\":\"low_priority\",\"resetsAtSec\":1791415200,\"allowanceLeftPercent\":101}")]
    [InlineData("{\"stage\":\"low_priority\",\"resetsAtSec\":1791415200,\"allowanceLeftPercent\":12.5}")]
    public void Missing_or_malformed_state_clears_previous_account_stage(string? limit)
    {
        var conversation = new ConversationState();
        conversation.ApplyHistorySessionMetadata(State("""{"stage":"wrap_up","extraUsage":false}"""), DateTimeOffset.UtcNow);
        Assert.NotNull(conversation.Snapshot().UsageLimit);
        conversation.ApplyUsageLimit(State(limit));
        Assert.Null(conversation.Snapshot().UsageLimit);
    }

    [Fact]
    public void Model_events_and_session_changes_clear_the_stage_and_reject_older_reads()
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new ConversationState();
        var before = State("""{"stage":"wrap_up","extraUsage":false}""");
        conversation.ApplyHistorySessionMetadata(before, now);
        var changed = JsonSerializer.Deserialize<JsonElement>("""{"type":"model_changed"}""");
        conversation.Apply(new RpcFrame("model_changed", null, changed, 101, 1), now);
        Assert.Null(conversation.Snapshot().UsageLimit);
        conversation.ApplyUsageLimit(before);
        Assert.Null(conversation.Snapshot().UsageLimit);
        conversation.ApplyUsageLimit(before with { Timings = new RpcTimings { ResponseRead = 102 } });
        Assert.NotNull(conversation.Snapshot().UsageLimit);
        conversation.Model = "fake/vision";
        Assert.Null(conversation.Snapshot().UsageLimit);
        conversation.ApplyUsageLimit(before with { Timings = new RpcTimings { ResponseRead = 103 } });
        Assert.Null(conversation.Snapshot().UsageLimit);
        conversation.ApplyHistorySessionMetadata(before with { Timings = new RpcTimings { ResponseRead = 104 } }, now);
        conversation.SessionId = "chat-b";
        Assert.Null(conversation.Snapshot().UsageLimit);
        conversation.ApplyUsageLimit(before with { Timings = new RpcTimings { ResponseRead = 105 } });
        Assert.Null(conversation.Snapshot().UsageLimit);
        conversation.ApplyHistorySessionMetadata(before with { Timings = new RpcTimings { ResponseRead = 106 } }, now);
        conversation.ResetTranscript();
        Assert.Null(conversation.Snapshot().UsageLimit);
    }

    [Fact]
    public void Account_change_rejects_reads_started_before_it_even_when_they_arrive_later()
    {
        var conversation = new ConversationState();
        var old = State("""{"stage":"wrap_up","extraUsage":false}""") with
        {
            Timings = new RpcTimings { Sent = 90, ResponseRead = 100 },
        };
        conversation.ApplyHistorySessionMetadata(old, DateTimeOffset.UtcNow);
        conversation.InvalidateAccountUsage(101);
        Assert.Null(conversation.Snapshot().UsageLimit);
        conversation.ApplyUsageLimit(old with { Timings = new RpcTimings { Sent = 90, ResponseRead = 105 } });
        Assert.Null(conversation.Snapshot().UsageLimit);
        var current = State("""{"stage":"wrap_up","extraUsage":true}""") with
        {
            Timings = new RpcTimings { Sent = 102, ResponseRead = 103 },
        };
        conversation.ApplyUsageLimit(current);
        conversation.ApplyUsageLimit(old with { Timings = new RpcTimings { Sent = 90, ResponseRead = 106 } });
        Assert.True(conversation.Snapshot().UsageLimit!.ExtraUsage);
    }

    [Fact]
    public void Fresh_identity_adoption_requires_confirmation_same_file_and_a_new_request()
    {
        var conversation = new ConversationState();
        var initial = State("""{"stage":"wrap_up","extraUsage":false}""") with { SessionFile = "/chat.jsonl" };
        conversation.ApplyHistorySessionMetadata(initial, DateTimeOffset.UtcNow);
        var rotated = initial with
        {
            SessionId = "fresh-id",
            Timings = new RpcTimings { Sent = 102, ResponseRead = 103 },
        };
        Assert.False(conversation.AcceptStateSession(rotated));
        Assert.False(conversation.NoteFreshProviderSession("Wait for the current response to finish", 101));
        Assert.False(conversation.AcceptStateSession(rotated));
        Assert.True(conversation.NoteFreshProviderSession("Fresh provider session started (1 provider state pruned).", 101));
        Assert.False(conversation.AcceptStateSession(rotated with { SessionFile = "/other.jsonl" }));
        Assert.False(conversation.AcceptStateSession(rotated with { Timings = new RpcTimings { Sent = 99, ResponseRead = 104 } }));
        var epoch = conversation.TranscriptEpoch;
        Assert.True(conversation.AcceptStateSession(rotated));
        Assert.Equal(epoch, conversation.TranscriptEpoch);
        Assert.Equal("fresh-id", conversation.SessionId);
        Assert.False(conversation.AcceptStateSession(initial with { Timings = new RpcTimings { Sent = 105, ResponseRead = 106 } }));
        Assert.True(conversation.NoteFreshProviderSession("Fresh provider session started (1 provider state pruned).", 107));
        conversation.ResetTranscript();
        Assert.False(conversation.AcceptStateSession(initial with { Timings = new RpcTimings { Sent = 108, ResponseRead = 109 } }));
    }

    private static readonly TimeSpan IdentityWait = TimeSpan.FromSeconds(20);

    private static async Task<JsonElement> UsageControl(SessionController session, string json = "{}")
    {
        using var fields = JsonDocument.Parse(json);
        var reply = await session.Connection!.RequestAsync("test_usage_control", writer =>
        {
            foreach (var property in fields.RootElement.EnumerateObject()) property.WriteTo(writer);
        });
        return reply.Data!.Value;
    }

    private static async Task UsageReadsHeld(SessionController session, int count)
    {
        using var timeout = new CancellationTokenSource(IdentityWait);
        while ((await UsageControl(session)).GetProperty("held").GetInt32() < count)
            await Task.Delay(10, timeout.Token);
    }

    private static async Task SignIn(SessionController session)
    {
        var signingIn = session.LoginAsync("fakeauth");
        var prompt = await TestProcesses.Eventually(session.Snapshot, s => s.Dialogs.Count == 1, IdentityWait, "sign-in code");
        await session.AnswerDialogAsync(prompt.Dialogs[0].Id, new DialogAnswer.Value("1234"));
        Assert.True(await signingIn.WaitAsync(IdentityWait));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_provider_identity_keeps_transcript_and_allows_subsequent_refresh_and_wake(bool typed)
    {
        await using var session = new SessionController(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("usage-fresh-sessions")),
            new LaunchRequest(TestProcesses.TempDir("usage-fresh-project")));
        await session.StartAsync();
        await session.PromptAsync("Keep this transcript");
        await TestProcesses.Eventually(session.Snapshot, s => s.Phase == SessionPhase.Ready && s.MessagesCompleted > 0,
            IdentityWait, "completed transcript");
        await SetLimit(session, """{"stage":"wrap_up","extraUsage":false}""");
        var before = await TestProcesses.Eventually(session.Snapshot, s => s.UsageLimit is not null, IdentityWait, "initial usage");
        if (typed) await session.PromptAsync("/fresh");
        else Assert.True((await session.RunSlashCommandAsync("/fresh")).Ok);
        var after = await TestProcesses.Eventually(session.Snapshot, s => s.SessionId != before.SessionId && s.Phase == SessionPhase.Ready,
            IdentityWait, "adopted fresh provider identity");
        Assert.Equal(before.TranscriptEpoch, after.TranscriptEpoch);
        Assert.Equal(before.SessionFile, after.SessionFile);
        Assert.Equal(before.Items.OfType<AssistantItem>().Select(i => i.Key), after.Items.OfType<AssistantItem>().Select(i => i.Key));
        await SetLimit(session, """{"stage":"wrap_up","extraUsage":true}""");
        await TestProcesses.Eventually(session.Snapshot, s => s.UsageLimit?.ExtraUsage == true, IdentityWait, "fresh-session refresh");
        await UsageControl(session, """{"clear":true}""");
        await session.ReconcileAfterResumeAsync();
        Assert.Null(session.Snapshot().UsageLimit);
        // A different id without a confirmed /fresh is still rejected, even on the same file.
        await UsageControl(session, """{"sessionId":"unsolicited","usageLimit":{"stage":"wrap_up","extraUsage":false}}""");
        await session.ReconcileAfterResumeAsync();
        Assert.Equal(after.SessionId, session.Snapshot().SessionId);
        Assert.Null(session.Snapshot().UsageLimit);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Successful_account_change_clears_usage_rejects_old_wake_and_refreshes_same_session(bool logout, bool hasNewUsage)
    {
        await using var session = new SessionController(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("usage-account-sessions")),
            new LaunchRequest(TestProcesses.TempDir("usage-account-project")));
        await session.StartAsync();
        await SignIn(session);
        await SetLimit(session, """{"stage":"wrap_up","extraUsage":false}""");
        var before = await TestProcesses.Eventually(session.Snapshot, s => s.UsageLimit is not null, IdentityWait, "old account usage");
        var replacement = hasNewUsage ? """{"stage":"wrap_up","extraUsage":true}""" : "null";
        await UsageControl(session, $$"""{"holdState":true,"remainingSource":"another stored credential","accountUsage":{{replacement}}}""");
        var wake = session.ReconcileAfterResumeAsync();
        await UsageReadsHeld(session, 1);
        if (logout)
        {
            var account = Assert.Single(await session.GetLogoutAccountsAsync("fakeauth"));
            Assert.Equal("another stored credential", await session.LogoutAsync("fakeauth", account));
        }
        else await SignIn(session);
        Assert.Null(session.Snapshot().UsageLimit);
        await UsageReadsHeld(session, 2); // Account success requests its own authoritative refresh.
        await UsageControl(session, """{"releaseState":true}""");
        await wake.WaitAsync(IdentityWait);
        Assert.Null(session.Snapshot().UsageLimit); // The old read arrived after account success, on the same model/id.
        var readAt = (await UsageControl(session, """{"holdState":false,"releaseState":true}""")).GetProperty("stateCalls").GetInt32();
        await session.ReconcileAfterResumeAsync();
        var after = session.Snapshot();
        Assert.Equal(before.SessionId, after.SessionId);
        Assert.Equal(before.TranscriptEpoch, after.TranscriptEpoch);
        Assert.Equal(before.Model, after.Model);
        if (hasNewUsage) Assert.True(after.UsageLimit!.ExtraUsage);
        else Assert.Null(after.UsageLimit);
        Assert.True((await UsageControl(session)).GetProperty("stateCalls").GetInt32() > readAt);
    }

    private static async Task SetLimit(SessionController session, string? json)
    {
        await session.Connection!.RequestAsync("test_usage_control", w =>
        {
            if (json is null) w.WriteBoolean("clear", true);
            else
            {
                w.WritePropertyName("usageLimit");
                using var document = JsonDocument.Parse(json);
                document.RootElement.WriteTo(w);
            }
        });
        session.RefreshState();
    }

    [AvaloniaFact]
    public async Task Active_stage_badge_and_details_follow_rpc_reset_without_inventing_percentages()
    {
        var e = await OpenAsync(model: "anthropic/claude-sonnet-4-5");
        try
        {
            var vm = e.Vm;
            var reset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
            await SetLimit(vm.Session, $$"""{"stage":"wrap_up","extraUsage":false,"resetsAtSec":{{reset}}}""");
            await Until(() => vm.Usage.HasUsageLimit, "wrap-up stage");
            Assert.False(vm.Usage.HasPercent); // No context ring before the first message, but the stage is still visible.
            var indicator = e.W.GetVisualDescendants().OfType<UsageIndicator>().Single();
            await Settle(100);
            Assert.True(indicator.Button.IsEffectivelyVisible);
            Assert.True(indicator.FindControl<TextBlock>("UsageLimitBadge")!.IsEffectivelyVisible);
            Assert.DoesNotContain("%", vm.Usage.LimitStatus);
            // In the app's language, as the conversation's times: never the system region's date format
            Assert.Contains(Loc.F("Resets {0}", MessageTimes.Short(DateTimeOffset.FromUnixTimeSeconds(reset))), vm.Usage.LimitStatus);
            indicator.Button.Command!.Execute(null);
            await Until(() => !vm.Usage.IsLoading && vm.Usage.HasContext, "context details");
            Assert.True(indicator.Popup.Child!.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Name == "UsageLimitStatus").IsEffectivelyVisible);
            Assert.Empty(indicator.Popup.Child!.GetVisualDescendants().OfType<ProviderUsageGroupView>());

            await SetLimit(vm.Session, """{"stage":"wrap_up","extraUsage":true}""");
            await Until(() => vm.Usage.UsageLimit?.ExtraUsage == true, "paid extra usage indication");
            Assert.Contains(Loc.T("Limit reached · wrap-up, then extra usage"), vm.Usage.LimitStatus);
            Assert.DoesNotContain(Loc.F("Resets {0}", "\0").Split('\0')[0], vm.Usage.LimitStatus);
            await SetLimit(vm.Session, $$"""{"stage":"low_priority","resetsAtSec":{{reset}}}""");
            await Until(() => vm.Usage.UsageLimit?.Stage == "low_priority", "low-priority stage");
            Assert.DoesNotContain("%", vm.Usage.LimitStatus);
            await SetLimit(vm.Session, $$"""{"stage":"low_priority","resetsAtSec":{{reset}},"allowanceLeftPercent":37}""");
            await Until(() => vm.Usage.LimitStatus.Contains("37%"), "reported allowance only");
            await SetLimit(vm.Session, null);
            await Until(() => !vm.Usage.HasUsageLimit, "runtime reset clears stage");
            Assert.Equal("", vm.Usage.LimitStatus);
            Assert.False(indicator.FindControl<TextBlock>("UsageLimitBadge")!.IsVisible);
        }
        finally { await Close(e); }
    }

    [AvaloniaFact]
    public async Task Account_stage_never_leaks_to_another_model_or_chat()
    {
        var e = await OpenAsync(model: "fake/model");
        try
        {
            var vm = e.Vm;
            await Send(e, "Keep this chat");
            await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<AssistantRowViewModel>().Any(), "first reply");
            var first = vm.Session;
            await first.Connection!.RequestAsync("test_usage_control", w =>
            {
                w.WriteStartObject("usageLimitByModel");
                w.WriteStartObject("fake/model");
                w.WriteString("stage", "wrap_up");
                w.WriteBoolean("extraUsage", false);
                w.WriteEndObject();
                w.WriteEndObject();
            });
            first.RefreshState();
            await Until(() => vm.Usage.HasUsageLimit, "first account stage");
            await first.SetModelAsync(new OmpModel("fake", "vision", "Vision", false, true, 131072));
            await Until(() => vm.CurrentModel == "fake/vision" && !vm.Usage.HasUsageLimit, "different model has no stage");
            await first.SetModelAsync(new OmpModel("fake", "model", "Model", true, false, 131072));
            await Until(() => vm.Usage.HasUsageLimit, "original account stage");
            await vm.NewChatCommand.ExecuteAsync(null);
            await Until(() => !ReferenceEquals(vm.Session, first) && vm.Phase == SessionPhase.Ready, "second chat");
            Assert.False(vm.Usage.HasUsageLimit);
            Assert.Equal("", vm.Usage.LimitStatus);
            Assert.NotNull(first.Snapshot().UsageLimit);
            await Until(() => vm.Sessions.Any(s => s.Model.Path == first.SessionFile), "first chat listed");
            await vm.OpenSessionCommand.ExecuteAsync(vm.Sessions.Single(s => s.Model.Path == first.SessionFile));
            await Until(() => ReferenceEquals(vm.Session, first) && vm.Usage.HasUsageLimit, "restored first chat stage");
        }
        finally { await Close(e); }
    }
}
