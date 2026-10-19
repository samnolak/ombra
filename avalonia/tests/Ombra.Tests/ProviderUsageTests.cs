using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views.Session;
using Ombra.App.Views.Settings;
using Ombra.ClientCore;
using static Ombra.Tests.SessionAreaTests;

namespace Ombra.Tests;

/// <summary>
/// The session model's plan limits in the context ring's popover and every provider's in Settings → Model providers,
/// both from <c>omp usage --json</c> read only while on screen; saved resets; the active account's exceptional stage.
/// </summary>
public sealed class ProviderUsageTests
{
    /// <summary>omp 18.8.0's <c>omp usage --json --redact</c> for a ChatGPT Plus and a Claude account (trimmed).</summary>
    public const string RealUsageJson = """
        {"generatedAt":1791399588556,"reports":[
        {"provider":"openai-codex","fetchedAt":1791399582623,"limits":[
          {"id":"openai-codex:primary","label":"5 hours","scope":{"provider":"openai-codex","windowId":"5h","shared":true},"window":{"id":"5h","label":"5 hours","durationMs":18000000,"resetsAt":1791417583000},"amount":{"used":0,"limit":100,"remaining":100,"usedFraction":0,"remainingFraction":1,"unit":"percent"},"status":"ok"},
          {"id":"openai-codex:secondary","label":"7 days","scope":{"provider":"openai-codex","windowId":"7d","shared":true},"window":{"id":"7d","label":"7 days","durationMs":604800000,"resetsAt":1791978687000},"amount":{"used":16,"limit":100,"remaining":84,"usedFraction":0.16,"remainingFraction":0.84,"unit":"percent"},"status":"ok"}],
         "resetCredits":{"availableCount":2},"metadata":{"planType":"plus","allowed":true,"email":"sc*","accountId":"7d*"}},
        {"provider":"anthropic","fetchedAt":1791399583137,"limits":[
          {"id":"anthropic:5h","label":"Claude 5 Hour","scope":{"provider":"anthropic","windowId":"5h","shared":true},"window":{"id":"5h","label":"5 Hour","durationMs":18000000,"resetsAt":1791415200051},"amount":{"used":6,"limit":100,"remaining":94,"usedFraction":0.06,"remainingFraction":0.94,"unit":"percent"},"status":"ok"},
          {"id":"anthropic:7d:fable","label":"Claude 7 Day (Fable)","scope":{"provider":"anthropic","windowId":"7d","tier":"fable"},"window":{"id":"7d","label":"7 Day","durationMs":604800000,"resetsAt":1791597600000},"amount":{"used":0,"limit":100,"remaining":100,"usedFraction":0,"remainingFraction":1,"unit":"percent"},"status":"ok"}],
         "metadata":{"email":"ig*","accountId":"30*"}}],
        "accountsWithoutUsage":[],"disabledCredentials":[]}
        """;

    private const string Claude = "anthropic/claude-sonnet-4-5";

