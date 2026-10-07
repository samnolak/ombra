using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using OmpGui.App.ViewModels;
using OmpGui.App.Views.Session;
using OmpGui.App.Views.Settings;
using OmpGui.ClientCore;
using static OmpGui.Tests.SessionAreaTests;

namespace OmpGui.Tests;

/// <summary>
/// Plan usage of the signed-in providers (<c>omp usage --json</c>, a side process) in the context ring's popover and
/// in Settings → Model providers, and this session's tokens and cost (<c>get_session_stats</c>).
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
        Assert.Equal("16% used", ProviderUsage.Amount(codex.Limits[1]));
        Assert.Equal(["Claude 5 Hour", "Claude 7 Day (Fable)"], reports[1].Limits.Select(l => l.Label));
        Assert.Null(ProviderUsage.Parse("No credentials found. Run `omp` and use /login to add accounts."));
        Assert.Empty(ProviderUsage.Parse("""{"generatedAt":1,"reports":[],"accountsWithoutUsage":[]}""")!);

        // Amounts without a fraction (API spend, request quotas), and the countdown
        var spend = new ProviderUsageLimit("x", "Monthly spend", null, 12.4, null, "usd", null, "resets", "ok");
        Assert.Equal("$12.40 used", ProviderUsage.Amount(spend));
        Assert.Equal("1,200 of 5,000 requests", ProviderUsage.Amount(spend with { Used = 1200, Limit = 5000, Unit = "requests" }));
        Assert.Equal("", ProviderUsage.Amount(spend with { Used = null }));
        var now = DateTimeOffset.UnixEpoch;
        Assert.Equal("resets in 2h 10m", ProviderUsage.ResetsIn(now.AddMinutes(130), now));
        Assert.Equal("resets in 3d 4h", ProviderUsage.ResetsIn(now.AddHours(76), now));
        Assert.Equal("resets in 45m", ProviderUsage.ResetsIn(now.AddMinutes(45), now));
        Assert.Equal("regen soon", ProviderUsage.ResetsIn(now.AddSeconds(20), now, "regen"));

        // Meter names in sentence case, by window
        Assert.Equal("5-hour limit", ProviderUsage.DisplayLabel(reports[1].Limits[0], "Claude"));
        Assert.Equal("Weekly limit (Fable)", ProviderUsage.DisplayLabel(reports[1].Limits[1], "Claude"));
        Assert.Equal("Weekly limit", ProviderUsage.DisplayLabel(codex.Limits[1], "ChatGPT"));
        Assert.Equal("Monthly spend", ProviderUsage.DisplayLabel(spend, "Kimi"));
        Assert.Equal("Premium requests", ProviderUsage.DisplayLabel(spend with { Label = "Copilot premium requests" }, "Copilot"));
    }

    private static int UsageReads(Env e) => File.Exists(e.Cli) ? File.ReadAllLines(e.Cli).Count(l => l == "usage --json") : 0;

    [AvaloniaFact]
    public async Task The_models_plan_usage_shows_in_the_ring_popover_and_every_provider_in_settings()
    {
        var e = await OpenAsync(model: Claude);
        var vm = e.Vm;
        await Until(() => vm.Usage.HasPercent, "context ring");
        Assert.Equal(0, UsageReads(e)); // nothing read before it is looked at

        vm.Usage.IsOpen = true;
        await Until(() => !vm.Usage.IsLoading && vm.Usage.HasLimits, "plan usage in the popover");
        var claude = Assert.Single(vm.Usage.Limits); // only the session model's provider
        Assert.Equal(("anthropic", "Claude", "dev@example.com"), (claude.Provider, claude.Name, claude.Account));
        Assert.Equal(["5-hour limit", "Weekly limit"], claude.Meters.Select(m => m.Label));
        Assert.Equal(("42% used", 42.0), (claude.Meters[0].Amount, claude.Meters[0].Value));
        Assert.Matches(@"^resets in 2h (9|10)m$", claude.Meters[0].Resets);
        Assert.Matches(@"^resets in 3d (3|4)h$", claude.Meters[1].Resets);
        Assert.True(claude.Meters[1].IsHigh);
        Assert.False(claude.Meters[0].IsHigh);
        var ring = e.W.GetVisualDescendants().OfType<UsageIndicator>().Single();
        await Settle(300);
        var meters = ring.Popup.Child!.GetVisualDescendants().OfType<ProviderUsageGroupView>().Single();
        Assert.True(meters.IsEffectivelyVisible);
        Assert.Equal(1, UsageReads(e));

        // Opened again at once: omp is not asked again (no polling)
        vm.Usage.IsOpen = false;
        vm.Usage.IsOpen = true;
        await Until(() => !vm.Usage.IsLoading, "reopened");
        Assert.Equal(1, UsageReads(e));

        // A run ended while it is open: the limits are read again, the session's tallies grow
        await Send(e, "Explain the parser change");
        await Until(() => UsageReads(e) == 2 && !vm.Usage.IsLoading, "read again after the run");
        await Until(() => vm.Usage.Totals.Any(t => t.Label == "Cost"), "the session's cost");
        Assert.DoesNotContain(Sent(e), l => l.StartsWith("/usage", StringComparison.Ordinal)); // never a command in the conversation

        // Settings → Model providers: every provider that reports usage
        vm.Usage.IsOpen = false;
        await vm.OpenSettingsAtAsync("providers");
        await Until(() => vm.ProviderUsage.Groups.Count == 2 && !vm.ProviderUsage.IsLoading, "plan usage in settings");
        Assert.Equal(["Claude", "ChatGPT"], vm.ProviderUsage.Groups.Select(g => g.Name));
        Assert.Equal("dev@example.com · Plus plan", vm.ProviderUsage.Groups[1].Account);
        Assert.Equal(["5-hour limit", "Weekly limit"], vm.ProviderUsage.Groups[1].Meters.Select(m => m.Label));
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
        // The session's provider reports nothing: the popover has no plan section (no made-up numbers)
        var e = await OpenAsync();
        var vm = e.Vm;
        await Until(() => vm.Usage.HasPercent, "context ring");
        vm.Usage.IsOpen = true;
        await Until(() => !vm.Usage.IsLoading && vm.Usage.HasContext && UsageReads(e) == 1, "popover loaded");
        Assert.False(vm.Usage.HasLimits);
        Assert.Empty(vm.Usage.Limits);
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

    /// <summary>Pictures of the popover and the settings card, light and dark (OMPGUI_REVIEW_DIR/usage).</summary>
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Usage_screens(string theme)
    {
        if (Environment.GetEnvironmentVariable("OMPGUI_REVIEW_DIR") is not { } dir) return;
        Directory.CreateDirectory(Path.Combine(dir, "usage"));
        MainViewModel.ApplyTheme(theme);
        try
        {
            var e = await OpenAsync(model: Claude);
            var vm = e.Vm;
            await Send(e, "Explain the parser change");
            await Until(() => vm.Phase == SessionPhase.Ready && vm.Usage.HasPercent, "reply");
            vm.Usage.IsOpen = true;
            await Until(() => !vm.Usage.IsLoading && vm.Usage.HasLimits && vm.Usage.HasTotals, "popover");
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
