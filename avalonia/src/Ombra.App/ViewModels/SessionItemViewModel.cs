using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>What the sidebar's dot before a session says. Every chat with an omp open has one, shown or in the background.</summary>
public enum SessionStatus
{
    None,
    /// <summary>omp is working in it.</summary>
    Running,
    /// <summary>omp waits for an approval or an answer.</summary>
    Waiting,
    /// <summary>A run ended while the chat was not shown (or the window was in the background); cleared when it is.</summary>
    Unread,
    /// <summary>Its omp stopped with an error (crashed, or did not start); opening the chat offers to start it again.</summary>
    Error,
}

/// <summary>One saved session in the sidebar. The row lives as long as its session is listed: a rescan of the catalog
/// updates <see cref="Model"/> in place, so the row's control (its hover, focus, open menu) survives the refresh.</summary>
public sealed partial class SessionItemViewModel(SessionSummary model) : ObservableObject
{
    /// <summary>What omp's file says now; replaced (record equality decides) by each catalog scan.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Title), nameof(Project), nameof(Cwd), nameof(When), nameof(WhenFull), nameof(Tooltip), nameof(HasId))]
    private SessionSummary _model = model;
    /// <summary>The header's title for the open session while the saved list still has the old one (a first run, a rename).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Title), nameof(Tooltip))] private string? _liveTitle;
    public string Title => LiveTitle ?? Model.Title;
    public string Project => ProjectName(Model.Cwd);
    public string Cwd => Model.Cwd;
    /// <summary>How long ago the last message was ("now", "5m", "3h", "2d", then the date); <see cref="RefreshWhen"/>
    /// re-reads it (the sidebar does every minute).</summary>
    public string When => Relative(Model.LastMessageAt, DateTimeOffset.Now);
    public string Tooltip => $"{Title}\n{Model.Cwd}\n{WhenFull}";
    /// <summary>The last message's date and time, in full (the hover card's last line).</summary>
    public string WhenFull => Model.LastMessageAt.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
    /// <summary>omp has given it an id yet (a chat not yet saved has none to copy).</summary>
    public bool HasId => !string.IsNullOrEmpty(Model.Id);

    /// <summary>Found by the sidebar search in its messages, not its title: the matching words, shown under the title.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnippet), nameof(SnippetBefore), nameof(SnippetMatch), nameof(SnippetAfter), nameof(SnippetText))]
    private SessionContentMatch? _contentMatch;
    public bool HasSnippet => ContentMatch is not null;
    public string SnippetBefore => ContentMatch?.Before ?? "";
    /// <summary>The searched words as the message has them, highlighted between the rest.</summary>
    public string SnippetMatch => ContentMatch?.Match ?? "";
    public string SnippetAfter => ContentMatch?.After ?? "";
    public string SnippetText => ContentMatch?.Text ?? "";

    /// <summary>The row menu's commands (MainViewModel's, set when the row is made): a menu opens in a popup, outside
    /// the window's visual tree, so it cannot reach the window's DataContext.</summary>
    public System.Windows.Input.ICommand? OpenCommand { get; init; }
    public System.Windows.Input.ICommand? DeleteCommand { get; init; }
    public System.Windows.Input.ICommand? RenameCommand { get; init; }
    public System.Windows.Input.ICommand? TogglePinCommand { get; init; }
    public System.Windows.Input.ICommand? CopyPathCommand { get; init; }
    public System.Windows.Input.ICommand? CopyIdCommand { get; init; }
    /// <summary>Export as HTML and Share act on the chat shown (omp's /export, /share): offered on its row only.</summary>
    public System.Windows.Input.ICommand? ExportCommand { get; init; }
    public System.Windows.Input.ICommand? ShareCommand { get; init; }
    public System.Windows.Input.ICommand? ToggleArchiveCommand { get; init; }
    public System.Windows.Input.ICommand? ToggleReadCommand { get; init; }
    public System.Windows.Input.ICommand? MarkAllReadCommand { get; init; }
    public System.Windows.Input.ICommand? RevealCommand { get; init; }
    public System.Windows.Input.ICommand? OpenTerminalCommand { get; init; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReadLabel))] private bool _isMarkedUnread;
    public string ReadLabel => IsMarkedUnread ? Loc.T("Mark as read") : Loc.T("Mark as unread");

    [ObservableProperty] private bool _isCurrent;

    /// <summary>Pinned in omp's session list (<c>session-pins.json</c>, as omp's <c>/pin</c>): listed under Pinned.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PinLabel), nameof(PinTooltip))] private bool _isPinned;
    public string PinLabel => IsPinned ? Loc.T("Unpin") : Loc.T("Pin");
    public string PinTooltip => IsPinned ? Loc.T("Unpin") : Loc.T("Pin to the top");

    /// <summary>Archived from the sidebar (the client's settings, nothing on disk changes): listed under Archived.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ArchiveLabel))] private bool _isArchived;
    public string ArchiveLabel => IsArchived ? Loc.T("Unarchive") : Loc.T("Archive");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsWaiting), nameof(IsUnread), nameof(IsFailed), nameof(HasStatus), nameof(HasDot), nameof(StatusText))]
    private SessionStatus _status;

    /// <summary>omp is working in this session right now.</summary>
    public bool IsActive => Status == SessionStatus.Running;
    public bool IsWaiting => Status == SessionStatus.Waiting;
    public bool IsUnread => Status == SessionStatus.Unread;
    public bool IsFailed => Status == SessionStatus.Error;
    public bool HasStatus => Status != SessionStatus.None;
    /// <summary>A dot for every status but waiting on the user, which shows a raised hand.</summary>
    public bool HasDot => HasStatus && !IsWaiting;
    public string StatusText => Status switch
    {
        SessionStatus.Running => Loc.T("omp is working"),
        SessionStatus.Waiting => Loc.T("Needs your input"),
        SessionStatus.Unread => Loc.T("New reply"),
        SessionStatus.Error => Loc.T("omp stopped with an error"),
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
        if (d < TimeSpan.FromMinutes(1)) return Loc.T("now");
        if (d < TimeSpan.FromHours(1)) return Loc.F("{0}m", (int)d.TotalMinutes);
        if (d < TimeSpan.FromDays(1)) return Loc.F("{0}h", (int)d.TotalHours);
        if (d < TimeSpan.FromDays(7)) return Loc.F("{0}d", (int)d.TotalDays);
        var local = at.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        return local.Year == now.ToLocalTime().Year
            ? local.ToString(culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM"), culture)
            : local.ToString("d", culture);
    }
}
