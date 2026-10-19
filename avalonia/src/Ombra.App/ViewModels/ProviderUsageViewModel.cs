using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

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
    public bool ShowAdvice => IsHigh || IsFull;
    public string Advice => Loc.T("Near the plan limit. Wait for the reset or choose another provider.");
    public string AccessibleName => Resets.Length > 0 ? $"{Label}: {Amount}, {Resets}" : $"{Label}: {Amount}";
}

/// <summary>
/// One account's limits at one provider (a group of meters), and its saved rate-limit resets with "Use a saved
/// reset": asked once more in place, then spent through omp (<c>/usage reset</c>).
/// </summary>
public sealed partial class ProviderUsageGroupViewModel(ProviderUsageViewModel owner, ProviderUsageReport report, string name, string account,
    IReadOnlyList<UsageMeterViewModel> meters) : ObservableObject
{
    public ProviderUsageReport Report { get; } = report;
    public string Provider => Report.Provider;
    public string Name { get; } = name;
    public string Account { get; } = account;
    public IReadOnlyList<UsageMeterViewModel> Meters { get; } = meters;
    public bool HasAccount => Account.Length > 0;

    public bool HasSavedResets => Report.Resets is { Saved: > 0 };
    /// <summary>"2 saved resets".</summary>
    public string SavedResetsText => Report.Resets is { } r ? Loc.Plural(r.Saved, "{0} saved reset", "{0} saved resets") : "";
    /// <summary>"1 usable now · the soonest expires in 3d 4h", or why none can be spent now.</summary>
    public string SavedResetsDetail => Report.Resets is { } r ? ResetsDetail(r, DateTimeOffset.UtcNow) : "";
    public bool CanSpendReset => Report.Resets is { Usable: > 0 } && !IsSpending;

    /// <summary>"Use a saved reset" was pressed: the group asks before anything is spent.</summary>
    [ObservableProperty] private bool _isConfirmingSpend;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSpendReset))]
    private bool _isSpending;
    /// <summary>What omp answered to the last spend (kept across re-reads of the usage).</summary>
    [ObservableProperty] private string _spendMessage = "";
    [ObservableProperty] private bool _spendFailed;
    public bool HasSpendMessage => SpendMessage.Length > 0;
    partial void OnSpendMessageChanged(string value) => OnPropertyChanged(nameof(HasSpendMessage));

    public string ConfirmText => Loc.F("Spend one of {0}’s saved resets now? The provider refreshes the limit it covers; a spent reset cannot be taken back.", Name);

    [RelayCommand]
    private void AskSpendReset()
    {
        owner.ForgetSpendMessage(this);
        SpendMessage = "";
        IsConfirmingSpend = true;
    }

    [RelayCommand]
    private void CancelSpendReset() => IsConfirmingSpend = false;

    [RelayCommand]
    private Task SpendReset()
    {
        IsConfirmingSpend = false;
        return owner.SpendResetAsync(this);
    }

    internal static string ResetsDetail(ProviderUsageResets r, DateTimeOffset now)
    {
        var parts = new List<string>();
        if (r.Usable != r.Saved) parts.Add(Loc.F("{0} usable now", r.Usable));
        if (r.SoonestExpiry is { } at)
            parts.Add(at > now ? Loc.F("the soonest expires in {0}", ProviderUsageViewModel.Span(at - now)) : Loc.F("expired {0}", at.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)));
        if (r.Usable == 0 && r.UnavailableFormat is { } why) parts.Add(Loc.F("unavailable: {0}", Loc.F(why, r.UnavailableArgs ?? [])));
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// The subscription limits of every signed-in provider that reports them (Claude's and ChatGPT's 5-hour and weekly
/// windows, API spend…), read from <c>omp usage --json</c>: a side process, never a message in the conversation.
/// Explicitly read in Settings → Model providers, at most every <see cref="MinInterval"/> unless refreshed by hand
/// or marked stale (a run ended). Providers without a usage source are simply absent.
/// </summary>
public sealed partial class ProviderUsageViewModel : ObservableObject
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    private readonly MainViewModel _main;
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;
    private Task? _loading;
    private bool _stale = true;
    private IReadOnlyList<ProviderUsageReport> _reports = [];
    /// <summary>omp's answer to the last spend, by provider and account, shown in that group until the next spend.</summary>
    private readonly Dictionary<(string Provider, string? Account), (string Text, bool Failed)> _spendMessages = [];

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
    /// <summary>Timestamp of the oldest report shown; retained across failed refreshes.</summary>
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private bool _refreshFailed;

    /// <summary>A run ended: the next load asks omp again even within <see cref="MinInterval"/>.</summary>
    internal void MarkStale() => _stale = true;

    [RelayCommand]
    private Task Refresh() => LoadAsync(force: true);

    /// <summary>Reads the reports again unless they are recent; concurrent calls share one read.</summary>
    internal Task LoadAsync(bool force = false)
    {
        if (force) _stale = true;
        if (!_main.IsSettingsOpen || _main.SettingsCategory != "providers") return Task.CompletedTask;
        if (_loading is { IsCompleted: false } running) return running;
        if (!force && !_stale && DateTimeOffset.UtcNow - _fetchedAt < MinInterval)
        {
            Apply(); // refresh the reset countdown without querying the provider
            return Task.CompletedTask;
        }
        return _loading = FetchAsync();
    }

    private async Task FetchAsync()
    {
        IsLoading = true;
        _stale = false; // invalidations arriving during this read must survive its completion
        try
        {
            var r = await _main.RunOmpCliAsync(ProviderUsage.CliArgs, TimeSpan.FromSeconds(30), _main.Closing);
            if (r.Ok && ProviderUsage.Parse(r.Stdout) is { } reports)
            {
                _fetchedAt = DateTimeOffset.UtcNow;
                _reports = reports;
                RefreshFailed = false;
            }
            else
            {
                // Keep the last real report, but never present it as a successful refresh.
                RefreshFailed = true;
                _stale = true;
            }
            Apply();
        }
        catch (OperationCanceledException) { _stale = true; }
        finally { IsLoading = false; }
    }

    private void Apply()
    {
        var now = DateTimeOffset.UtcNow;
        Groups.Clear();
        foreach (var report in _reports)
        {
            var name = ProviderUsage.ProviderName(report.Provider);
            var meters = report.Limits.Select(l => new UsageMeterViewModel(ProviderUsage.DisplayLabel(l, name, Loc.T), l.UsedFraction, Amount(l),
                l.ResetsAt is { } at ? ResetsIn(at, now, l.ResetVerb) : "", l.Status == "exhausted")).ToList();
            var plan = report.Plan is { Length: > 0 } p ? Loc.F("{0} plan", CultureInfo.InvariantCulture.TextInfo.ToTitleCase(p)) : "";
            var account = string.Join(" · ", new[] { report.Account ?? "", plan }.Where(s => s.Length > 0));
            var group = new ProviderUsageGroupViewModel(this, report, name, account, meters);
            if (_spendMessages.TryGetValue((report.Provider, report.Account), out var said)) (group.SpendMessage, group.SpendFailed) = said;
            Groups.Add(group);
        }
        HasGroups = Groups.Count > 0;
        var oldest = _reports.Select(r => r.FetchedAt).OfType<DateTimeOffset>().DefaultIfEmpty(_fetchedAt).Min();
        Note = HasGroups ? Loc.F("As of {0}", oldest.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)) : "";
    }

    internal void ForgetSpendMessage(ProviderUsageGroupViewModel group) => _spendMessages.Remove((group.Provider, group.Report.Account));

    /// <summary>
    /// Spends one of the group's saved resets: omp lists the accounts with their ids (<c>/usage reset</c>), then spends
    /// one of this account's; its answer stays under the group, and the limits are read again.
    /// </summary>
    internal async Task SpendResetAsync(ProviderUsageGroupViewModel group)
    {
        var key = (group.Provider, group.Report.Account);
        group.IsSpending = true;
        group.SpendMessage = Loc.T("Spending a saved reset…");
        group.SpendFailed = false;
        string text;
        var failed = true;
        if (!_main.CanRunOmpCommands) text = Loc.T("omp is not running: open a chat, then try again.");
        else
        {
            var list = await _main.RunOmpCommandAsync(ProviderUsage.SavedResetsCommand, TimeSpan.FromMinutes(1), _main.Closing);
            if (!list.Ok) text = list.Error ?? list.Output;
            else if (ProviderUsage.ResetAccountFor(group.Report, ProviderUsage.ParseSavedResetAccounts(list.Output)) is not { } target)
                text = Loc.T("omp did not list this account among the ones with saved resets.");
            else
            {
                var spend = await _main.RunOmpCommandAsync(ProviderUsage.SpendResetCommand(target), TimeSpan.FromMinutes(1), _main.Closing);
                text = spend.Ok ? spend.Output : spend.Error ?? spend.Output;
                failed = !spend.Ok || !ProviderUsage.ResetApplied(spend.Output);
            }
        }
        _spendMessages[key] = (text, failed);
        group.IsSpending = false;
        (group.SpendMessage, group.SpendFailed) = (text, failed);
        await LoadAsync(force: true);
    }

    /// <summary>A time left in its two largest units: "3d 4h", "2h 10m", "45m" (in the app's language).</summary>
    internal static string Span(TimeSpan left) =>
        left.TotalDays >= 1 ? Loc.F("{0}d", (int)left.TotalDays) + (left.Hours > 0 ? " " + Loc.F("{0}h", left.Hours) : "")
        : left.TotalHours >= 1 ? left.Minutes > 0 ? Loc.F("{0}h {1}m", (int)left.TotalHours, left.Minutes) : Loc.F("{0}h", (int)left.TotalHours)
        : Loc.F("{0}m", (int)left.TotalMinutes);

    /// <summary>"resets in 2h 10m", "resets soon" under a minute; a verb of omp's other than "resets" stays omp's words.</summary>
    internal static string ResetsIn(DateTimeOffset at, DateTimeOffset now, string verb)
    {
        if (verb != "resets") return ProviderUsage.ResetsIn(at, now, verb);
        var left = at - now;
        return left < TimeSpan.FromMinutes(1) ? Loc.T("resets soon") : Loc.F("resets in {0}", Span(left));
    }

    /// <summary>The share used ("42% used"), else the amount: "$12.40 used", "1,200 of 5,000 requests" (unit as omp names it).</summary>
    internal static string Amount(ProviderUsageLimit l)
    {
        if (l.UsedFraction is { } f) return Loc.F("{0:0}% used", Math.Round(f * 100));
        if (l.Used is not { } used) return "";
        string N(double v) => v.ToString(l.Unit == "usd" ? "$#,0.00" : "#,0.##", CultureInfo.CurrentCulture);
        var unit = l.Unit is "usd" or "unknown" or "percent" ? "" : " " + l.Unit;
        return l.Limit is { } max ? Loc.F("{0} of {1}", N(used), N(max) + unit) : Loc.F("{0} used", N(used) + unit);
    }
}

public partial class MainViewModel
{
    private ProviderUsageViewModel? _providerUsage;

    /// <summary>Signed-in providers' plan limits, read only in Settings → Model providers.</summary>
    public ProviderUsageViewModel ProviderUsage => _providerUsage ??= new ProviderUsageViewModel(this);
}
