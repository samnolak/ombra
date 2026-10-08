using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>One bar of the context breakdown: a category, its share of the window, its tokens.</summary>
public sealed record ContextRowViewModel(string Label, double Fraction, string Tokens, string Percent, bool IsFree, bool IsReserve)
{
    /// <summary>The bar's value (0–100); a category that is there at all shows at least a sliver.</summary>
    public double Value => Fraction <= 0 ? 0 : Math.Clamp(Math.Max(Fraction * 100, 1.5), 0, 100);
    public string Tooltip => $"{Label}: {Tokens} tokens ({Percent} of the context window)";
    public bool IsUsed => !IsFree && !IsReserve;
}

/// <summary>One of this session's tallies (<c>get_session_stats</c>): "Input", "Cost".</summary>
public sealed record UsageTotalViewModel(string Label, string Value);

/// <summary>
/// Claude Code's context ring next to the model: how full the model's context is (get_state's contextUsage, after
/// every turn), in the warning colour from 70 % and the negative one from 85 % (omp compacts near the end). Its
/// popover draws omp's <c>/context</c> breakdown as bars, this session's tokens and cost (<c>get_session_stats</c>)
/// and the plan limits of the model's provider (<see cref="ProviderUsageViewModel"/>), and offers "Compact now"; it
/// refreshes after each turn while it is open.
/// </summary>
public sealed partial class UsageViewModel(MainViewModel owner) : ObservableObject
{
    public const double WarnAt = 70, FullAt = 85;
    private long _runsEnded = -1;
    private int _loads;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPercent), nameof(PercentText), nameof(RingLevel), nameof(IsWarn), nameof(IsFull), nameof(Tooltip), nameof(AccessibleName))]
    private double? _percent;

    public bool HasPercent => Percent is not null;
    /// <summary>Next to the ring, so a small value never reads as a loading spinner: "12%", "&lt;1%".</summary>
    public string PercentText => Percent switch
    {
        null => "",
        > 0 and < 1 => "<1%",
        { } p => string.Create(CultureInfo.InvariantCulture, $"{Math.Min(p, 100):0}%"),
    };
    public bool IsWarn => Percent is >= WarnAt and < FullAt;
    public bool IsFull => Percent >= FullAt;
    public string RingLevel => IsFull ? "full" : IsWarn ? "warn" : "normal";

    public string Tooltip => Percent is { } p
        ? string.Create(CultureInfo.InvariantCulture, $"Context {p:0}% used") + (IsFull ? " — omp compacts it soon" : "") + " · details"
        : "Context usage: details";

    public string AccessibleName => Percent is { } p ? string.Create(CultureInfo.InvariantCulture, $"Context usage, {p:0} percent") : "Context usage";

    /// <summary>The popover's headline when omp gave no breakdown: the ring's own number, not a bare "Context".</summary>
    private string PercentHeadline => PercentText.Length > 0 ? $"{PercentText} of the context used" : "Context";

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _contextHeadline = "";
    [ObservableProperty] private string _contextNote = "";
    [ObservableProperty] private string _usageNote = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _hasContext;
    [ObservableProperty] private bool _hasTotals;
    [ObservableProperty] private bool _hasLimits;

    public ObservableCollection<ContextRowViewModel> ContextRows { get; } = [];
    public ObservableCollection<UsageTotalViewModel> Totals { get; } = [];
    /// <summary>The plan limits of the session model's provider (every account omp has for it).</summary>
    public ObservableCollection<ProviderUsageGroupViewModel> Limits { get; } = [];

    /// <summary>From each applied snapshot: the ring, and a refresh of the open popover once a turn ended.</summary>
    internal void Apply(SessionSnapshot s)
    {
        // Like Claude: no ring on a new chat. Before the first message omp's percentage is its system prompt and tools
        // alone (5% of an empty chat read as something already used); it shows once there is a conversation.
        var percent = s.Items.Any(i => i is not NoticeItem) ? s.ContextPercent : null;
        if (Percent != percent) Percent = percent;
        if (s.RunsEnded != _runsEnded)
        {
            var first = _runsEnded < 0;
            _runsEnded = s.RunsEnded;
            if (first) return;
            owner.ProviderUsage.MarkStale(); // a turn spends plan allowance
            if (IsOpen) _ = LoadAsync();
        }
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (!value) return;
        CompactNowCommand.NotifyCanExecuteChanged(); // the conversation may have begun since
        _ = LoadAsync();
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    internal void RefreshIfOpen()
    {
        if (IsOpen) _ = LoadAsync();
    }

    /// <summary>Only with something to compact: an empty new session has no conversation yet.</summary>
    private bool CanCompactNow() => owner.Rows.Count > 0 && owner.CompactConversationCommand.CanExecute(null);

    [RelayCommand(CanExecute = nameof(CanCompactNow))]
    private Task CompactNowAsync()
    {
        IsOpen = false;
        return owner.CompactNowAsync();
    }

    private bool CanOpenDashboard() => owner.OpenUsageDashboardCommand.CanExecute(null);

    /// <summary>omp's usage dashboard across all sessions (<c>/stats</c>), reported on the session card.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenDashboard))]
    private Task OpenDashboardAsync()
    {
        IsOpen = false;
        return owner.OpenUsageDashboardCommand.ExecuteAsync(null);
    }

    /// <summary>All providers' limits: Settings → Model providers.</summary>
    [RelayCommand]
    private Task ShowAllProvidersAsync()
    {
        IsOpen = false;
        return owner.OpenSettingsAtAsync("providers");
    }

    /// <summary>Runs <c>/context</c>, reads <c>get_session_stats</c> and the provider's limits, and draws them.</summary>
    internal async Task LoadAsync()
    {
        var load = ++_loads;
        IsLoading = true;
        Error = "";
        try
        {
            var limits = owner.ProviderUsage.LoadAsync(); // a side process: runs while omp answers the rest
            if (!owner.CanRunOmpCommands)
            {
                Error = "omp is not running.";
                return;
            }
            owner.Session.RefreshState(); // the ring from get_state, measured now
            var context = await owner.RunOmpCommandAsync("/context");
            var stats = await owner.Session.GetSessionStatsAsync(owner.Closing);
            await limits;
            if (load != _loads) return; // a newer refresh is on its way
            ApplyContext(context);
            ApplyTotals(stats);
            ApplyLimits();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (load == _loads) IsLoading = false;
        }
    }

    private void ApplyContext(SlashCommandResult r)
    {
        ContextRows.Clear();
        ContextNote = "";
        var report = r.Ok ? SessionOutputs.ParseContext(r.Output) : null;
        if (report is null)
        {
            HasContext = false;
            ContextHeadline = PercentHeadline;
            ContextNote = r.Ok ? r.Output.Trim() : r.Error ?? "omp did not answer.";
            return;
        }
        if (report.Unavailable is { } why)
        {
            HasContext = false;
            ContextHeadline = PercentHeadline;
            ContextNote = why;
            return;
        }
        HasContext = true;
        ContextHeadline = string.Create(CultureInfo.InvariantCulture,
            $"{report.UsedPercent:0}% of {SessionOutputs.ShortTokens(report.Window)} tokens used");
        foreach (var c in report.Categories)
            ContextRows.Add(new ContextRowViewModel(c.Label, c.Fraction, SessionOutputs.Tokens(c.Tokens),
                string.Create(CultureInfo.InvariantCulture, $"{c.Fraction * 100:0}%"), c.Label == "Free", c.Label == "Auto-compact buffer"));
        if (ContextRows.Any(x => x.IsReserve))
            ContextNote = "The auto-compact buffer is kept free: omp compacts the conversation when the rest is full.";
    }

    private void ApplyTotals(SessionTokenStats? s)
    {
        Totals.Clear();
        if (s is not null)
        {
            Totals.Add(new UsageTotalViewModel("Input", SessionOutputs.Tokens(s.Input)));
            Totals.Add(new UsageTotalViewModel("Output", SessionOutputs.Tokens(s.Output)));
            if (s.CacheRead > 0) Totals.Add(new UsageTotalViewModel("Cache read", SessionOutputs.Tokens(s.CacheRead)));
            if (s.CacheWrite > 0) Totals.Add(new UsageTotalViewModel("Cache write", SessionOutputs.Tokens(s.CacheWrite)));
            if (s.PremiumRequests > 0) Totals.Add(new UsageTotalViewModel("Premium requests", SessionOutputs.Tokens(s.PremiumRequests)));
            // omp prices a turn from the model's catalog rates; nothing to show when it has none (0)
            if (s.Cost > 0) Totals.Add(new UsageTotalViewModel("Cost", Money(s.Cost)));
        }
        HasTotals = Totals.Count > 0;
    }

    private void ApplyLimits()
    {
        Limits.Clear();
        var provider = owner.Session.Snapshot().Model is { } m && m.IndexOf('/') is var slash and > 0 ? m[..slash] : null;
        if (provider is not null)
            foreach (var g in owner.ProviderUsage.For(provider)) Limits.Add(g);
        HasLimits = Limits.Count > 0;
        UsageNote = HasLimits ? owner.ProviderUsage.Note : "";
    }

    /// <summary>"$0.42", and four decimals under a cent so a cheap session does not read as free.</summary>
    private static string Money(double cost) =>
        "$" + cost.ToString(cost >= 0.01 ? "#,0.00" : "0.0000", CultureInfo.InvariantCulture);
}
