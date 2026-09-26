using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OmpGui.App.ViewModels;

/// <summary>Where a session action is: asking first, running, or done (well or not).</summary>
public enum SessionCardState { Ask, Running, Done, Failed }

/// <summary>A button on the session card: its label and what it does.</summary>
public sealed record SessionCardAction(string Label, IRelayCommand Command);

/// <summary>A workspace folder of the session (<c>/dirs</c>), with a way to take it out when it is not the project.</summary>
public sealed record WorkspaceFolderViewModel(string Path, bool IsWorkingDirectory, IRelayCommand? Remove)
{
    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } n ? n : Path;
    public bool CanRemove => Remove is not null;
}

/// <summary>One choice listed on the card (a message to rewind to), clicked to pick it.</summary>
public sealed record SessionCardChoice(string Label, string Detail, IRelayCommand Command)
{
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>
/// The card above the message box that a session action uses (compact, hand off, share, export, workspace folders,
/// move, memory, a command that runs only in omp's terminal…): what it will do, then what it is doing, then what came
/// of it — in plain words, with the next step as a button. One at a time.
/// </summary>
public sealed partial class SessionCardViewModel : ObservableObject
{
    public SessionCardViewModel(string kind, string iconKey, string title, string message)
    {
        Kind = kind;
        _iconKey = iconKey;
        _title = title;
        _message = message;
    }

    /// <summary>Which action this is ("compact", "share", "terminal", …).</summary>
    public string Kind { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon))]
    private string _iconKey;

    public Geometry? Icon => Application.Current?.TryGetResource(IconKey, null, out var v) == true ? v as Geometry : null;

    [ObservableProperty] private string _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message;

    public bool HasMessage => Message.Length > 0;

    /// <summary>Text to read or copy exactly (a path, a link, omp's memory): monospace, selectable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private string _detail = "";

    public bool HasDetail => Detail.Length > 0;

    /// <summary>Long output (omp's memory, a raw report) scrolls inside the card.</summary>
    [ObservableProperty] private bool _isDetailLong;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsFailed), nameof(IsDone), nameof(ShowInput), nameof(IsAsking))]
    private SessionCardState _state = SessionCardState.Done;

    public bool IsAsking => State == SessionCardState.Ask;
    public bool IsRunning => State == SessionCardState.Running;
    public bool IsFailed => State == SessionCardState.Failed;
    public bool IsDone => State == SessionCardState.Done;

    /// <summary>A free-text field while asking (focus instructions for compact / hand off).</summary>
    public bool HasInput { get; init; }
    public string InputHint { get; init; } = "";
    public bool ShowInput => HasInput && State == SessionCardState.Ask;
    [ObservableProperty] private string _input = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrimary))]
    private SessionCardAction? _primary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSecondary))]
    private SessionCardAction? _secondary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTertiary))]
    private SessionCardAction? _tertiary;

    public bool HasPrimary => Primary is not null;
    public bool HasSecondary => Secondary is not null;
    public bool HasTertiary => Tertiary is not null;

    /// <summary>The session's workspace folders (the "Workspace folders" card only).</summary>
    public ObservableCollection<WorkspaceFolderViewModel> Folders { get; } = [];

    [ObservableProperty] private bool _hasFolders;

    /// <summary>Things to pick from (the "Rewind" card: the conversation's messages, newest first).</summary>
    public ObservableCollection<SessionCardChoice> Choices { get; } = [];

    [ObservableProperty] private bool _hasChoices;

    public void SetChoices(IEnumerable<SessionCardChoice> choices)
    {
        Choices.Clear();
        foreach (var c in choices) Choices.Add(c);
        HasChoices = Choices.Count > 0;
    }

    /// <summary>Closes the card (the × in its corner, Esc); a running action keeps going in omp.</summary>
    public IRelayCommand? DismissCommand { get; set; }

    public void SetFolders(IEnumerable<WorkspaceFolderViewModel> folders)
    {
        Folders.Clear();
        foreach (var f in folders) Folders.Add(f);
        HasFolders = Folders.Count > 0;
    }

    /// <summary>Moves the card to its end state in one step (title, words, buttons).</summary>
    public void Finish(bool ok, string title, string message, string detail = "", SessionCardAction? primary = null, SessionCardAction? secondary = null, SessionCardAction? tertiary = null)
    {
        State = ok ? SessionCardState.Done : SessionCardState.Failed;
        Title = title;
        Message = message;
        Detail = detail;
        Primary = primary;
        Secondary = secondary;
        Tertiary = tertiary;
    }
}
