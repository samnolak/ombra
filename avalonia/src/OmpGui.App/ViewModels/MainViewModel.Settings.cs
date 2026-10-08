using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;
using OmpGui.Rpc;

namespace OmpGui.App.ViewModels;

public sealed record ApprovalModeOption(string Mode, string Label, string Description);

public enum ModelRowKind { Model, Family, Back }

/// <summary>A row of the model menu: a model, a family of models that opens to its versions, or the way back.</summary>
public sealed partial class ModelItemViewModel(OmpModel model) : ObservableObject
{
    /// <summary>The model; on a family row its newest version, on the back row the open family's first.</summary>
    public OmpModel Model { get; } = model;
    public ModelRowKind Kind { get; init; }
    /// <summary>Family and back rows: the family's name ("Claude Sonnet").</summary>
    public string Family { get; init; } = "";
    public int Versions { get; init; }
    /// <summary>A model listed under the same name as another (omp's aliases: "claude-haiku-4-5" and its dated id):
    /// its id tells them apart, as Claude's desktop app labels such duplicates.</summary>
    public bool ShowId { get; init; }
    public bool IsFamily => Kind == ModelRowKind.Family;
    public bool IsBack => Kind == ModelRowKind.Back;
    public string Name => Kind switch
    {
        ModelRowKind.Family => Family,
        ModelRowKind.Back => "All models",
        _ => Model.Name,
    };
    public string Key => Model.Key;
    public string Provider => Model.Provider;
    public string Detail => Kind switch
    {
        ModelRowKind.Family => $"{Model.Provider} · {Versions} versions · newest {Model.Name}",
        ModelRowKind.Back => $"{Model.Provider} · {Family}",
        _ => string.Join(" · ", new[] { Model.Provider, ShowId ? Model.Id : null,
            Model.ContextWindow > 0 ? $"{Model.ContextWindow / 1000:N0}k context" : null }.Where(s => s is not null)),
    };
    [ObservableProperty] private bool _isCurrent;

    /// <summary>The name without its version words: "Claude Sonnet 4.5" → "Claude Sonnet", "Gemini 2.5 Pro" →
    /// "Gemini Pro". A name with no version, or only a version, is its own family.</summary>
    public static string FamilyOf(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = words.Where(w => !IsVersion(w)).ToArray();
        return kept.Length == 0 || kept.Length == words.Length ? name : string.Join(' ', kept);
    }

    /// <summary>The version words of a name as numbers to sort by, newest first ("4.5" → [4, 5]).</summary>
    public static int[] VersionOf(string name) =>
        name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(IsVersion)
            .SelectMany(w => w.Split('.')).Select(p => int.TryParse(new string(p.TakeWhile(char.IsAsciiDigit).ToArray()), out var n) ? n : 0).ToArray();

    private static bool IsVersion(string word) => char.IsAsciiDigit(word[0]);

