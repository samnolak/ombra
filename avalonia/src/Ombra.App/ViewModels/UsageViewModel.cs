using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.App.ViewModels;

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
/// popover draws omp's <c>/context</c> breakdown, this session's tokens and cost (<c>get_session_stats</c>),
/// and the active account's exceptional usage stage from <c>get_state</c>. Full provider reports belong to Settings.
/// </summary>
public sealed partial class UsageViewModel(MainViewModel owner) : ObservableObject
{
    public const double WarnAt = 70, FullAt = 85;
    private long _runsEnded = -1;
    private int _loads;
    private long _epoch = -1;
    private string? _model;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsageLimit), nameof(HasIndicator), nameof(LimitBadge), nameof(LimitStatus), nameof(Tooltip), nameof(AccessibleName))]
    private OmpUsageLimit? _usageLimit;

    public bool HasUsageLimit => UsageLimit is not null;
    public bool HasIndicator => HasPercent || HasUsageLimit;
    public string LimitBadge => UsageLimit?.Stage == "low_priority" ? Loc.T("Low priority")
        : UsageLimit is not null ? Loc.T("Limit reached · wrapping up") : "";
    public string LimitStatus
    {
        get
        {
            if (UsageLimit is not { } limit) return "";
            var text = limit.Stage == "low_priority" ? Loc.T("Low priority")
                : limit.ExtraUsage ? Loc.T("Limit reached · wrap-up, then extra usage") : Loc.T("Limit reached · wrapping up");
            // In the app's language and 24-hour clock, as the conversation's times ("14:08", "29 Sep, 14:08"), never the
            // system region's mix ("10/18/2026 2:30 PM" inside a Russian sentence)
            if (limit.ResetsAt is { } reset) text += " · " + Loc.F("Resets {0}", MessageTimes.Short(reset));
            if (limit.AllowanceLeftPercent is { } left) text += " · " + Loc.F("Low-priority allowance: {0}% left", left);
            return text;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPercent), nameof(HasIndicator), nameof(PercentText), nameof(RingLevel), nameof(IsWarn), nameof(IsFull), nameof(Tooltip), nameof(AccessibleName))]
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

    public string Tooltip => (Percent is { } p
        ? string.Format(CultureInfo.InvariantCulture, Loc.T("Context {0:0}% used"), p) + (IsFull ? Loc.T(" — omp compacts it soon") : "") + Loc.T(" · details")
        : Loc.T("Context usage: details")) + (HasUsageLimit ? " · " + LimitStatus : "");

    public string AccessibleName => (Percent is { } p ? string.Format(CultureInfo.InvariantCulture, Loc.T("Context usage, {0:0} percent"), p) : Loc.T("Context usage"))
        + (HasUsageLimit ? " · " + LimitStatus : "");

    /// <summary>The popover's headline when omp gave no breakdown: the ring's own number, not a bare "Context".</summary>
    private string PercentHeadline => PercentText.Length > 0 ? Loc.F("{0} of the context used", PercentText) : Loc.T("Context");

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _contextHeadline = "";
    [ObservableProperty] private string _contextNote = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _hasContext;
    [ObservableProperty] private bool _hasTotals;

    public ObservableCollection<ContextRowViewModel> ContextRows { get; } = [];
    public ObservableCollection<UsageTotalViewModel> Totals { get; } = [];

    /// <summary>From each applied snapshot: the ring, and a refresh of the open popover once a turn ended.</summary>
    internal void Apply(SessionSnapshot s)
    {
        if (_epoch != s.TranscriptEpoch || _model != s.Model)
        {
            _epoch = s.TranscriptEpoch;
            _model = s.Model;
            ++_loads;
            IsLoading = false;
            ContextRows.Clear();
            Totals.Clear();
            HasContext = HasTotals = false;
            ContextHeadline = ContextNote = Error = "";
            _runsEnded = -1;
        }
        UsageLimit = s.UsageLimit;
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

    /// <summary>Reads context and session stats through the active connection, never provider CLI reports.</summary>
    internal async Task LoadAsync()
    {
        var load = ++_loads;
        var session = owner.Session;
        var snapshot = session.Snapshot();
        bool IsCurrent() => load == _loads && ReferenceEquals(owner.Session, session)
            && session.Snapshot() is var current && current.TranscriptEpoch == snapshot.TranscriptEpoch && current.Model == snapshot.Model;
        IsLoading = true;
        Error = "";
        try
        {
            if (!owner.CanRunOmpCommands)
            {
                Error = Loc.T("omp is not running.");
                return;
            }
            session.RefreshState();
            var context = await owner.RunOmpCommandAsync("/context");
            if (!IsCurrent()) return;
            var stats = await session.GetSessionStatsAsync(owner.Closing);
            if (!IsCurrent()) return;
            ApplyContext(context);
            ApplyTotals(stats);
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
            ContextNote = r.Ok ? r.Output.Trim() : r.Error ?? Loc.T("omp did not answer.");
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
        ContextHeadline = string.Format(CultureInfo.InvariantCulture,
            Loc.T("{0:0}% of {1} tokens used"), report.UsedPercent, SessionOutputs.ShortTokens(report.Window));
        foreach (var c in report.Categories)
            ContextRows.Add(new ContextRowViewModel(c.Label, c.Fraction, SessionOutputs.Tokens(c.Tokens),
                string.Create(CultureInfo.InvariantCulture, $"{c.Fraction * 100:0}%"), c.Label == "Free", c.Label == "Auto-compact buffer"));
        if (ContextRows.Any(x => x.IsReserve))
            ContextNote = Loc.T("The auto-compact buffer is kept free: omp compacts the conversation when the rest is full.");
    }

    private void ApplyTotals(SessionTokenStats? s)
    {
        Totals.Clear();
        if (s is not null)
        {
            Totals.Add(new UsageTotalViewModel(Loc.T("Input"), SessionOutputs.Tokens(s.Input)));
            Totals.Add(new UsageTotalViewModel(Loc.T("Output"), SessionOutputs.Tokens(s.Output)));
            if (s.CacheRead > 0) Totals.Add(new UsageTotalViewModel(Loc.T("Cache read"), SessionOutputs.Tokens(s.CacheRead)));
            if (s.CacheWrite > 0) Totals.Add(new UsageTotalViewModel(Loc.T("Cache write"), SessionOutputs.Tokens(s.CacheWrite)));
            if (s.PremiumRequests > 0) Totals.Add(new UsageTotalViewModel(Loc.T("Premium requests"), SessionOutputs.Tokens(s.PremiumRequests)));
            // omp prices a turn from the model's catalog rates; nothing to show when it has none (0)
            if (s.Cost > 0) Totals.Add(new UsageTotalViewModel(Loc.T("Cost"), Money(s.Cost)));
        }
        HasTotals = Totals.Count > 0;
    }

    /// <summary>"$0.42", and four decimals under a cent so a cheap session does not read as free.</summary>
    private static string Money(double cost) =>
        "$" + cost.ToString(cost >= 0.01 ? "#,0.00" : "0.0000", CultureInfo.InvariantCulture);
}
