using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>One limit as a meter: its window, a bar with the share used, and when it resets.</summary>
public sealed record UsageMeterViewModel(string Label, double? Fraction, string Amount, string Resets, bool Exhausted)
{
    public bool HasBar => Fraction is not null;
    public double Value => Math.Clamp((Fraction ?? 0) * 100, 0, 100);
    public bool HasResets => Resets.Length > 0;
    /// <summary>Near the limit: the bar in the warning colour.</summary>
    public bool IsHigh => Fraction >= 0.8 && !IsFull;
    /// <summary>Used up: the bar in the negative colour.</summary>
    public bool IsFull => Exhausted || Fraction >= 1;
    public string AccessibleName => Resets.Length > 0 ? $"{Label}: {Amount}, {Resets}" : $"{Label}: {Amount}";
}

/// <summary>One account's limits at one provider (a group of meters).</summary>
public sealed record ProviderUsageGroupViewModel(string Provider, string Name, string Account, IReadOnlyList<UsageMeterViewModel> Meters)
{
    public bool HasAccount => Account.Length > 0;
}

/// <summary>
/// The subscription limits of every signed-in provider that reports them (Claude's and ChatGPT's 5-hour and weekly
/// windows, API spend…), read from <c>omp usage --json</c>: a side process, never a message in the conversation.
/// omp caches the reports itself; this asks it at most every <see cref="MinInterval"/> unless refreshed by hand or
/// marked stale (a run ended). Providers without a usage source are simply absent.
/// </summary>
public sealed partial class ProviderUsageViewModel : ObservableObject
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    private readonly MainViewModel _main;
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;
    private Task? _loading;
    private bool _stale = true;
    private IReadOnlyList<ProviderUsageReport> _reports = [];

    internal ProviderUsageViewModel(MainViewModel main)
    {
        _main = main;
        main.SettingsCategoryShown += key =>
        {
            if (key == "providers") _ = LoadAsync();
        };
        // Made after the page was shown (the view model is made on first use): load now.
        if (main.IsSettingsOpen && main.SettingsCategory == "providers") Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    /// <summary>Every account's limits, grouped by provider.</summary>
    public ObservableCollection<ProviderUsageGroupViewModel> Groups { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasGroups;
    /// <summary>"Updated 3m ago"; empty while fresh or when nothing was reported.</summary>
    [ObservableProperty] private string _note = "";

    /// <summary>Raised after new reports were applied.</summary>
    public event Action? Updated;

    /// <summary>A run ended: the next load asks omp again even within <see cref="MinInterval"/>.</summary>
    internal void MarkStale() => _stale = true;

    [RelayCommand]
    private Task Refresh() => LoadAsync(force: true);

    /// <summary>The groups for one provider (the session's model), in report order.</summary>
    internal IEnumerable<ProviderUsageGroupViewModel> For(string provider) => Groups.Where(g => g.Provider == provider);

    /// <summary>Reads the reports again unless they are recent; concurrent calls share one read.</summary>
    internal Task LoadAsync(bool force = false)
    {
        if (_loading is { IsCompleted: false } running) return running;
        if (!force && !_stale && DateTimeOffset.UtcNow - _fetchedAt < MinInterval)
        {
            Apply(); // only the "updated … ago" note moves on
            return Task.CompletedTask;
        }
        return _loading = FetchAsync();
    }

    private async Task FetchAsync()
    {
        IsLoading = true;
        try
        {
            var r = await _main.RunOmpCliAsync(ProviderUsage.CliArgs, TimeSpan.FromSeconds(30), _main.Closing);
            _stale = false;
            _fetchedAt = DateTimeOffset.UtcNow;
            // Only what omp reported: a failed read or an unknown payload shows no meters (never made-up numbers)
            _reports = r.Ok && ProviderUsage.Parse(r.Stdout) is { } reports ? reports : [];
            Apply();
        }
        catch (OperationCanceledException) { }
        finally { IsLoading = false; }
    }

    private void Apply()
    {
        var now = DateTimeOffset.UtcNow;
        Groups.Clear();
        foreach (var report in _reports)
        {
            var name = ProviderUsage.ProviderName(report.Provider);
            var meters = report.Limits.Select(l => new UsageMeterViewModel(ProviderUsage.DisplayLabel(l, name), l.UsedFraction, ProviderUsage.Amount(l),
                l.ResetsAt is { } at ? ProviderUsage.ResetsIn(at, now, l.ResetVerb) : "", l.Status == "exhausted")).ToList();
            var plan = report.Plan is { Length: > 0 } p ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(p) + " plan" : "";
            var account = string.Join(" · ", new[] { report.Account ?? "", plan }.Where(s => s.Length > 0));
            Groups.Add(new ProviderUsageGroupViewModel(report.Provider, name, account, meters));
        }
        HasGroups = Groups.Count > 0;
        var oldest = _reports.Select(r => r.FetchedAt).OfType<DateTimeOffset>().DefaultIfEmpty(now).Min();
        Note = HasGroups && now - oldest >= TimeSpan.FromMinutes(1) ? $"Updated {Age(now - oldest)} ago" : "";
        Updated?.Invoke();
    }

    private static string Age(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h" : $"{(int)t.TotalMinutes}m";
}

public partial class MainViewModel
{
    private ProviderUsageViewModel? _providerUsage;

    /// <summary>Signed-in providers' plan limits (the context popover and Settings → Model providers).</summary>
    public ProviderUsageViewModel ProviderUsage => _providerUsage ??= new ProviderUsageViewModel(this);
}
