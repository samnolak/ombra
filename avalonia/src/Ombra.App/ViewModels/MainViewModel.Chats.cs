using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// Chats outside any project (ChatGPT, Claude and Codex open a new chat this way): New chat (⌘N, the sidebar's top
/// button) gives each one an empty folder of its own (<see cref="ChatFolders"/>) and opens a logical session in the
/// app-owned engine.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Where chats outside a project get their folders (OMBRA_CHATS_DIR, else the app's data folder).</summary>
    public ChatFolders Chats { get; init; } = new(ChatFolders.DefaultRoot, ChatFolders.LegacyDefaultRoot);

    /// <summary>The shown chat belongs to no project: the header, the greeting and the starters say so.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProjectChip), nameof(ProjectChipLabel), nameof(ProjectChipTip), nameof(GreetingStarters), nameof(GreetingLead), nameof(GreetingProject), nameof(GreetingTail))]
    private bool _isProjectless;

    /// <summary>The header's project menu: a project's name, or "No project" for a chat.</summary>
    public bool HasProjectChip => IsProjectless || ProjectName.Length > 0;

    public string ProjectChipLabel => IsProjectless ? Loc.T("No project") : ProjectName.Length > 0 ? ProjectName : Loc.T("Choose a folder");

    /// <summary>A chat's folder is the app's own, of no interest to the reader: the tip says what choosing a project does.</summary>
    public string ProjectChipTip => IsProjectless ? Loc.T("This chat isn’t in a project. Choose one to work on its files.") : ShownProjectPath;

    partial void OnProjectPathChanged(string value) => OnPropertyChanged(nameof(ProjectChipTip));

    partial void OnProjectNameChanged(string value)
    {
        OnPropertyChanged(nameof(HasProjectChip));
        OnPropertyChanged(nameof(ProjectChipLabel));
        OnPropertyChanged(nameof(GreetingLead));
        OnPropertyChanged(nameof(GreetingProject));
        OnPropertyChanged(nameof(GreetingTail));
    }

    /// <summary>
    /// A new session's greeting (JoJo's words) as the words before the project menu, the menu's label and what follows:
    /// "Oh? You’re approaching ‹project›?", and without a project "Oh? You’re approaching ‹me›?" — "me" opens the same
    /// menu. The "me" sentence is split where the translated "me" stands, so a language can bend the words around it.
    /// </summary>
    private (string Lead, string Project, string Tail) Greeting
    {
        get
        {
            if (!IsProjectless && ProjectName.Length > 0) return (Loc.T("Oh? You’re approaching"), ProjectName, "?");
            var sentence = Loc.T("Oh? You’re approaching me?");
            var me = Loc.T("me");
            var at = sentence.LastIndexOf(me, StringComparison.Ordinal);
            return at < 0 ? (sentence, me, "") : (sentence[..at].TrimEnd(), me, sentence[(at + me.Length)..]);
        }
    }

    public string GreetingLead => Greeting.Lead;
    public string GreetingProject => Greeting.Project;
    public string GreetingTail => Greeting.Tail;

    /// <summary>A chat's starters: nothing in them needs a project's files.</summary>
    public IReadOnlyList<string> ChatStarterPrompts { get; } =
    [
        Loc.T("Explain a concept in simple terms"),
        Loc.T("Write a script that automates a task"),
        Loc.T("Help me plan something step by step"),
    ];

    public IReadOnlyList<string> GreetingStarters => IsProjectless ? ChatStarterPrompts : StarterPrompts;

    /// <summary>The shown chat is a chat (no project) that nothing was said or typed in: New chat has nothing to add.</summary>
    private bool IsUntouchedChat(OpenSession open) =>
        Chats.Contains(open.Controller.CurrentLaunch.WorkingDirectory) && IsUntouched(open);

    /// <summary>
    /// A new chat outside any project, shown at once. The shown chat is reused while nothing was said or typed in it.
    /// </summary>
    [RelayCommand]
    private async Task NewChatAsync()
    {
        SidebarShownWhileNarrow = false; // the drawer closes on a choice
        if (IsUntouchedChat(_open))
        {
            FocusComposerRequested?.Invoke();
            return;
        }
        string folder;
        try { folder = Chats.Create(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowUrgentBrief("IconAlert", Loc.T("Couldn’t start a new chat"), Loc.T("Its folder could not be made: ") + e.Message, Chats.Root);
            return;
        }
        await StartNewSessionAsync(NewLaunch(folder)); // MainViewModel.OpenSessions.cs
    }

    /// <summary>A new session where the shown chat works (omp's /new): in its project, or a new chat for a chat.</summary>
    private Task NewSessionHereAsync()
    {
        if (Chats.Contains(Session.CurrentLaunch.WorkingDirectory)) return NewChatAsync();
        SidebarShownWhileNarrow = false;
        if (IsUntouched(_open))
        {
            FocusComposerRequested?.Invoke();
            return Task.CompletedTask;
        }
        return StartNewSessionAsync(NewLaunch(ProjectFolder ?? Session.CurrentLaunch.WorkingDirectory));
    }

    private void PrepareDraftWorkingDirectory(LaunchRequest request)
    {
        var folder = request.WorkingDirectory;
        // Older cleanup could remove an empty chat folder while its composer still lived in the journal.
        // Recreate only app-owned chat folders, never a missing external project. Core reports preparation failures.
        if (Chats.Contains(folder) && !Directory.Exists(folder) && _draftStore.HasDraftInProject(folder))
            Directory.CreateDirectory(folder!);
    }

    /// <summary>A chat's empty folder goes with its process only when no saved input needs that working directory.</summary>
    private void OnChatStopped(SessionController c) => DeleteUnusedChatFolder(c.CurrentLaunch.WorkingDirectory, c.SessionFile);

    private void DeleteUnusedChatFolder(string? folder, string? sessionFile)
    {
        if (!_draftStore.HasDraftInProject(folder)) Chats.DeleteIfUnused(folder, sessionFile);
    }

    /// <summary>An untouched chat's omp went to another folder (a project, a saved session): the chat's own folder goes
    /// when nothing was made in it.</summary>
    private void LeftChatFolder(SessionController c, string? folder, string? sessionFile)
    {
        if (Chats.Contains(folder) && !SessionCatalog.SamePath(folder, c.CurrentLaunch.WorkingDirectory))
            _ = Task.Run(() => DeleteUnusedChatFolder(folder, sessionFile));
    }
}