    [Fact]
    public void Provider_usage_is_read_from_omps_usage_json()
    {
        var reports = ProviderUsage.Parse(RealUsageJson)!;
        Assert.Equal(["openai-codex", "anthropic"], reports.Select(r => r.Provider));
        var codex = reports[0];
        Assert.Equal(("sc*", "plus"), (codex.Account, codex.Plan));
        Assert.Equal(["5 hours", "7 days"], codex.Limits.Select(l => l.Label));
        Assert.Equal(0.16, codex.Limits[1].UsedFraction);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791978687000), codex.Limits[1].ResetsAt);
        Assert.Equal(Loc.F("{0:0}% used", 16.0), ProviderUsageViewModel.Amount(codex.Limits[1]));
        Assert.Equal(["Claude 5 Hour", "Claude 7 Day (Fable)"], reports[1].Limits.Select(l => l.Label));
        Assert.Null(ProviderUsage.Parse("No credentials found. Run `omp` and use /login to add accounts."));
        Assert.Empty(ProviderUsage.Parse("""{"generatedAt":1,"reports":[],"accountsWithoutUsage":[]}""")!);

        // Amounts without a fraction (API spend, request quotas), and the countdown, in the app's language and culture
        var spend = new ProviderUsageLimit("x", "Monthly spend", null, 12.4, null, "usd", null, "resets", "ok");
        Assert.Equal(Loc.F("{0} used", 12.4.ToString("$#,0.00", System.Globalization.CultureInfo.CurrentCulture)), ProviderUsageViewModel.Amount(spend));
        Assert.Equal(Loc.F("{0} of {1}", 1200.0.ToString("#,0.##", System.Globalization.CultureInfo.CurrentCulture), 5000.0.ToString("#,0.##", System.Globalization.CultureInfo.CurrentCulture) + " requests"),
            ProviderUsageViewModel.Amount(spend with { Used = 1200, Limit = 5000, Unit = "requests" }));
        Assert.Equal("", ProviderUsageViewModel.Amount(spend with { Used = null }));
        var now = DateTimeOffset.UnixEpoch;
        Assert.Equal("resets in 2h 10m", ProviderUsage.ResetsIn(now.AddMinutes(130), now));
        Assert.Equal("resets in 3d 4h", ProviderUsage.ResetsIn(now.AddHours(76), now));
        Assert.Equal("resets in 45m", ProviderUsage.ResetsIn(now.AddMinutes(45), now));
        Assert.Equal("regen soon", ProviderUsage.ResetsIn(now.AddSeconds(20), now, "regen"));
        Assert.Equal(Loc.F("resets in {0}", Loc.F("{0}h {1}m", 2, 10)), ProviderUsageViewModel.ResetsIn(now.AddMinutes(130), now, "resets"));
        Assert.Equal(Loc.F("resets in {0}", Loc.F("{0}d", 3) + " " + Loc.F("{0}h", 4)), ProviderUsageViewModel.ResetsIn(now.AddHours(76), now, "resets"));
        Assert.Equal(Loc.T("resets soon"), ProviderUsageViewModel.ResetsIn(now.AddSeconds(20), now, "resets"));
        Assert.Equal("regen soon", ProviderUsageViewModel.ResetsIn(now.AddSeconds(20), now, "regen")); // omp's own word

        // Meter names in sentence case, by window
        Assert.Equal("5-hour limit", ProviderUsage.DisplayLabel(reports[1].Limits[0], "Claude"));
        Assert.Equal(Loc.T("Weekly limit") + " (Fable)", ProviderUsage.DisplayLabel(reports[1].Limits[1], "Claude", Loc.T));
        Assert.Equal("Weekly limit (Fable)", ProviderUsage.DisplayLabel(reports[1].Limits[1], "Claude"));
        Assert.Equal("Weekly limit", ProviderUsage.DisplayLabel(codex.Limits[1], "ChatGPT"));
        Assert.Equal("Monthly spend", ProviderUsage.DisplayLabel(spend, "Kimi"));
        Assert.Equal("Premium requests", ProviderUsage.DisplayLabel(spend with { Label = "Copilot premium requests" }, "Copilot"));
    }

    [Fact]
    public void Saved_resets_are_read_as_omp_summarizes_them_and_matched_to_omps_account_list()
    {
        // Count only (omp 18.8.0's real ChatGPT report): every saved reset usable, nothing more said
        var reports = ProviderUsage.Parse(RealUsageJson)!;
        Assert.Equal(new ProviderUsageResets(2, 2, null, null), reports[0].Resets);
        Assert.Null(reports[1].Resets);

        static ProviderUsageResets? Read(string resetCredits) => ProviderUsage.Parse(
            $$"""{"reports":[{"provider":"anthropic","limits":[{"label":"Claude 5 Hour"}],"resetCredits":{{resetCredits}}}]}""")![0].Resets;
        var soon = DateTimeOffset.UtcNow.AddDays(2);
        var later = DateTimeOffset.UtcNow.AddDays(9);
        // The soonest expiry among the ones still to spend (a redeemed one does not count); the picked credit's block
        var blocked = Read($$"""
            {"availableCount":3,"redeemableCount":0,"nextCreditId":"b","credits":[
              {"id":"a","status":"redeemed","expiresAt":"{{DateTimeOffset.UtcNow.AddHours(1):O}}"},
              {"id":"b","remainingCount":1,"blocking":["anthropic:5h","anthropic:7d:opus"],"expiresAt":"{{later:O}}"},
              {"id":"c","remainingCount":1,"expiresAt":"{{soon:O}}"}]}
            """)!;
        Assert.Equal((3, 0, "blocked by Claude 5h, Claude Opus weekly"), (blocked.Saved, blocked.Usable, blocked.Unavailable));
        Assert.Equal(soon, blocked.SoonestExpiry!.Value, TimeSpan.FromSeconds(1));
        // omp's own reason wins; a cooldown next; none usable without a reason says so
        var omps = Read("""{"availableCount":1,"redeemableCount":0,"reason":"not eligible for this plan","cooldownUntil":"x"}""")!;
        Assert.Equal("not eligible for this plan", omps.Unavailable);
        var cooldown = Read("""{"availableCount":1,"redeemableCount":0,"cooldownUntil":"2026-10-16"}""")!;
        Assert.Equal("cooldown until 10/16/2026", cooldown.Unavailable);
        var none = Read("""{"availableCount":1,"redeemableCount":0}""")!;
        Assert.Equal("not usable right now", none.Unavailable);
        // Shown in the app's language and date format; omp's own reason as omp said it
        var now = DateTimeOffset.UtcNow;
        string Detail(string reason) => Loc.F("{0} usable now", 0) + " · " + Loc.F("unavailable: {0}", reason);
        Assert.Equal(Detail("not eligible for this plan"), ProviderUsageGroupViewModel.ResetsDetail(omps, now));
        Assert.Equal(Detail(Loc.F("cooldown until {0:d}", new DateTime(2026, 10, 16))), ProviderUsageGroupViewModel.ResetsDetail(cooldown, now));
        Assert.Equal(Detail(Loc.T("not usable right now")), ProviderUsageGroupViewModel.ResetsDetail(none, now));
        Assert.Equal(Detail(Loc.F("blocked by {0}", "Claude 5h, Claude Opus weekly")), ProviderUsageGroupViewModel.ResetsDetail(blocked with { SoonestExpiry = null }, now));

        // /usage reset's list (omp 18.8.0): the account a report is about, by provider and e-mail (organization after it)
        var listed = ProviderUsage.ParseSavedResetAccounts("""
            Saved rate-limit resets:
            - dev@example.com · Example [Claude · anthropic/2]: 2 saved, 1 usable now, expires 2026-10-18T09:00:00.000Z (active)
            - other@example.com [Claude · anthropic/5]: 1 saved, 0 usable now (cooldown until 2026-10-16)
            - dev@example.com [Codex · openai-codex/3]: unavailable (network error)

            Spend one with `/usage reset <provider>/<credential id>` or `/usage reset <provider>/active`.
            """);
        Assert.Equal([new SavedResetAccount("anthropic", "2", "dev@example.com · Example", true), new SavedResetAccount("anthropic", "5", "other@example.com", false),
            new SavedResetAccount("openai-codex", "3", "dev@example.com", false)], listed);
        var claude = new ProviderUsageReport("anthropic", "dev@example.com", null, null, []);
        Assert.Equal("/usage reset anthropic/2", ProviderUsage.SpendResetCommand(ProviderUsage.ResetAccountFor(claude, listed)!));
        Assert.Equal("3", ProviderUsage.ResetAccountFor(claude with { Provider = "openai-codex", Account = null }, listed)!.CredentialId); // its provider's only one
        Assert.Null(ProviderUsage.ResetAccountFor(claude with { Account = null }, listed)); // two Claude accounts: never a guess
        Assert.Null(ProviderUsage.ResetAccountFor(claude with { Account = "dev@example" }, listed)); // not a prefix of another e-mail
    }

    [AvaloniaFact]
    public async Task Failed_usage_refresh_keeps_the_dated_report_until_a_successful_empty_report()
    {
        var e = await OpenAsync(model: Claude);
        try
        {
            await e.Vm.OpenSettingsAtAsync("providers");
            var usage = e.Vm.ProviderUsage;
            await Until(() => usage.HasGroups && !usage.IsLoading, "initial usage");
            var reports = usage.Groups.Select(g => g.Report).ToArray();
            var note = usage.Note;
            Assert.NotEmpty(note);
            var cli = Path.Combine(Path.GetDirectoryName(e.Cli)!, "cli.json");
            await File.WriteAllTextAsync(cli, System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, object> { ["usage --json"] = new { stdout = "invalid report" } }));
            await usage.RefreshCommand.ExecuteAsync(null);
            Assert.True(usage.RefreshFailed);
            Assert.Equal(reports, usage.Groups.Select(g => g.Report));
            Assert.Equal(note, usage.Note);
            await File.WriteAllTextAsync(cli, System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, object> { ["usage --json"] = new { stdout = """{"reports":[]}""" } }));
            await usage.RefreshCommand.ExecuteAsync(null);
            Assert.False(usage.RefreshFailed);
            Assert.False(usage.HasGroups);
            Assert.Empty(usage.Note);
        }
        finally { await Close(e); }
    }

    [AvaloniaFact]
    public void Usage_advice_starts_at_eighty_percent_and_includes_exhausted_limits()
    {
        var meter = new UsageMeterViewModel("Weekly", 0.79, "", "resets soon", false);
        Assert.False(meter.ShowAdvice);
        Assert.True((meter with { Fraction = 0.8 }).ShowAdvice);
        Assert.True((meter with { Fraction = null, Exhausted = true }).ShowAdvice);
        Assert.Equal(Loc.T("Near the plan limit. Wait for the reset or choose another provider."), meter.Advice);
    }

    [AvaloniaFact]
    public async Task A_saved_reset_shows_with_the_limits_and_is_spent_only_after_asking()
    {
        const string applied = "Reset applied for dev@example.com · Example (Claude) — Claude's 5h session limit has been refreshed; weekly limits are unchanged.";
        var e = await OpenAsync(model: Claude, extraCommands: _ => new()
        {
            ["/usage reset"] = "Saved rate-limit resets:\n- dev@example.com · Example [Claude · anthropic/2]: 2 saved, 1 usable now (active)\n\nSpend one with `/usage reset <provider>/<credential id>`.",
            ["/usage reset anthropic/2"] = applied,
        });
        var vm = e.Vm;
        await vm.OpenSettingsAtAsync("providers");
        await Until(() => vm.ProviderUsage.HasGroups && !vm.ProviderUsage.IsLoading, "plan usage in settings");
        var claude = vm.ProviderUsage.Groups.Single(g => g.Provider == "anthropic");
        Assert.True(claude.HasSavedResets);
        Assert.Equal(Loc.Plural(2, "{0} saved reset", "{0} saved resets"), claude.SavedResetsText);
        Assert.Equal(Loc.F("{0} usable now", 1) + " · " + Loc.F("the soonest expires in {0}", Loc.F("{0}d", 3)), claude.SavedResetsDetail);
        Assert.True(claude.CanSpendReset);
        await Settle(300);
        var view = e.W.GetVisualDescendants().OfType<ProviderUsageGroupView>().Single(v => ReferenceEquals(v.DataContext, claude));
        Assert.True(view.FindControl<Button>("UseSavedResetButton")!.IsEffectivelyVisible);

        // Asked in place first: nothing is spent until the user confirms
        claude.AskSpendResetCommand.Execute(null);
        await Settle(100);
        Assert.True(view.FindControl<StackPanel>("SpendResetConfirm")!.IsEffectivelyVisible);
        Assert.DoesNotContain(Sent(e), l => l.StartsWith("/usage", StringComparison.Ordinal));
        claude.CancelSpendResetCommand.Execute(null);
        Assert.False(claude.IsConfirmingSpend);

        var reads = UsageReads(e);
        var runs = vm.Session.Snapshot().RunsEnded;
        claude.AskSpendResetCommand.Execute(null);
        await claude.SpendResetCommand.ExecuteAsync(null);
        Assert.Equal(["/usage reset", "/usage reset anthropic/2"], Sent(e).Where(l => l.StartsWith("/usage", StringComparison.Ordinal)));
        Assert.Equal(reads + 1, UsageReads(e)); // the limits are read again
        Assert.Equal(runs, vm.Session.Snapshot().RunsEnded); // listing and spending resets are local commands
        // omp's answer stays under the group made from the new read
        var after = vm.ProviderUsage.Groups.Single(g => g.Provider == "anthropic");
        Assert.Equal((applied, false), (after.SpendMessage, after.SpendFailed));
        await Close(e);
    }

    [AvaloniaFact]
    public async Task A_saved_reset_completed_away_from_settings_invalidates_the_cached_report()
    {
        var e = await OpenAsync(model: Claude, extraCommands: _ => new()
        {
            ["/usage reset"] = "Saved rate-limit resets:\n- dev@example.com [Claude · anthropic/2]: 2 saved, 1 usable now (active)",
            ["/usage reset anthropic/2"] = "Reset applied for dev@example.com (Claude) — Claude's 5h session limit has been refreshed; weekly limits are unchanged.",
        });
        try
        {
            var vm = e.Vm;
            await vm.OpenSettingsAtAsync("providers");
            await Until(() => vm.ProviderUsage.HasGroups && !vm.ProviderUsage.IsLoading, "initial reports");
            var group = vm.ProviderUsage.Groups.Single(g => g.Provider == "anthropic");
            var reads = UsageReads(e);
            group.AskSpendResetCommand.Execute(null);
            var spending = group.SpendResetCommand.ExecuteAsync(null);
            await vm.OpenSettingsAtAsync("general");
            await spending;
            Assert.False(group.SpendFailed);
            Assert.Equal(reads, UsageReads(e)); // mutation must not start a hidden report process
            var report = RealUsageJson.Replace("\"ig*\"", "\"dev@example.com\"", StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(e.Cli)!, "cli.json"),
                System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["usage --json"] = new { stdout = report } }));
            await vm.OpenSettingsAtAsync("providers");
            await Until(() => UsageReads(e) == reads + 1 && !vm.ProviderUsage.IsLoading, "post-spend report");
            var refreshed = Assert.Single(vm.ProviderUsage.Groups, g => g.Provider == "anthropic");
            Assert.False(refreshed.CanSpendReset);
            Assert.Equal(6, refreshed.Meters[0].Value);
        }
        finally { await Close(e); }
    }

    [AvaloniaFact]
    public async Task A_saved_reset_of_an_account_omp_does_not_list_is_never_spent()
    {
        var e = await OpenAsync(model: Claude, extraCommands: _ => new()
        {
            ["/usage reset"] = "Saved rate-limit resets:\n- someone@else.com [Claude · anthropic/7]: 1 saved, 1 usable now",
        });
        var vm = e.Vm;
        await vm.OpenSettingsAtAsync("providers");
        await Until(() => vm.ProviderUsage.HasGroups && !vm.ProviderUsage.IsLoading, "plan usage in settings");
        var claude = vm.ProviderUsage.Groups.Single(g => g.Provider == "anthropic");
        Assert.False(vm.ProviderUsage.Groups.Single(g => g.Provider == "openai-codex").HasSavedResets);
        await claude.SpendResetCommand.ExecuteAsync(null);
        Assert.Equal(["/usage reset"], Sent(e).Where(l => l.StartsWith("/usage", StringComparison.Ordinal)));
        var after = vm.ProviderUsage.Groups.Single(g => g.Provider == "anthropic");
        Assert.True(after.SpendFailed);
        Assert.Equal(Loc.T("omp did not list this account among the ones with saved resets."), after.SpendMessage);
        await Close(e);
    }

    private static int UsageReads(Env e) => File.Exists(e.Cli) ? File.ReadAllLines(e.Cli).Count(l => l == "usage --json") : 0;

    [AvaloniaFact]
    public async Task The_models_plan_usage_shows_in_the_ring_popover_and_every_provider_in_settings()
    {
        // get_state carries no usageLimit outside wrap-up/low priority (the owner's real engine, 18.8.0): the popover's
        // 5-hour and weekly meters must come from omp's usage report, never wait for that stage.
        var e = await OpenAsync(model: Claude);
        var vm = e.Vm;
        await Send(e, "Look at the parser"); // a new chat has no ring: it shows with the conversation
        await Until(() => vm.Usage.HasPercent && vm.Phase == SessionPhase.Ready && vm.Rows.OfType<AssistantRowViewModel>().Any(), "context ring");
        Assert.Equal(0, UsageReads(e)); // nothing read before it is looked at
        Assert.False(vm.Usage.HasUsageLimit);
        var runs = vm.Session.Snapshot().RunsEnded;

        vm.Usage.IsOpen = true;
        await Until(() => !vm.Usage.IsLoading && vm.Usage.HasContext && vm.Usage.HasTotals && vm.Usage.HasLimits, "plan usage in the popover");
        var claude = Assert.Single(vm.Usage.Limits); // only the session model's provider
        Assert.Equal(("anthropic", "Claude", "dev@example.com"), (claude.Provider, claude.Name, claude.Account));
        Assert.Equal([Loc.T("5-hour limit"), Loc.T("Weekly limit")], claude.Meters.Select(m => m.Label));
        Assert.Equal((Loc.F("{0:0}% used", 42.0), 42.0), (claude.Meters[0].Amount, claude.Meters[0].Value));
        Assert.True(claude.Meters[1].IsHigh);
        Assert.False(claude.Meters[0].IsHigh);
        Assert.False(vm.Usage.ShowsStageCaption);
        var ring = e.W.GetVisualDescendants().OfType<UsageIndicator>().Single();
        await Settle(300);
        var meters = ring.Popup.Child!.GetVisualDescendants().OfType<ProviderUsageGroupView>().Single();
        Assert.True(meters.IsEffectivelyVisible);
        Assert.Equal(1, UsageReads(e));
        Assert.Equal(runs, vm.Session.Snapshot().RunsEnded); // /context is a read, not another agent turn

        // Opened again at once: omp is not asked again (no polling)
        vm.Usage.IsOpen = false;
        vm.Usage.IsOpen = true;
        await Until(() => !vm.Usage.IsLoading && vm.Usage.HasLimits, "reopened");
        Assert.Equal(1, UsageReads(e));
        Assert.Equal(runs, vm.Session.Snapshot().RunsEnded);

        // A run ended while it is open: the limits are read again, the session's tallies grow
        await Send(e, "Explain the parser change");
        await Until(() => UsageReads(e) == 2 && !vm.Usage.IsLoading && vm.Usage.HasLimits, "read again after the run");
        Assert.Equal(runs + 1, vm.Session.Snapshot().RunsEnded);
        await Until(() => vm.Usage.Totals.Any(t => t.Label == Loc.T("Cost")), "the session's cost");
        Assert.DoesNotContain(Sent(e), l => l.StartsWith("/usage", StringComparison.Ordinal)); // never a command in the conversation

        // Closed: a run ending reads nothing until it is looked at again
        vm.Usage.IsOpen = false;
        await Send(e, "And the tests");
        await Until(() => vm.Session.Snapshot().RunsEnded == runs + 2 && vm.Phase == SessionPhase.Ready, "run while closed");
        await Settle(200);
        Assert.Equal(2, UsageReads(e));

        // Settings → Model providers: every provider that reports usage
        await vm.OpenSettingsAtAsync("providers");
        await Until(() => vm.ProviderUsage.Groups.Count == 2 && !vm.ProviderUsage.IsLoading, "plan usage in settings");
        Assert.Equal(["Claude", "ChatGPT"], vm.ProviderUsage.Groups.Select(g => g.Name));
        Assert.Equal(3, UsageReads(e)); // stale since the last run
        var inSettings = vm.ProviderUsage.Groups.Single(g => g.Provider == "anthropic");
        Assert.Equal(("Claude", "dev@example.com"), (inSettings.Name, inSettings.Account));
        Assert.Equal([Loc.T("5-hour limit"), Loc.T("Weekly limit")], inSettings.Meters.Select(m => m.Label));
        Assert.Equal((Loc.F("{0:0}% used", 42.0), 42.0), (inSettings.Meters[0].Amount, inSettings.Meters[0].Value));
        Assert.True(inSettings.Meters[1].IsHigh);
        Assert.False(inSettings.Meters[0].IsHigh);
        Assert.Equal("dev@example.com · " + Loc.F("{0} plan", "Plus"), vm.ProviderUsage.Groups[1].Account);
        Assert.Equal([Loc.T("5-hour limit"), Loc.T("Weekly limit")], vm.ProviderUsage.Groups[1].Meters.Select(m => m.Label));
        await Settle(200);
        var card = e.W.GetVisualDescendants().OfType<ProviderUsageSettings>().Single().FindControl<Border>("ProviderUsageCard")!;
        Assert.True(card.IsEffectivelyVisible);
        Assert.Equal(2, card.GetVisualDescendants().OfType<ProviderUsageGroupView>().Count());

        // Refresh by hand asks omp at once
        var reads = UsageReads(e);
        await vm.ProviderUsage.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(reads + 1, UsageReads(e));
        await Close(e);
    }

    [AvaloniaFact]
    public async Task Providers_without_a_usage_source_show_no_meters()
    {
        // The session's provider reports nothing and no exceptional stage: no plan section, no badge, no made-up numbers
        var e = await OpenAsync();
        var vm = e.Vm;
        await Send(e, "Look at the parser"); // a new chat has no ring: it shows with the conversation
        await Until(() => vm.Usage.HasPercent && vm.Phase == SessionPhase.Ready && vm.Rows.OfType<AssistantRowViewModel>().Any(), "context ring");
        vm.Usage.IsOpen = true;
        await Until(() => !vm.Usage.IsLoading && vm.Usage.HasContext && UsageReads(e) == 1 && !vm.ProviderUsage.IsLoading, "popover loaded");
        Assert.False(vm.Usage.HasLimits);
        Assert.Empty(vm.Usage.Limits);
        Assert.False(vm.Usage.HasUsageLimit);
        Assert.Equal("", vm.Usage.LimitStatus);
        await Close(e);

        // omp has no usage to report (no credentials): Settings shows no plan usage card
        var none = await OpenAsync(cli: new() { ["usage --json"] = new { stderr = "No credentials found.", exit = 1 } }, model: Claude);
        await none.Vm.OpenSettingsAtAsync("providers");
        await Until(() => UsageReads(none) == 1 && !none.Vm.ProviderUsage.IsLoading, "usage read");
        Assert.False(none.Vm.ProviderUsage.HasGroups);
        await Settle(100);
        var card = none.W.GetVisualDescendants().OfType<ProviderUsageSettings>().Single().FindControl<Border>("ProviderUsageCard")!;
        Assert.False(card.IsEffectivelyVisible);
        await Close(none);
    }

    /// <summary>Pictures of the popover and the settings card, light and dark (OMBRA_REVIEW_DIR/usage).</summary>
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Usage_screens(string theme)
    {
        if (Environment.GetEnvironmentVariable("OMBRA_REVIEW_DIR") is not { } dir) return;
        Directory.CreateDirectory(Path.Combine(dir, "usage"));
        MainViewModel.ApplyTheme(theme);
        try
        {
            var e = await OpenAsync(model: Claude);
            var vm = e.Vm;
            await Send(e, "Explain the parser change");
            await Until(() => vm.Phase == SessionPhase.Ready && vm.Usage.HasPercent, "reply");
            vm.Usage.IsOpen = true;
            await Until(() => !vm.Usage.IsLoading && vm.Usage.HasContext && vm.Usage.HasTotals && vm.Usage.HasLimits, "popover");
            await Settle(400);
            Save(e.W.CaptureRenderedFrame(), dir, $"{theme}-usage-window");
            vm.Usage.IsOpen = false;
            await vm.OpenSettingsAtAsync("providers");
            await Until(() => vm.ProviderUsage.HasGroups && !vm.ProviderUsage.IsLoading, "settings");
            await Settle(400);
            e.W.GetVisualDescendants().OfType<ProviderUsageSettings>().Single().BringIntoView();
            await Settle(200);
            Save(e.W.CaptureRenderedFrame(), dir, $"{theme}-usage-settings");
            await Close(e);
        }
        finally
        {
            MainViewModel.ApplyTheme("light");
        }
    }

    private static void Save(WriteableBitmap? frame, string dir, string name)
    {
        if (frame is null) return;
        using (frame)
        using (var f = File.Create(Path.Combine(dir, "usage", name + ".png")))
            frame.Save(f, new PngBitmapEncoderOptions());
    }
}
