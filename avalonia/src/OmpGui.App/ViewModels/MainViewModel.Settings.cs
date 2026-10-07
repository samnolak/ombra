using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;
using OmpGui.Rpc;

namespace OmpGui.App.ViewModels;

public sealed record ApprovalModeOption(string Mode, string Label, string Description);

public sealed partial class ModelItemViewModel(OmpModel model) : ObservableObject
{
    public OmpModel Model { get; } = model;
    public string Name => Model.Name;
    public string Key => Model.Key;
    public string Provider => Model.Provider;
    public string Detail => Model.ContextWindow > 0 ? $"{Model.Provider} · {Model.ContextWindow / 1000:N0}k context" : Model.Provider;
    [ObservableProperty] private bool _isCurrent;
}

public sealed partial class LoginProviderViewModel(OmpLoginProvider provider) : ObservableObject
{
    public OmpLoginProvider Provider { get; } = provider;
    public string Name => Provider.Name;
    public string Id => Provider.Id;
    [ObservableProperty] private bool _isAuthenticated = provider.Authenticated;
    [ObservableProperty] private bool _isBusy;
    public bool CanSignIn => Provider.Available;
}

/// <summary>Model and thinking level, approval mode, settings page, recovery.</summary>
public sealed partial class MainViewModel
{
    public static readonly IReadOnlyList<ApprovalModeOption> ApprovalModes =
    [
        // omp 18.2.0 tiers (tools/approval.ts): each mode auto-approves up to a tier and asks above it.
        // Named as Claude Code names its permission modes (Ask permissions, Accept edits, Bypass permissions)
        new("always-ask", "Ask permissions", "Reading runs; file edits, shell commands and other risky tools ask first."),
        new("write", "Accept edits", "Reading and editing files runs; shell commands and other exec-tier tools ask first. Recommended."),
        new("yolo", "Bypass permissions", "omp runs every tool without asking, including shell commands. Only for trusted, sandboxed work."),
    ];

    public static readonly IReadOnlyList<string> ThinkingLevels = ["off", "minimal", "low", "medium", "high", "xhigh"];

    private readonly ClientSettingsStore? _settings;
    private IReadOnlyList<OmpModel> _allModels = [];
    private string? _modelsFetchedFor;

    private async Task FetchModelsForHeaderAsync()
    {
        try
        {
            _allModels = await _session.GetModelsAsync(_cts.Token);
            if (_last is { } last) ApplyModelInfo(last);
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or IOException or TimeoutException
                                      or OmpGui.Rpc.RpcCommandException or OmpGui.Rpc.RpcConnectionClosedException or OmpGui.Rpc.RpcProtocolException)
        {
            // Not essential: the selector stays as it was, and the picker fetches the list again.
        }
    }
    private bool _applyingThinking;

    public ObservableCollection<ModelItemViewModel> Models { get; } = [];
    public ObservableCollection<LoginProviderViewModel> LoginProviders { get; } = [];

