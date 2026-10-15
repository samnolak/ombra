using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// Chats outside any project (ChatGPT, Claude and Codex open a new chat this way): New chat (⌘N, the sidebar's top
/// button) gives each one an empty folder of its own (<see cref="ChatFolders"/>), and takes over an omp started ahead for
/// it (the host's spare), so the new chat can be used at once instead of after omp's start (about two seconds: omp's own
/// start, and before it the provider check of network privacy, each a Bun process).
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
    /// A new chat outside any project, shown at once. The spare omp takes it when it is ready and was started the way a
    /// new chat would be now (else a new omp starts, as for any new chat); either way the next spare is prepared. The
    /// shown chat is that already while it is a chat nothing was said or typed in.
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
        if (_host.TakeSpare(NewLaunch(null)) is { } spare)
        {
            ShowStartedChat(spare); // MainViewModel.OpenSessions.cs
            ScheduleSpare();
            return;
        }
        string folder;
        try { folder = Chats.Create(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowBrief("IconAlert", Loc.T("Couldn’t start a new chat"), Loc.T("Its folder could not be made: ") + e.Message, Chats.Root);
            return;
        }
        var start = StartNewSessionAsync(NewLaunch(folder)); // MainViewModel.OpenSessions.cs
        ScheduleSpare();
        await start;
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

    // ───────────── The spare omp ─────────────

    /// <summary>How long the window waits before it starts the spare: after the first chat's omp is ready, and after a new
    /// chat took the spare (omp's start is seconds of CPU; it is not wanted while the window itself starts).</summary>
    internal TimeSpan SpareDelay { get; set; } = TimeSpan.FromSeconds(3);

    private DispatcherTimer? _spareTimer;
    private bool _spareScheduled;

    /// <summary>The omp started ahead for the next new chat, if any (the host's).</summary>
    internal SessionController? Spare => _host.Spare;

    /// <summary>The spare is prepared once the window has been idle for <see cref="SpareDelay"/>, unless there is one.</summary>
    private void ScheduleSpare()
    {
        _spareScheduled = true;
        if (IsClosing || _host.Spare is not null || _spareTimer is not null) return;
        _spareTimer = new DispatcherTimer(SpareDelay, DispatcherPriority.ApplicationIdle, (_, _) =>
        {
            _spareTimer?.Stop();
            _spareTimer = null;
            if (IsClosing || _host.Spare is not null) return;
            string folder;
            try { folder = Chats.Create(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return; // New chat says why when it cannot make a folder either
            }
            _ = _host.PrepareSpareAsync(NewLaunch(folder), _cts.Token);
        });
        _spareTimer.Start();
    }

    /// <summary>Something that changes how omp starts changed: the spare goes, and (when <paramref name="again"/>) a new
    /// one is prepared the new way.</summary>
    private void ReplaceSpare(bool again)
    {
        _spareTimer?.Stop();
        _spareTimer = null;
        _host.InvalidateSpare();
        if (again && _spareScheduled) ScheduleSpare();
    }

    /// <summary>A chat's omp stopped (closed, or the spare dropped): a chat's folder nothing was made in goes with it.</summary>
    private void OnChatStopped(SessionController c) => Chats.DeleteIfUnused(c.CurrentLaunch.WorkingDirectory, c.SessionFile);

    /// <summary>An untouched chat's omp went to another folder (a project, a saved session): the chat's own folder goes
    /// when nothing was made in it.</summary>
    private void LeftChatFolder(SessionController c, string? folder, string? sessionFile)
    {
        if (Chats.Contains(folder) && !SessionCatalog.SamePath(folder, c.CurrentLaunch.WorkingDirectory))
            _ = Task.Run(() => Chats.DeleteIfUnused(folder, sessionFile));
    }
}
