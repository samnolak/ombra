using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;

namespace Ombra.App.ViewModels;

/// <summary>The sidebar's sessions of one project folder (pinned first, then newest first), or the chats outside any
/// project (<see cref="IsChatsGroup"/>: every chat has a folder of its own, so the group stands for the folder they are
/// all in). Pinned projects come first, then Chats, then the other projects by their newest message.
/// A group lives as long as its folder is listed: a refresh updates <see cref="Items"/>, <see cref="IsCurrentProject"/>,
/// <see cref="Name"/> and <see cref="IsPinned"/> in place.</summary>
public sealed partial class SessionGroupViewModel(string cwd, bool isCurrentProject, bool isChatsGroup = false, bool isArchiveGroup = false) : ObservableObject
{
    public string Cwd { get; } = cwd;
    public bool IsChatsGroup { get; } = isChatsGroup;
    /// <summary>Archived: the sessions archived from the sidebar, last in the list and folded until opened (or searched).</summary>
    public bool IsArchiveGroup { get; } = isArchiveGroup;
    public bool IsProject => !IsChatsGroup && !IsArchiveGroup;

    /// <summary>The group's rows are shown: a project's (and Chats') header folds and unfolds it, kept in the settings;
    /// Archived starts folded; a search shows every match.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FoldLabel))]
    private bool _isExpanded = !isArchiveGroup;
    /// <summary>"Archived 3".</summary>
    [ObservableProperty] private string _archiveLabel = "";

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>What the header's click does, for assistive tech ("Fold project-x" / "Unfold project-x").</summary>
    public string FoldLabel => IsExpanded ? Loc.F("Fold {0}", Name) : Loc.F("Unfold {0}", Name);

    /// <summary>The folder's own name (what the project is called unless the user renamed it).</summary>
    public string FolderName { get; } = isChatsGroup ? Loc.T("Chats")
        : SessionItemViewModel.ProjectName(cwd) is { Length: > 0 } n ? n : Loc.T("(no folder)");

    /// <summary>What the sidebar calls it: the name the user gave it, else its folder's name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewSessionTooltip), nameof(FoldLabel))]
    private string _name = isChatsGroup ? Loc.T("Chats") : SessionItemViewModel.ProjectName(cwd) is { Length: > 0 } n ? n : Loc.T("(no folder)");

    /// <summary>Pinned to the top of the sidebar (the client's settings).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinLabel))]
    private bool _isPinned;
    public string PinLabel => IsPinned ? Loc.T("Unpin project") : Loc.T("Pin project");
    public static string RevealLabel => OperatingSystem.IsMacOS() ? Loc.T("Show in Finder") : OperatingSystem.IsWindows() ? Loc.T("Show in Explorer") : Loc.T("Open in file manager");

    /// <summary>omp works in this folder now (another folder opened moves the mark, not the group); for Chats, a chat is
    /// shown.</summary>
    [ObservableProperty] private bool _isCurrentProject = isCurrentProject;
    public ObservableCollection<SessionItemViewModel> Items { get; } = [];
    /// <summary>The project's folder as the user reads it (~/…, /var not /private/var); Chats has none.</summary>
    public string? Tooltip => IsProject ? ShownCwd : null;
    public string ShownCwd => GitProbe.ShownPath(Cwd);
    public string NewSessionTooltip => IsChatsGroup ? Loc.T("New chat") : Loc.F("New session in {0}", Name);

    /// <summary>The header shows a name field in place of the name (Rename in its menu).</summary>
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";

    [RelayCommand]
    private void StartRename()
    {
        RenameText = Name;
        IsRenaming = true;
    }

    /// <summary>Enter or leaving the field: the name is kept (empty: the folder's name again).</summary>
    [RelayCommand]
    private void CommitRename()
    {
        if (!IsRenaming) return;
        IsRenaming = false;
        RenameCommand?.Execute((this, RenameText.Trim()));
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    /// <summary>The header menu's commands (MainViewModel's, set when the group is made): the menu's popup cannot reach
    /// the window's DataContext.</summary>
    public System.Windows.Input.ICommand? NewSessionCommand { get; init; }
    public System.Windows.Input.ICommand? RemoveCommand { get; init; }
    public System.Windows.Input.ICommand? CopyPathCommand { get; init; }
    public System.Windows.Input.ICommand? TogglePinCommand { get; init; }
    public System.Windows.Input.ICommand? RevealCommand { get; init; }
    /// <summary>Takes <c>(group, name)</c>.</summary>
    public System.Windows.Input.ICommand? RenameCommand { get; init; }
    /// <summary>Folds or unfolds a project or Chats (MainViewModel's ToggleGroupFold: the fold is kept in the settings).</summary>
    public System.Windows.Input.ICommand? FoldCommand { get; init; }
    /// <summary>Archived's header: every archived session back into its group.</summary>
    public System.Windows.Input.ICommand? UnarchiveAllCommand { get; init; }
}
