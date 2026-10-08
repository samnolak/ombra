using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OmpGui.App.ViewModels;

/// <summary>The sidebar's sessions of one project folder (newest first); groups are ordered by their newest message.
/// The pinned sessions of every project form a group of their own, first. A group lives as long as its folder is
/// listed: a refresh updates <see cref="Items"/> and <see cref="IsCurrentProject"/> in place.</summary>
public sealed partial class SessionGroupViewModel(string cwd, bool isCurrentProject, bool isPinnedGroup = false) : ObservableObject
{
    public string Cwd { get; } = cwd;
    public bool IsPinnedGroup { get; } = isPinnedGroup;
    public bool IsProject => !IsPinnedGroup;
    public string Name { get; } = isPinnedGroup ? "Pinned"
        : SessionItemViewModel.ProjectName(cwd) is { Length: > 0 } n ? n : "(no folder)";
    /// <summary>omp works in this folder now (another folder opened moves the mark, not the group).</summary>
    [ObservableProperty] private bool _isCurrentProject = isCurrentProject;
    public ObservableCollection<SessionItemViewModel> Items { get; } = [];
    /// <summary>The project's folder as the user reads it (~/…, /var not /private/var); the pinned group has none.</summary>
    public string? Tooltip => IsPinnedGroup ? null : ShownCwd;
    public string ShownCwd => GitProbe.ShownPath(Cwd);
    public string NewSessionTooltip => $"New session in {Name}";
    /// <summary>The header menu's commands (MainViewModel's, set when the group is made): the menu's popup cannot reach
    /// the window's DataContext.</summary>
    public System.Windows.Input.ICommand? NewSessionCommand { get; init; }
    public System.Windows.Input.ICommand? RemoveCommand { get; init; }
    public System.Windows.Input.ICommand? CopyPathCommand { get; init; }
}