    [ObservableProperty] private string _modelFilter = "";
    [ObservableProperty] private bool _isModelMenuOpen;
    [ObservableProperty] private bool _modelsLoading;
    [ObservableProperty] private string _currentModel = "no model";
    [ObservableProperty] private string? _selectedThinking;
    [ObservableProperty] private bool _showThinking = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApprovalLabel), nameof(IsYolo))]
    private string? _approvalMode;

    public string ApprovalLabel => ApprovalModes.FirstOrDefault(m => m.Mode == ApprovalMode)?.Label ?? "Default permissions";
    public bool IsYolo => ApprovalMode == "yolo";

    /// <summary>"Never ask" waits for an explicit second confirmation.</summary>
    [ObservableProperty] private bool _confirmYolo;

    [ObservableProperty] private bool _isSettingsOpen;
    /// <summary>The settings page has too little width for its page list beside the page: the list becomes a strip on top.</summary>
    [ObservableProperty] private bool _isSettingsNarrow;
    [ObservableProperty] private string _settingsCommand = "";
    [ObservableProperty] private string _settingsPrefixArgs = "";
    [ObservableProperty] private string _settingsProfile = "";
    [ObservableProperty] private string _settingsTheme = "system";
    [ObservableProperty] private string _settingsMessage = "";
    [ObservableProperty] private bool _providersLoading;

    public string SettingsPath => _settings?.Path ?? "(not saved: settings file unavailable)";
    public static IReadOnlyList<string> Themes => ["system", "light", "dark"];

    /// <summary>omp stopped (crash or failed start) and can be started again on the same session.</summary>
    public bool CanRecover => Phase is SessionPhase.Faulted or SessionPhase.Stopped;

    public string RecoverTitle => StartProblem switch
    {
        StartProblem.NotFound => "Install omp to get started",
        StartProblem.NoModel => "Connect a model provider",
        _ => _last?.StartFailed == true ? "omp didn't start" : "omp stopped",
    };

    public string RecoverHint => StartProblem switch
    {
        StartProblem.NotFound when ShowInstallRuntime => RuntimeOffer,
        StartProblem.NotFound => "omp was not found on this computer. Set where it is in Settings → Advanced, then try again.",
        // omp's own setup opens on its provider step: Sign in with the keys (the terminal takes the keyboard)
        StartProblem.NoModel => "omp needs an AI provider before it can work. Open omp's setup, choose Sign in and a provider (or add an API key), then try again.",
        _ => _last?.StartFailed == true
            ? "Check the omp command in Settings → Advanced, then try again. The error is under Show details."
            : "It can pick this conversation up where it stopped.",
    };

    /// <summary>A new session that cannot start: the setup screen takes the page (no greeting, no message box).</summary>
    public bool ShowSetupScreen => CanRecover && Rows.Count == 0;

    /// <summary>Mid-conversation, the same content is a card above the (disabled) message box.</summary>
    public bool ShowRecoverCard => CanRecover && Rows.Count > 0;

    public bool ShowGreeting => !CanRecover && Rows.Count == 0;

    /// <summary>What omp or the system said, for the curious and for bug reports (under "Show details").</summary>
    public string ErrorDetail => CanRecover ? _last?.LastError?.Trim() ?? "" : "";
    public bool HasErrorDetail => ErrorDetail.Length > 0;

    [ObservableProperty] private bool _isErrorDetailOpen;

    public string RecoverLabel => _last?.StartFailed == true ? "Try again" : "Restart omp";

    /// <summary>Getting started (omp not installed, no provider yet) is onboarding, not an error: the app mark, no warning.</summary>
    public bool IsOnboarding => StartProblem is StartProblem.NotFound or StartProblem.NoModel;

    /// <summary>"Try again" next to Install would retry what cannot work yet.</summary>
    public bool ShowRecoverButton => !ShowInstallRuntime;

    public string RecoverSettingsLabel => StartProblem == StartProblem.NotFound ? "Use my own omp…" : "Settings";

    private void NotifyRecoverLayout()
    {
        OnPropertyChanged(nameof(ShowSetupScreen));
        OnPropertyChanged(nameof(ShowRecoverCard));
        OnPropertyChanged(nameof(ShowGreeting));
    }

    partial void OnModelFilterChanged(string value) => ApplyModelFilter();

    /// <summary>The thinking pill's menu (a DS menu instead of a combo box).</summary>
    [RelayCommand]
    private void SetThinking(string? level) => SelectedThinking = level;

    /// <summary>The segmented theme choice on the Settings page.</summary>
    [RelayCommand]
    private void SetTheme(string? theme)
    {
        if (theme is not null && Themes.Contains(theme)) SettingsTheme = theme;
    }

    partial void OnSelectedThinkingChanged(string? value)
    {
        if (_applyingThinking || value is null || value == _last?.ThinkingLevel || Phase != SessionPhase.Ready) return;
        _ = _session.SetThinkingLevelAsync(value, _cts.Token).ContinueWith(_ => PostToUi(() => Apply(_session.Snapshot())), TaskScheduler.Default);
    }

    partial void OnSettingsThemeChanged(string value)
    {
        ApplyTheme(value);
        // Saved when chosen, like notifications: it needs no omp restart ("Save and restart omp" is for the runtime).
        if (IsSettingsOpen) Persist(o => o with { Theme = value });
    }

    partial void OnNotificationsEnabledChanged(bool value)
    {
        if (IsSettingsOpen) Persist(o => o with { Notifications = value });
    }

    private static void PostToUi(Action a) => Avalonia.Threading.Dispatcher.UIThread.Post(a);

    [RelayCommand]
    private async Task OpenModelMenuAsync()
    {
        try
        {
            IsModelMenuOpen = true;
            ModelFilter = "";
            ModelsLoading = true;
            _allModels = await _session.GetModelsAsync(_cts.Token);
            ModelsLoading = false;
            ApplyModelFilter();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    [RelayCommand]
    private async Task ChooseModelAsync(ModelItemViewModel? item)
    {
        try
        {
            IsModelMenuOpen = false;
            if (item is null || Phase != SessionPhase.Ready) return;
            await _session.SetModelAsync(item.Model, _cts.Token);
            Apply(_session.Snapshot());
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    private void ApplyModelFilter()
    {
        var f = ModelFilter.Trim();
        Models.Clear();
        foreach (var m in _allModels.Where(m => f.Length == 0 || m.Key.Contains(f, StringComparison.OrdinalIgnoreCase) || m.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            Models.Add(new ModelItemViewModel(m) { IsCurrent = m.Key == _last?.Model });
    }

    [RelayCommand]
    private async Task SetApprovalModeAsync(string mode)
    {
        try
        {
            if (mode == ApprovalMode) return;
            if (mode == "yolo" && !ConfirmYolo)
            {
                ConfirmYolo = true; // the card asks once more; nothing changes until the user confirms
                return;
            }
            ConfirmYolo = false;
            if (Phase is SessionPhase.Running or SessionPhase.Aborting)
            {
                SettingsMessage = "Stop the current run first: changing approvals restarts omp.";
                return;
            }
            Persist(o => o with { ApprovalMode = mode });
            await _session.SetApprovalModeAsync(mode, _cts.Token);
            Apply(_session.Snapshot());
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    [RelayCommand]
    private void CancelYolo() => ConfirmYolo = false;

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        try
        {
            var o = LoadSettingsOrDefault();
            SettingsCommand = o.Command ?? "";
            SettingsPrefixArgs = string.Join("\n", o.PrefixArgs);
            SettingsProfile = o.Profile ?? "";
            SettingsTheme = o.Theme ?? "system";
            NotificationsEnabled = o.Notifications ?? true;
            SettingsMessage = "";
            RefreshRuntimeStatus();
            IsSettingsOpen = true;
            await LoadLoginProvidersAsync();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    private async Task LoadLoginProvidersAsync()
    {
        ProvidersLoading = true;
        var list = await _session.GetLoginProvidersAsync(_cts.Token);
        LoginProviders.Clear();
        foreach (var p in list.OrderByDescending(p => p.Authenticated).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            LoginProviders.Add(new LoginProviderViewModel(p));
        ProvidersLoading = false;
    }

    [RelayCommand]
    private async Task SignInAsync(LoginProviderViewModel? p)
    {
        try
        {
            if (p is null || p.IsBusy) return;
            p.IsBusy = true;
            IsSettingsOpen = false; // the sign-in link and any code prompt appear above the composer
            var ok = await _session.LoginAsync(p.Id, _cts.Token);
            p.IsBusy = false;
            p.IsAuthenticated = ok || p.IsAuthenticated;
            Apply(_session.Snapshot());
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    /// <summary>Saves the runtime fields and restarts omp with them on the same session.</summary>
    [RelayCommand]
    private async Task SaveRuntimeAsync()
    {
        try
        {
            if (Phase is SessionPhase.Running or SessionPhase.Aborting)
            {
                SettingsMessage = "Stop the current run first: applying these settings restarts omp.";
                return;
            }
            var prefix = SettingsPrefixArgs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!Persist(o => o with
                {
                    Command = SettingsCommand.Trim() is { Length: > 0 } c ? c : null,
                    PrefixArgs = prefix,
                    Profile = SettingsProfile.Trim() is { Length: > 0 } p ? p : null,
                    Theme = SettingsTheme,
                }))
                return;
            RefreshRuntimeStatus();
            SettingsMessage = "Saved. Restarting omp…";
            await _session.RecoverAsync(_cts.Token);
            Apply(_session.Snapshot());
            SettingsMessage = Phase == SessionPhase.Ready ? "Saved. omp restarted with the new settings." : "Saved, but omp did not start: see the conversation for the error.";
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    /// <summary>omp 18.2.0 has no way to cancel a sign-in: restart omp on the same session.</summary>
    [RelayCommand]
    private async Task CancelSignInAsync()
    {
        try
        {
            await _session.ForceStopAsync(_cts.Token);
            PendingUrl = null;
            Apply(_session.Snapshot());
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
    }

    [RelayCommand(CanExecute = nameof(CanRecover))]
    private async Task RecoverAsync()
    {
        try
        {
            await _session.RecoverAsync(_cts.Token);
            Apply(_session.Snapshot());
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    private OmpRuntimeOptions LoadSettingsOrDefault()
    {
        try { return _settings?.Load() ?? new OmpRuntimeOptions(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SettingsMessage = e.Message;
            return new OmpRuntimeOptions();
        }
    }

    /// <summary>Writes one change to the client's settings file; false (with a message) when it cannot.</summary>
    private bool Persist(Func<OmpRuntimeOptions, OmpRuntimeOptions> change)
    {
        if (_settings is null) return true;
        try
        {
            _settings.Update(change);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SettingsMessage = "Settings not saved: " + e.Message;
            return false;
        }
    }

    public static void ApplyTheme(string? theme)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    /// <summary>Header state from a snapshot; called from Apply.</summary>
    private void ApplyModelInfo(SessionSnapshot s)
    {
        CurrentModel = s.Model ?? "no model";
        ApprovalMode = s.ApprovalMode;
        _applyingThinking = true;
        SelectedThinking = s.ThinkingLevel is { } t && ThinkingLevels.Contains(t) ? t : null;
        _applyingThinking = false;
        var current = _allModels.FirstOrDefault(m => m.Key == s.Model);
        ShowThinking = current?.Reasoning ?? true;
        // Whether the model reasons is in omp's model list: fetched once omp is ready on a model not in it yet (the
        // picker fetches it too), so the thinking selector is right without opening the picker.
        if (current is null && s.Phase == SessionPhase.Ready && s.Model is { } model && model != _modelsFetchedFor)
        {
            _modelsFetchedFor = model;
            _ = FetchModelsForHeaderAsync();
        }
        OnPropertyChanged(nameof(CanRecover));
        OnPropertyChanged(nameof(RecoverTitle));
        OnPropertyChanged(nameof(RecoverHint));
        OnPropertyChanged(nameof(ErrorDetail));
        OnPropertyChanged(nameof(HasErrorDetail));
        OnPropertyChanged(nameof(RecoverLabel));
        OnPropertyChanged(nameof(IsOnboarding));
        OnPropertyChanged(nameof(ShowRecoverButton));
        OnPropertyChanged(nameof(RecoverSettingsLabel));
        NotifyRecoverLayout();
        RecoverCommand.NotifyCanExecuteChanged();
        ApplyRuntimeState();
    }

    /// <summary>Remembers the project folder for the next start (only when no folder is configured explicitly).</summary>
    private void RememberProject(string? cwd)
    {
        if (_settings is null || cwd is null || cwd == _rememberedProject) return;
        _rememberedProject = cwd;
        var store = _settings;
        _ = Task.Run(() =>
        {
            try { store.Update(o => o.WorkingDirectory is null ? o with { LastWorkingDirectory = cwd } : o); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                PostToUi(() => SettingsMessage = "Settings not saved: " + e.Message);
            }
        });
    }

    private string? _rememberedProject;
}
