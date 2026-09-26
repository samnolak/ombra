using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OmpGui.App.ViewModels;

/// <summary>
/// What the Computer use, Git and worktrees and SSH hosts pages share: loading when the page is shown, one line of
/// feedback (success or the real error), and "restart omp to apply" for settings omp reads only when it starts.
/// </summary>
public abstract partial class WorkspacePageViewModel : ObservableObject
{
    private readonly string _pageKey;
    private bool _wasRunning;

    protected WorkspacePageViewModel(MainViewModel main, string pageKey)
    {
        Main = main;
        _pageKey = pageKey;
        _wasRunning = main.CanRunOmpCommands;
        main.SettingsCategoryShown += key =>
        {
            if (key == pageKey) _ = LoadAsync();
        };
        main.PropertyChanged += OnMainChanged;
        // Made after its page was shown (the view models are made on first use): load now.
        if (main.IsSettingsOpen && main.SettingsCategory == pageKey) Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    protected MainViewModel Main { get; }

    [ObservableProperty] private bool _isLoading;

    /// <summary>What just happened (saved, failed and why); empty when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = "";

    [ObservableProperty] private bool _messageIsError;

    public bool HasMessage => Message.Length > 0;

    /// <summary>A setting changed that omp reads only when it starts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestartHint))]
    private bool _needsRestart;

    /// <summary>omp runs and takes commands (its builtins over RPC).</summary>
    public bool IsOmpRunning => Main.CanRunOmpCommands;

    public string RestartHint => IsOmpRunning
        ? "omp reads these settings when it starts. Restart it to use them in this session."
        : "omp reads these settings when it starts: they apply the next time it runs.";

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Phase)) return;
        OnPropertyChanged(nameof(IsOmpRunning));
        OnPropertyChanged(nameof(RestartHint));
        OnPhaseChanged();
        // omp started (or restarted) while the page is shown: what it says replaces what the page showed without it
        var running = Main.CanRunOmpCommands;
        if (running && !_wasRunning && Main.IsSettingsOpen && Main.SettingsCategory == _pageKey) _ = LoadAsync();
        _wasRunning = running;
    }

    /// <summary>omp started, stopped, or a reply began or ended.</summary>
    protected virtual void OnPhaseChanged() { }

    protected void Say(string text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
    }

    /// <summary>A setting omp reads when it starts was saved: say so and offer the restart.</summary>
    protected void SavedForNextStart(string? note = null)
    {
        NeedsRestart = true;
        Say(note ?? "Saved.");
    }

    /// <summary>Reads the page's data again (git, gh, omp's settings and builtins).</summary>
    public abstract Task LoadAsync();

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    [RelayCommand]
    private void DismissMessage() => Message = "";

    [RelayCommand]
    private async Task RestartOmpAsync()
    {
        if (!await Main.RestartOmpToApplyAsync())
        {
            Say("omp is replying. Restart it when the reply ends (or the change applies the next time omp starts).");
            return;
        }
        NeedsRestart = false;
        Say("omp restarted with the new settings.");
        if (Main.CanRunOmpCommands) await LoadAsync(); // else the page reloads when omp reports it is ready
    }
}