    /// <summary>Newest first: by version numbers, then by id descending (a dated id after its alias).</summary>
    public static int CompareNewestFirst(OmpModel a, OmpModel b)
    {
        var (x, y) = (VersionOf(a.Name), VersionOf(b.Name));
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < y.Length ? y[i] : 0).CompareTo(i < x.Length ? x[i] : 0);
            if (c != 0) return c;
        }
        return string.CompareOrdinal(a.Id, b.Id);
    }
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
        new("always-ask", "Ask permissions", "omp reads on its own and asks before it edits a file or runs a command."),
        new("write", "Accept edits", "omp reads and edits files on its own and asks before it runs a command. Recommended."),
        new("yolo", "Bypass permissions", "omp runs every tool without asking, shell commands included. Only for trusted, sandboxed work."),
    ];

    /// <summary>All of omp's thinking levels: the menu when omp cannot say which the model accepts (omp before 18.8.0).</summary>
    public static readonly IReadOnlyList<string> ThinkingLevels = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>The thinking menu: the levels the current model accepts (omp's <c>get_available_thinking_levels</c>).</summary>
    [ObservableProperty] private IReadOnlyList<string> _thinkingOptions = ThinkingLevels;

    /// <summary>The model the thinking menu was fetched for.</summary>
    private string? _thinkingFetchedFor;

    private async Task FetchThinkingLevelsAsync(string model)
    {
        IReadOnlyList<string>? levels;
        try { levels = await Session.GetThinkingLevelsAsync(_cts.Token); }
        catch (OperationCanceledException) { return; }
        if (_open.Last?.Model != model) return; // the model (or the chat) changed meanwhile: that fetch decides
        _thinkingLevelsKnown = levels is not null;
        ThinkingOptions = levels ?? ThinkingLevels;
        if (_open.Last is { } last) ApplyModelInfo(last);
    }

    /// <summary>omp named the levels of the current model (else the menu is the full list and the catalog decides).</summary>
    private bool _thinkingLevelsKnown;

    private readonly ClientSettingsStore? _settings;
    private IReadOnlyList<OmpModel> _allModels = [];
    private string? _modelsFetchedFor;

    private async Task FetchModelsForHeaderAsync()
    {
        try
        {
            _allModels = await Session.GetModelsAsync(_cts.Token);
            if (_open.Last is { } last) ApplyModelInfo(last);
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
    [NotifyPropertyChangedFor(nameof(ApprovalLabel), nameof(IsYolo), nameof(IsAcceptEdits), nameof(ShownApprovalMode))]
    private string? _approvalMode;

    /// <summary>A mode chosen while omp runs: it takes effect (omp restarts) once the run ends.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApprovalLabel), nameof(IsYolo), nameof(IsAcceptEdits), nameof(ApprovalPendingNote), nameof(ShownApprovalMode))]
    private string? _pendingApprovalMode;

    /// <summary>The mode the menu ticks: the one waiting for the run to end, else the current one.</summary>
    public string? ShownApprovalMode => PendingApprovalMode ?? ApprovalMode;
    public string ApprovalLabel => ApprovalModes.FirstOrDefault(m => m.Mode == ShownApprovalMode)?.Label ?? "Default permissions";
    public bool IsYolo => ShownApprovalMode == "yolo";
    public bool IsAcceptEdits => ShownApprovalMode == "write";
    public string? ApprovalPendingNote => PendingApprovalMode is null || !IsRunning ? null : "Takes effect when the current run ends (omp restarts).";

    /// <summary>"Bypass permissions" waits for an explicit second confirmation.</summary>
    [ObservableProperty] private bool _confirmYolo;

    [ObservableProperty] private bool _isSettingsOpen;
    /// <summary>The settings page has too little width for its page list beside the page: the list becomes a strip on top.</summary>
    [ObservableProperty] private bool _isSettingsNarrow;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUnsavedRuntime))] private string _settingsCommand = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUnsavedRuntime))] private string _settingsPrefixArgs = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUnsavedRuntime))] private string _settingsProfile = "";
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
        _ => _open.Last?.StartFailed == true ? "omp didn't start" : "omp stopped",
    };

    public string RecoverHint => StartProblem switch
    {
        StartProblem.NotFound when ShowInstallRuntime => RuntimeOffer,
        StartProblem.NotFound => "omp was not found on this computer. Set where it is in Settings → Advanced, then try again.",
        // omp's own setup opens on its provider step: Sign in with the keys (the terminal takes the keyboard)
        StartProblem.NoModel => "omp needs an AI provider before it can work. Open omp's setup, choose Sign in and a provider (or add an API key), then try again.",
        _ => _open.Last?.StartFailed == true
            ? "Check the omp command in Settings → Advanced, then try again. The error is under Show details."
            : "It can pick this conversation up where it stopped.",
    };

    /// <summary>A new session that cannot start: the setup screen takes the page (no greeting, no message box).</summary>
    public bool ShowSetupScreen => CanRecover && IsConversationEmpty;

    /// <summary>Mid-conversation, the same content is a card above the (disabled) message box.</summary>
    public bool ShowRecoverCard => CanRecover && !IsConversationEmpty;

    public bool ShowGreeting => !CanRecover && IsConversationEmpty;

    /// <summary>Nothing said yet: no rows, or only notices (a warning from omp's start does not end the new-session page).</summary>
    public bool IsConversationEmpty => Rows.All(r => r is NoticeRowViewModel);

    /// <summary>What omp or the system said, for the curious and for bug reports (under "Show details").</summary>
    public string ErrorDetail => CanRecover ? _open.Last?.LastError?.Trim() ?? "" : "";
    public bool HasErrorDetail => ErrorDetail.Length > 0;

    [ObservableProperty] private bool _isErrorDetailOpen;

    public string RecoverLabel => _open.Last?.StartFailed == true ? "Try again" : "Restart omp";

    /// <summary>Getting started (omp not installed, no provider yet) is onboarding, not an error: the app mark, no warning.</summary>
    public bool IsOnboarding => StartProblem is StartProblem.NotFound or StartProblem.NoModel;

    /// <summary>"Try again" next to Install would retry what cannot work yet.</summary>
    public bool ShowRecoverButton => !ShowInstallRuntime;

    public string RecoverSettingsLabel => StartProblem == StartProblem.NotFound ? "Use my own omp…" : "Settings";

    /// <summary>
    /// The notices the new-session page lists above its greeting: the rows while <see cref="ShowGreeting"/> (all notices
    /// then), otherwise none. A collection of its own, not <see cref="Rows"/> itself: a list bound to every row would
    /// build a control for each one of a long conversation while hidden, again on every chat switch.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<RowViewModel> GreetingNotices { get; } = [];

    private void NotifyRecoverLayout()
    {
        OnPropertyChanged(nameof(ShowSetupScreen));
        OnPropertyChanged(nameof(ShowRecoverCard));
        OnPropertyChanged(nameof(ShowGreeting));
        if (ShowGreeting) Reconcile(GreetingNotices, Rows);
        else if (GreetingNotices.Count > 0) GreetingNotices.Clear();
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
        if (_applyingThinking || value is null || value == _open.Last?.ThinkingLevel || Phase != SessionPhase.Ready) return;
        var open = _open;
        _ = open.Controller.SetThinkingLevelAsync(value, _cts.Token).ContinueWith(_ => PostToUi(() => ApplyIfShown(open)), TaskScheduler.Default);
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

    /// <summary>
    /// The message box sends with <c>enter</c> (Shift+Enter adds a line) or <c>mod-enter</c> (⌘/Ctrl+Enter sends,
    /// Enter adds a line). Saved when chosen.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SendWithModifier), nameof(SendKeyHint), nameof(ComposerHint))]
    private string _sendKey = "enter";

    public bool SendWithModifier => SendKey == "mod-enter";

    /// <summary>The key that sends, as the platform writes it.</summary>
    public string SendKeyHint => !SendWithModifier ? "Enter" : OperatingSystem.IsMacOS() ? "⌘Enter" : "Ctrl+Enter";

    /// <summary>The ⌘/Ctrl modifier's label for the send-key choice.</summary>
    public static string ModEnterLabel => OperatingSystem.IsMacOS() ? "⌘ Enter" : "Ctrl+Enter";

    [RelayCommand]
    private void SetSendKey(string? key)
    {
        if (key is not ("enter" or "mod-enter") || key == SendKey) return;
        SendKey = key;
        Persist(o => o with { SendKey = key == "enter" ? null : key });
    }

    private static void PostToUi(Action a) => Avalonia.Threading.Dispatcher.UIThread.Post(a);

    [RelayCommand]
    private async Task OpenModelMenuAsync()
    {
        try
        {
            IsModelMenuOpen = true;
            ModelFilter = "";
            _openModelFamily = null;
            ModelsLoading = true;
            _allModels = await Session.GetModelsAsync(_cts.Token);
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
            if (item is null) return;
            if (item.Kind != ModelRowKind.Model)
            {
                // A family opens to its versions, back returns to the families; the menu stays open
                if (item.IsFamily) OpenModelFamily(item);
                else CloseModelFamily();
                return;
            }
            IsModelMenuOpen = false;
            if (Phase != SessionPhase.Ready) return;
            var open = _open;
            await open.Controller.SetModelAsync(item.Model, _cts.Token);
            ApplyIfShown(open);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    /// <summary>The family whose versions the model menu shows (provider, family); null: the families.</summary>
    private (string Provider, string Family)? _openModelFamily;

    /// <summary>The model menu replaced its rows after a family opened or closed: the row to put the keyboard on.</summary>
    public event Action<int>? ModelRowsNavigated;

    public void OpenModelFamily(ModelItemViewModel family)
    {
        if (!family.IsFamily) return;
        _openModelFamily = (family.Provider, family.Family);
        ApplyModelFilter();
        // On the current version if it is in this family, else on the newest (the row after "All models")
        var current = Models.ToList().FindIndex(m => m.IsCurrent);
        ModelRowsNavigated?.Invoke(current > 0 ? current : Math.Min(1, Models.Count - 1));
    }

    /// <summary>Back to the families, on the one that was open. False when no family is open.</summary>
    public bool CloseModelFamily()
    {
        if (_openModelFamily is not { } open) return false;
        _openModelFamily = null;
        ApplyModelFilter();
        ModelRowsNavigated?.Invoke(Math.Max(0, Models.ToList().FindIndex(m => m.IsFamily && (m.Provider, m.Family) == open)));
        return true;
    }

    /// <summary>A search lists every matching model, flat. Otherwise one row per family ("Claude Sonnet"; a family
    /// of one is just its model) that opens to its versions, newest first.</summary>
    private void ApplyModelFilter()
    {
        var f = ModelFilter.Trim();
        Models.Clear();
        // Two entries under one name (an alias and its dated id) show their ids
        var sharedNames = _allModels.GroupBy(m => (m.Provider, m.Name)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        ModelItemViewModel Row(OmpModel m) => new(m) { IsCurrent = m.Key == _open.Last?.Model, ShowId = sharedNames.Contains((m.Provider, m.Name)) };
        if (f.Length > 0)
        {
            _openModelFamily = null; // a search spans every family
            foreach (var m in _allModels.Where(m => m.Key.Contains(f, StringComparison.OrdinalIgnoreCase) || m.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                Models.Add(Row(m));
            return;
        }
        var families = _allModels.GroupBy(m => (m.Provider, Family: ModelItemViewModel.FamilyOf(m.Name)))
            .Select(g => (g.Key, Versions: g.Order(Comparer<OmpModel>.Create(ModelItemViewModel.CompareNewestFirst)).ToList())).ToList();
        if (_openModelFamily is { } open && families.FirstOrDefault(g => g.Key == open).Versions is { } chosen)
        {
            Models.Add(new ModelItemViewModel(chosen[0]) { Kind = ModelRowKind.Back, Family = open.Family });
            foreach (var m in chosen) Models.Add(Row(m));
            return;
        }
        _openModelFamily = null;
        foreach (var (key, versions) in families)
            Models.Add(versions.Count == 1 ? Row(versions[0]) : new ModelItemViewModel(versions[0])
            {
                Kind = ModelRowKind.Family, Family = key.Family, Versions = versions.Count,
                IsCurrent = versions.Any(m => m.Key == _open.Last?.Model),
            });
    }

    // Concurrent: a choice made while an earlier one waits (for the runs to end, or for Shift+Tab to pause) replaces it
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SetApprovalModeAsync(string mode)
    {
        try
        {
            if (mode == ShownApprovalMode) return;
            if (mode == "yolo" && !ConfirmYolo)
            {
                ConfirmYolo = true; // the card asks once more; nothing changes until the user confirms
                return;
            }
            ConfirmYolo = false;
            Persist(o => o with { ApprovalMode = mode });
            // A launch flag: every open chat's omp restarts with it (new chats start with it). Never mid-run (each one
            // switches once its omp is idle), and not on every step of a Shift+Tab through the modes: the newest choice
            // applies once the keys pause.
            _chosenApprovalMode = mode;
            _approvalChosenAt = DateTime.UtcNow;
            foreach (var open in _opens.Values) open.PendingApprovalMode = mode;
            PendingApprovalMode = mode;
            if (_approvalModeApplying) return; // the loop already waiting picks up the newest choice
            _approvalModeApplying = true;
            try { await ApplyApprovalModeAsync(); }
            finally { _approvalModeApplying = false; }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    /// <summary>
    /// Restarts each open chat's omp with the chosen mode once the keys paused and that omp is idle. One that is working
    /// keeps its run and switches when the run ends: in the shown chat the menu says so, and when chats in the background
    /// are still working a brief note does.
    /// </summary>
    private async Task ApplyApprovalModeAsync()
    {
        var told = false;
        while (_opens.Values.Any(o => o.PendingApprovalMode is not null))
        {
            await Task.Delay(100, _cts.Token);
            if (DateTime.UtcNow - _approvalChosenAt < ApprovalSettle) continue;
            var working = 0;
            foreach (var open in _opens.Values.Where(o => o.PendingApprovalMode is not null).ToList())
            {
                var s = open.Controller.Snapshot();
                if (s.Phase is SessionPhase.NotStarted or SessionPhase.Starting or SessionPhase.Running or SessionPhase.Aborting or SessionPhase.Stopping)
                {
                    if (open != _open) working++;
                    continue;
                }
                var mode = open.PendingApprovalMode!;
                open.PendingApprovalMode = null;
                if (open == _open) PendingApprovalMode = null;
                if (s.ApprovalMode == mode) continue;
                // An omp that is not running (crashed, stopped) starts with it when the user starts it again. With the
                // chat's own lifetime: closed meanwhile, its restart ends instead of starting an omp nobody stops.
                try
                {
                    if (s.Phase is SessionPhase.Faulted or SessionPhase.Stopped) await open.Controller.SetNextApprovalModeAsync(mode, open.Lifetime.Token);
                    else await open.Controller.SetApprovalModeAsync(mode, open.Lifetime.Token);
                }
                catch (OperationCanceledException) when (open.Lifetime.IsCancellationRequested && !_cts.IsCancellationRequested)
                {
                    continue; // that chat closed; the others still switch
                }
                ApplyIfShown(open);
            }
            if (working == 0 || told) continue;
            told = true;
            ShowBrief(ApprovalModeIcons.IconKey(_chosenApprovalMode), ApprovalLabel,
                working == 1 ? "A chat that is working (marked in the sidebar) switches when its run ends: omp restarts then."
                    : $"{working} chats that are working (marked in the sidebar) switch when their runs end: omp restarts then.");
        }
    }

    [RelayCommand]
    private void CancelYolo() => ConfirmYolo = false;

    /// <summary>How long the permission mode waits for another Shift+Tab before omp restarts with it.</summary>
    private static readonly TimeSpan ApprovalSettle = TimeSpan.FromMilliseconds(600);
    private DateTime _approvalChosenAt;
    private bool _approvalModeApplying;

    /// <summary>The mode the user chose in this window (saved too): chats opened from now on start with it.</summary>
    private string? _chosenApprovalMode;

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        try
        {
            var o = LoadSettingsOrDefault();
            // Edits not saved yet stay as they were left (Back or Esc must not throw them away); else what is on disk
            if (!HasUnsavedRuntime) ShowSavedRuntime(o);
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
        var list = await Session.GetLoginProvidersAsync(_cts.Token);
        LoginProviders.Clear();
        foreach (var p in list.OrderByDescending(p => p.Authenticated).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            LoginProviders.Add(new LoginProviderViewModel(p));
        ProvidersLoading = false;
    }

    [RelayCommand]
    private async Task SignInAsync(LoginProviderViewModel? p)
    {
        var open = _open;
        try
        {
            if (p is null || p.IsBusy) return;
            p.IsBusy = true;
            IsSettingsOpen = false; // the sign-in link and any code prompt appear above the composer
            var ok = await open.Controller.LoginAsync(p.Id, _cts.Token);
            p.IsBusy = false;
            p.IsAuthenticated = ok || p.IsAuthenticated;
            ApplyIfShown(open);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    /// <summary>Saves the runtime fields and restarts every chat's omp with them (one that is working once its run ends).</summary>
    [RelayCommand]
    private async Task SaveRuntimeAsync()
    {
        try
        {
            var prefix = SettingsPrefixArgs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!Persist(o => o with
                {
                    Command = SettingsCommand.Trim() is { Length: > 0 } c ? c : null,
                    PrefixArgs = prefix,
                    Profile = SettingsProfile.Trim() is { Length: > 0 } p ? p : null,
                    Theme = SettingsTheme,
                }))
                return;
            ShowSavedRuntime(LoadSettingsOrDefault());
            RefreshRuntimeStatus();
            SettingsMessage = "Saved. Restarting omp…";
            if (!await RestartAllSessionsAsync())
                SettingsMessage = "Saved. omp is replying: it restarts with the new settings when the reply ends.";
            else
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
        var open = _open;
        try
        {
            await open.Controller.ForceStopAsync(_cts.Token);
            if (open == _open) PendingUrl = null;
            ApplyIfShown(open);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
    }

    [RelayCommand(CanExecute = nameof(CanRecover))]
    private async Task RecoverAsync()
    {
        var open = _open;
        try
        {
            await open.Controller.RecoverAsync(_cts.Token);
            ApplyIfShown(open);
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
        SelectedThinking = s.ThinkingLevel is { } t && ThinkingOptions.Contains(t) ? t : null;
        _applyingThinking = false;
        var current = _allModels.FirstOrDefault(m => m.Key == s.Model);
        // omp's own answer wins: a model it knows to reason has more than "off" (omp 18.8.0 knows newer models than
        // its catalog flag says, e.g. claude-opus-5-5); without it, the model list's reasoning flag decides.
        ShowThinking = _thinkingLevelsKnown ? ThinkingOptions.Count > 1 : current?.Reasoning ?? true;
        if (s.Phase == SessionPhase.Ready && s.Model is { } m && m != _thinkingFetchedFor)
        {
            _thinkingFetchedFor = m;
            _ = FetchThinkingLevelsAsync(m);
        }
        // Whether the model reasons is in omp's model list: fetched once omp is ready on a model not in it yet (the
        // picker fetches it too), so the thinking selector is right without opening the picker.
        if (current is null && s.Phase == SessionPhase.Ready && s.Model is { } model && model != _modelsFetchedFor)
        {
            _modelsFetchedFor = model;
            _ = FetchModelsForHeaderAsync();
        }
        // The start-problem page and the recover card follow these alone (the rows and the runtime install notify on
        // their own): every binding on them is told again only when one changed, not on each streamed token.
        var recover = (CanRecover, s.StartProblem, s.StartFailed, s.LastError);
        if (recover == _recoverShown) return;
        _recoverShown = recover;
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

    private (bool, StartProblem, bool, string?)? _recoverShown;

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
