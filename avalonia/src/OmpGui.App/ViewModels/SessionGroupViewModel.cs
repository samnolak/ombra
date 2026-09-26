using System.Collections.ObjectModel;

namespace OmpGui.App.ViewModels;

/// <summary>The sidebar's sessions of one project folder (newest first); the open project comes first.</summary>
public sealed class SessionGroupViewModel(string cwd, bool isCurrentProject)
{
    public string Cwd { get; } = cwd;
    public string Name { get; } = SessionItemViewModel.ProjectName(cwd) is { Length: > 0 } n ? n : "(no folder)";
    public bool IsCurrentProject { get; } = isCurrentProject;
    public ObservableCollection<SessionItemViewModel> Items { get; } = [];
}
