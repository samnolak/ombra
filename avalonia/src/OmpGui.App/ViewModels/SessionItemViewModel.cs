using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>One saved session in the sidebar.</summary>
public sealed partial class SessionItemViewModel(SessionSummary model) : ObservableObject
{
    public SessionSummary Model { get; } = model;
    /// <summary>The header's title for the open session while the saved list still has the old one (a first run, a rename).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Title), nameof(Tooltip))] private string? _liveTitle;
    public string Title => LiveTitle ?? Model.Title;
    public string Project => ProjectName(Model.Cwd);
    public string Cwd => Model.Cwd;
    public string When => Relative(Model.LastModified, DateTimeOffset.Now);
    public string Tooltip => $"{Title}\n{Model.Cwd}\n{Model.LastModified.ToLocalTime():g}";

    [ObservableProperty] private bool _isCurrent;
    /// <summary>omp is working in this session right now (the open session during a run).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowPin))] private bool _isActive;
    /// <summary>Pinned in omp's session list (<c>/pin</c>): listed first, with a pin where the running dot goes.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowPin))] private bool _isPinned;
    public bool ShowPin => IsPinned && !IsActive;

    public static string ProjectName(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return "";
        var trimmed = Path.TrimEndingDirectorySeparator(cwd);
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }

    public static string Relative(DateTimeOffset at, DateTimeOffset now)
    {
        var d = now - at;
        if (d < TimeSpan.FromMinutes(1)) return "now";
        if (d < TimeSpan.FromHours(1)) return string.Create(CultureInfo.InvariantCulture, $"{(int)d.TotalMinutes} min");
        if (d < TimeSpan.FromDays(1)) return string.Create(CultureInfo.InvariantCulture, $"{(int)d.TotalHours} h");
        if (d < TimeSpan.FromDays(7)) return string.Create(CultureInfo.InvariantCulture, $"{(int)d.TotalDays} d");
        return at.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
    }
}
