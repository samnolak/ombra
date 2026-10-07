using System.Collections.ObjectModel;

namespace OmpGui.App.ViewModels;

/// <summary>The sidebar's sessions of one project folder (newest first); groups are ordered by their newest message.</summary>
public sealed class SessionGroupViewModel(string cwd, bool isCurrentProject)
{
    public string Cwd { get; } = cwd;
    public string Name { get; } = SessionItemViewModel.ProjectName(cwd) is { Length: > 0 } n ? n : "(no folder)";
    public bool IsCurrentProject { get; } = isCurrentProject;
    public ObservableCollection<SessionItemViewModel> Items { get; } = [];
    /// <summary>The header menu's commands (MainViewModel's, set when the group is made): the menu's popup cannot reach
    /// the window's DataContext.</summary>
    public System.Windows.Input.ICommand? NewSessionCommand { get; init; }
    public System.Windows.Input.ICommand? RemoveCommand { get; init; }
}
