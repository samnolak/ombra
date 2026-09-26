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

/// <summary>A total from <c>/usage</c> ("Input tokens", "Cost") as a label and value.</summary>
public sealed record UsageTotalViewModel(string Label, string Value);

/// <summary>A provider limit from <c>/usage</c> with its bar.</summary>
public sealed record UsageLimitViewModel(string Title, string Account, double Fraction, string Used, string Resets, bool InUse, bool HasAmount)
{
    public double Value => Math.Clamp(Fraction * 100, 0, 100);
    public bool HasAccount => Account.Length > 0;
    public bool HasResets => Resets.Length > 0;
    /// <summary>Near the limit: the bar in the warning colour (with the percentage beside it).</summary>
    public bool IsHigh => Fraction >= 0.8;
}

/// <summary>
/// Claude Code's context ring next to the model: how full the model's context is (get_state's contextUsage, after
/// every turn), in the warning colour from 70 % and the negative one from 85 % (omp compacts near the end). Its
/// popover draws omp's <c>/context</c> breakdown as bars, adds <c>/usage</c> (tokens and cost, or the providers'
/// limits) and offers "Compact now"; it refreshes after each turn while it is open.
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

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _contextHeadline = "";
    [ObservableProperty] private string _contextNote = "";
    [ObservableProperty] private string _usageNote = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _hasContext;
    [ObservableProperty] private bool _hasTotals;
    [ObservableProperty] private bool _hasLimits;
    /// <summary>omp printed something the popover does not know how to draw: shown as it is.</summary>
    [ObservableProperty] private string _rawUsage = "";

    public ObservableCollection<ContextRowViewModel> ContextRows { get; } = [];
    public ObservableCollection<UsageTotalViewModel> Totals { get; } = [];
    public ObservableCollection<UsageLimitViewModel> Limits { get; } = [];

    /// <summary>From each applied snapshot: the ring, and a refresh of the open popover once a turn ended.</summary>
    internal void Apply(SessionSnapshot s)
    {
        if (Percent != s.ContextPercent) Percent = s.ContextPercent;
        if (s.RunsEnded != _runsEnded)
        {
            var first = _runsEnded < 0;
            _runsEnded = s.RunsEnded;
            if (!first && IsOpen) _ = LoadAsync();
        }
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (value) _ = LoadAsync();
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    internal void RefreshIfOpen()
    {
        if (IsOpen) _ = LoadAsync();
    }

    private bool CanCompactNow() => owner.CompactConversationCommand.CanExecute(null);

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

    /// <summary>Runs <c>/context</c> and <c>/usage</c> and draws what they print.</summary>
    internal async Task LoadAsync()
    {
        var load = ++_loads;
        IsLoading = true;
        Error = "";
        try
        {
            if (!owner.CanRunOmpCommands)
            {
                Error = "omp is not running.";
                return;
            }
            owner.Session.RefreshState(); // the ring from get_state, measured now
            var context = await owner.RunOmpCommandAsync("/context");
            var usage = await owner.RunOmpCommandAsync("/usage", TimeSpan.FromMinutes(1));
            if (load != _loads) return; // a newer refresh is on its way
            ApplyContext(context);
            ApplyUsage(usage);
        }
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
            ContextHeadline = "Context";
            ContextNote = r.Ok ? r.Output.Trim() : r.Error ?? "omp did not answer.";
            return;
        }
        if (report.Unavailable is { } why)
        {
            HasContext = false;
            ContextHeadline = "Context";
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

    private void ApplyUsage(SlashCommandResult r)
    {
        Totals.Clear();
        Limits.Clear();
        RawUsage = "";
        UsageNote = "";
        var report = r.Ok ? SessionOutputs.ParseUsage(r.Output) : null;
        if (report is null)
        {
            HasTotals = HasLimits = false;
            if (r.Ok) RawUsage = r.Output.Trim();
            else UsageNote = r.Error ?? "omp did not answer.";
            return;
        }
        foreach (var (label, value) in report.Totals)
            Totals.Add(new UsageTotalViewModel(label.Replace(" tokens", "", StringComparison.Ordinal), Friendly(label, value)));
        foreach (var l in report.Limits)
        {
            var title = l.Provider.Length > 0 && !l.Label.StartsWith(l.Provider, StringComparison.OrdinalIgnoreCase) ? $"{l.Provider} · {l.Label}" : l.Label;
            Limits.Add(new UsageLimitViewModel(title, l.Account ?? "", (l.UsedPercent ?? 0) / 100.0,
                l.UsedPercent is { } u ? string.Create(CultureInfo.InvariantCulture, $"{u:0.#}% used") : l.Detail ?? "",
                l.Resets ?? "", l.InUse, l.UsedPercent is not null));
        }
        HasTotals = Totals.Count > 0;
        HasLimits = Limits.Count > 0;
        UsageNote = string.Join(" ", report.Notes.Concat(report.Age is { } age ? [$"Reported {age} ago."] : []));
    }

    /// <summary>"1234567" → "1,234,567"; the cost as omp prints it but with two decimals when it is round.</summary>
    private static string Friendly(string label, string value)
    {
        if (label == "Cost" && value.StartsWith('$') && decimal.TryParse(value[1..], NumberStyles.Number, CultureInfo.InvariantCulture, out var cost))
            return "$" + cost.ToString(cost >= 0.01m || cost == 0 ? "0.00" : "0.0000", CultureInfo.InvariantCulture);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? SessionOutputs.Tokens(n) : value;
    }
}
