using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>What the sidebar's dot before a session says. Only the open session runs (one omp at a time).</summary>
public enum SessionStatus
{
    None,
    /// <summary>omp is working in it.</summary>
    Running,
    /// <summary>omp waits for an approval or an answer.</summary>
    Waiting,
    /// <summary>A run ended while the window was in the background; cleared when the window is back in front.</summary>
    Unread,
}

/// <summary>One saved session in the sidebar.</summary>
public sealed partial class SessionItemViewModel(SessionSummary model) : ObservableObject
{
    public SessionSummary Model { get; } = model;
    /// <summary>The header's title for the open session while the saved list still has the old one (a first run, a rename).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Title), nameof(Tooltip))] private string? _liveTitle;
    public string Title => LiveTitle ?? Model.Title;
    public string Project => ProjectName(Model.Cwd);
    public string Cwd => Model.Cwd;
    /// <summary>How long ago the last message was ("now", "5m", "3h", "2d", then the date); <see cref="RefreshWhen"/>
    /// re-reads it (the sidebar does every minute).</summary>
    public string When => Relative(Model.LastMessageAt, DateTimeOffset.Now);
    public string Tooltip => $"{Title}\n{Model.Cwd}\n{Model.LastMessageAt.ToLocalTime().ToString("f", CultureInfo.CurrentCulture)}";

    /// <summary>The row menu's commands (MainViewModel's, set when the row is made): a menu opens in a popup, outside
    /// the window's visual tree, so it cannot reach the window's DataContext.</summary>
    public System.Windows.Input.ICommand? OpenCommand { get; init; }
    public System.Windows.Input.ICommand? DeleteCommand { get; init; }
    public System.Windows.Input.ICommand? RenameCommand { get; init; }
    public System.Windows.Input.ICommand? TogglePinCommand { get; init; }
    public System.Windows.Input.ICommand? CopyPathCommand { get; init; }

    [ObservableProperty] private bool _isCurrent;

    /// <summary>Pinned in omp's session list (<c>session-pins.json</c>, as omp's <c>/pin</c>): listed under Pinned.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PinLabel), nameof(PinTooltip))] private bool _isPinned;
    public string PinLabel => IsPinned ? "Unpin" : "Pin";
    public string PinTooltip => IsPinned ? "Unpin" : "Pin to the top";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsWaiting), nameof(IsUnread), nameof(HasStatus), nameof(StatusText))]
    private SessionStatus _status;

    /// <summary>omp is working in this session right now (the open session during a run).</summary>
    public bool IsActive => Status == SessionStatus.Running;
    public bool IsWaiting => Status == SessionStatus.Waiting;
    public bool IsUnread => Status == SessionStatus.Unread;
    public bool HasStatus => Status != SessionStatus.None;
    public string StatusText => Status switch
    {
        SessionStatus.Running => "omp is working",
        SessionStatus.Waiting => "Needs your input",
        SessionStatus.Unread => "New reply",
        _ => "",
    };

    public void RefreshWhen() => OnPropertyChanged(nameof(When));

    public static string ProjectName(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return "";
        var trimmed = Path.TrimEndingDirectorySeparator(cwd);
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }

    /// <summary>Claude Code's sidebar ages: "now" under a minute (and for a clock running behind), then 5m, 3h, 2d;
    /// from a week on the date (the year only when it is not this one).</summary>
    public static string Relative(DateTimeOffset at, DateTimeOffset now)
    {
        var d = now - at;
        if (d < TimeSpan.FromMinutes(1)) return "now";
        if (d < TimeSpan.FromHours(1)) return string.Create(CultureInfo.InvariantCulture, $"{(int)d.TotalMinutes}m");
        if (d < TimeSpan.FromDays(1)) return string.Create(CultureInfo.InvariantCulture, $"{(int)d.TotalHours}h");
        if (d < TimeSpan.FromDays(7)) return string.Create(CultureInfo.InvariantCulture, $"{(int)d.TotalDays}d");
        var local = at.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        return local.Year == now.ToLocalTime().Year
            ? local.ToString(culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM"), culture)
            : local.ToString("d", culture);
    }
}
