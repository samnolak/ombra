using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.App.ViewModels;

/// <summary>A permission mode of the menu; its words are read in the app's language when shown (the list is static).</summary>
public sealed record ApprovalModeOption(string Mode, string EnglishLabel, string EnglishDescription)
{
    public string Label => Loc.T(EnglishLabel);
    public string Description => Loc.T(EnglishDescription);
}

public enum ModelRowKind { Model, Family, Back, Provider }

/// <summary>A row of the model menu: a model, a family of models that opens to its versions, the way back, or a
/// provider's heading that opens and closes its models (an accordion, while models come from more than one provider).</summary>
public sealed partial class ModelItemViewModel(OmpModel model) : ObservableObject
{
    /// <summary>The model; on a family row its newest version, on the back row the open family's first, on a provider
    /// row its first model (or the current one).</summary>
    public OmpModel Model { get; } = model;
    public ModelRowKind Kind { get; init; }
    /// <summary>Family and back rows: the family's name ("Claude Sonnet").</summary>
    public string Family { get; init; } = "";
    public int Versions { get; init; }
    /// <summary>A provider row: how many models it has; whether they are listed under it.</summary>
    public int Count { get; init; }
    public bool IsExpanded { get; init; }
    /// <summary>Under its provider's heading: the detail leaves the provider out.</summary>
    public bool UnderProvider { get; init; }
    /// <summary>A model listed under the same name as another (omp's aliases: "claude-haiku-4-5" and its dated id):
    /// its id tells them apart, as Claude's desktop app labels such duplicates.</summary>
    public bool ShowId { get; init; }
    public bool IsFamily => Kind == ModelRowKind.Family;
    public bool IsBack => Kind == ModelRowKind.Back;
    public bool IsProvider => Kind == ModelRowKind.Provider;
    public bool IsProviderOpen => IsProvider && IsExpanded;
    public bool IsProviderClosed => IsProvider && !IsExpanded;
    public string Name => Kind switch
    {
        ModelRowKind.Family => Family,
        ModelRowKind.Back => Loc.T("All models"),
        ModelRowKind.Provider => Model.Provider,
        _ => Model.Name,
    };
    public string Key => Model.Key;
    public string Provider => Model.Provider;
    public string Detail => Kind switch
    {
        ModelRowKind.Family => string.Join(" · ", new[] { UnderProvider ? null : Model.Provider, Loc.F("{0} versions", Versions), Loc.F("newest {0}", Model.Name) }.Where(s => s is not null)),
        ModelRowKind.Back => $"{Model.Provider} · {Family}",
        // A closed provider with the current model (IsCurrent) names it: the one to open
        ModelRowKind.Provider => (Count == 1 ? Loc.T("1 model") : Loc.F("{0} models", Count)) + (IsCurrent ? " · " + Loc.F("current {0}", Model.Name) : ""),
        _ => string.Join(" · ", new[] { UnderProvider ? null : Model.Provider, ShowId ? Model.Id : null,
            Model.ContextWindow > 0 ? Loc.F("{0:N0}k context", Model.ContextWindow / 1000) : null }.Where(s => s is not null)),
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

    /// <summary>The sign-out panel under the row: the stored accounts to choose from, or why there are none.</summary>
    [ObservableProperty] private bool _isSignOutOpen;
    [ObservableProperty] private IReadOnlyList<LogoutAccountViewModel> _accounts = [];
    /// <summary>Shown in the panel instead of the accounts (none stored, omp refused…).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignOutNote))]
    private string _signOutNote = "";
    public bool HasSignOutNote => SignOutNote.Length > 0;
}

/// <summary>A credential omp stored for a provider, as a row of the sign-out panel.</summary>
public sealed class LogoutAccountViewModel(LoginProviderViewModel owner, OmpLogoutAccount account)
{
    public LoginProviderViewModel Owner { get; } = owner;
    public OmpLogoutAccount Account { get; } = account;
    public string Label => Account.Label;
    public string Detail => !Account.Active ? Account.Detail : Account.Detail.Length > 0 ? Loc.F("In use · {0}", Account.Detail) : Loc.T("In use");
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
                                      or Ombra.Rpc.RpcCommandException or Ombra.Rpc.RpcConnectionClosedException or Ombra.Rpc.RpcProtocolException)
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
    /// <summary>Why the model menu lists nothing yet (omp is still starting); null otherwise.</summary>
    [ObservableProperty] private string? _modelsNotice;
    [ObservableProperty] private string _currentModel = Loc.T("no model");
    [ObservableProperty] private string? _selectedThinking;
    [ObservableProperty] private bool _showThinking = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApprovalLabel), nameof(IsYolo), nameof(IsAcceptEdits), nameof(ShownApprovalMode))]
    private string? _approvalMode;

    /// <summary>A chosen mode stays pending until its chat has reopened and the new mode is projected into the UI.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApprovalLabel), nameof(IsYolo), nameof(IsAcceptEdits), nameof(ApprovalPendingNote), nameof(ShownApprovalMode))]
    private string? _pendingApprovalMode;

    /// <summary>The mode the menu ticks: the latest choice until its restart is projected, else the current one.</summary>
    public string? ShownApprovalMode => PendingApprovalMode ?? ApprovalMode;
    public string ApprovalLabel => ApprovalModes.FirstOrDefault(m => m.Mode == ShownApprovalMode)?.Label ?? Loc.T("Default permissions");
    public bool IsYolo => ShownApprovalMode == "yolo";
    public bool IsAcceptEdits => ShownApprovalMode == "write";
    public string? ApprovalPendingNote => PendingApprovalMode is null || !IsRunning ? null : Loc.T("Takes effect when the current run ends (the chat session restarts).");

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
    [ObservableProperty] private string _settingsWarning = "";

    private void OnSettingsWarningsChanged() => PostToUi(RefreshSettingsWarning);

    private void RefreshSettingsWarning()
    {
        var warnings = new List<string>();
        if (_settings is { InvalidFields.Count: > 0 })
            warnings.Add(Loc.F("Some settings could not be read; defaults are in use: {0}", string.Join(", ", _settings.InvalidFields)));
        if (_settings is { IsPersistenceUnavailable: true })
            warnings.Add(Loc.T("Settings cannot be saved. Changes are kept for this run only and will be lost when the app closes. Check write permissions for the settings folder, then change a setting to retry."));
        var warning = string.Join("\n", warnings);
        if (warning == SettingsWarning) return;
        SettingsWarning = warning;
        if (warning.Length > 0) Rows.Add(RowViewModel.Create(new NoticeItem(0, NoticeLevel.Warning, warning)));
    }
    [ObservableProperty] private bool _providersLoading;
    /// <summary>What the last sign-out did (under the provider list); empty when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProvidersMessage))]
    private string _providersMessage = "";
    public bool HasProvidersMessage => ProvidersMessage.Length > 0;

    public string SettingsPath => _settings?.Path ?? Loc.T("(not saved: settings file unavailable)");
    public static IReadOnlyList<string> Themes => ["system", "light", "dark"];

    /// <summary>omp stopped (crash or failed start) and can be started again on the same session. Not while another
    /// process has the chat open: its own banner offers "Open here" (MainViewModel.Lease.cs).</summary>
    public bool CanRecover => !IsReadOnly && !IsLeaseReadOnly && Phase is SessionPhase.Faulted or SessionPhase.Stopped;

    public string RecoverTitle => IsAutoRecovering ? Loc.T("omp stopped unexpectedly") : StartProblem switch
    {
        StartProblem.NotFound => Loc.T("Set up engine"),
        StartProblem.NoModel => Loc.T("Connect a model provider"),
        StartProblem.SessionModelUnavailable => Loc.T("Saved model unavailable"),
        _ => _open.Last?.StartFailed == true ? Loc.T("omp didn't start") : Loc.T("omp stopped"),
    };

    public string RecoverHint => IsAutoRecovering ? AutoRecoverText : StartProblem switch
    {
        StartProblem.NotFound when ShowInstallRuntime => RuntimeOffer,
        StartProblem.NotFound => Loc.T("omp was not found on this computer. Set where it is in Settings → Advanced, then try again."),
        // omp's own setup opens on its provider step: Sign in with the keys (the terminal takes the keyboard)
        StartProblem.NoModel => Loc.T("omp needs an AI provider before it can work. Open omp's setup, choose Sign in and a provider (or add an API key), then try again."),
        StartProblem.SessionModelUnavailable => Loc.F("This conversation's saved model “{0}” is unavailable. Reconnect that provider and try again, or explicitly open on the current model. Your history and unsent input are kept.",
            _open.Last?.UnavailableSessionModel ?? Loc.T("unknown")),
        _ => _open.Last?.StartFailed == true
            ? Loc.T("Check the omp command in Settings → Advanced, then try again. The error is under Show details.")
            : Loc.T("It can pick this conversation up where it stopped."),
    };

    /// <summary>A new session that cannot start: the setup screen takes the page (no greeting, no message box).</summary>
    public bool ShowSetupScreen => CanRecover && IsConversationEmpty;

    /// <summary>Mid-conversation, the same content is a card above the (disabled) message box.</summary>
    public bool ShowRecoverCard => CanRecover && !IsConversationEmpty;

    public bool ShowGreeting => !IsReadOnly && !IsLeaseReadOnly && !CanRecover && IsConversationEmpty;

    /// <summary>Nothing said yet: no rows, or only notices (a warning from omp's start does not end the new-session page).</summary>
    public bool IsConversationEmpty => Rows.All(r => r is NoticeRowViewModel);

    /// <summary>What omp or the system said, for the curious and for bug reports (under "Show details").</summary>
    public string ErrorDetail => CanRecover ? _open.Last?.LastError?.Trim() ?? "" : "";
    public bool HasErrorDetail => ErrorDetail.Length > 0;

    [ObservableProperty] private bool _isErrorDetailOpen;

    public string RecoverLabel => IsAutoRecovering ? Loc.T("Restart now")
        : _open.Last?.StartFailed == true ? Loc.T("Try again") : Loc.T("Restart omp");

    /// <summary>Getting started (omp not installed, no provider yet) is onboarding, not an error: the app mark, no warning.</summary>
    public bool IsOnboarding => StartProblem is StartProblem.NotFound or StartProblem.NoModel;

    /// <summary>"Try again" next to Install would retry what cannot work yet.</summary>
    public bool ShowRecoverButton => !ShowInstallRuntime;

    public bool CanOpenOnCurrentModel => CanRecover && StartProblem == StartProblem.SessionModelUnavailable;

    public string RecoverSettingsLabel => StartProblem == StartProblem.NotFound ? Loc.T("Use my own omp…") : Loc.T("Settings");

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

    /// <summary>
    /// At idle the level is set at once. While omp works the controller keeps it for the next message instead
    /// (SessionSnapshot.NextThinkingLevel), so the current run keeps its level and a queued message takes the new one.
    /// </summary>
    partial void OnSelectedThinkingChanged(string? value)
    {
        var shown = _open.Last;
        if (_applyingThinking || value is null || value == (shown?.NextThinkingLevel ?? shown?.ThinkingLevel)
            || Phase is not (SessionPhase.Ready or SessionPhase.Running)) return;
        var open = _open;
        _ = open.Controller.SetThinkingLevelAsync(value, _cts.Token).ContinueWith(_ => PostToUi(() => ApplyIfShown(open)), TaskScheduler.Default);
    }

    // ───────────── Language (Localization/Loc.cs): set at start, so a change shows after a restart ─────────────

    /// <summary>The languages to choose from: English, the built-in ones, the user's own files.</summary>
    [ObservableProperty] private IReadOnlyList<Localization.LanguageInfo> _languages = [];
    [ObservableProperty] private Localization.LanguageInfo? _settingsLanguage;
    /// <summary>Said under the choice once it differs from the language shown.</summary>
    [ObservableProperty] private string _languageNote = "";
    private bool _loadingLanguages;

    private void LoadLanguages(OmpRuntimeOptions o)
    {
        if (_settings is null) return;
        _loadingLanguages = true;
        Languages = Localization.Loc.Available(_settings.Path);
        var code = Localization.Loc.Resolve(o.Language, _settings.Path);
        SettingsLanguage = Languages.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) ?? Languages[0];
        _loadingLanguages = false;
        UpdateLanguageNote();
    }

    partial void OnSettingsLanguageChanged(Localization.LanguageInfo? value)
    {
        if (_loadingLanguages || value is null) return;
        Persist(o => o with { Language = value.Code });
        UpdateLanguageNote();
    }

    private void UpdateLanguageNote() =>
        LanguageNote = SettingsLanguage is { } l && !l.Code.Equals(Localization.Loc.Current, StringComparison.OrdinalIgnoreCase)
            ? Localization.Loc.T("The new language shows after the app is restarted.") : "";

    /// <summary>Opens the folder for one's own languages, with <c>template.json</c> (every text, in English) to start from.</summary>
    [RelayCommand]
    private async Task OpenLanguagesFolderAsync()
    {
        if (_settings is null) return;
        string folder;
        try { folder = Localization.Loc.WriteTemplate(_settings.Path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SettingsMessage = e.Message;
            return;
        }
        await Files.OpenFolderInFileManagerCommand.ExecuteAsync(folder);
        LoadLanguages(LoadSettingsOrDefault());
    }

    partial void OnSettingsThemeChanged(string value)
    {
        ApplyTheme(value);
        // Saved when chosen, like notifications: it needs no omp restart ("Save and restart omp" is for the runtime).
        if (IsSettingsOpen) Persist(o => o with { Theme = value });
    }

    partial void OnNotificationsEnabledChanged(bool value)
    {
        UpdateDockAttention();
        if (IsSettingsOpen) Persist(o => o with { Notifications = value });
    }

    /// <summary>omp's browser tool drives the built-in browser. Process environment changes restart all chats together.</summary>
    [ObservableProperty] private bool _agentUsesGuiBrowser = true;

    partial void OnAgentUsesGuiBrowserChanged(bool value)
    {
        if (IsSettingsOpen && Persist(o => o with { AgentUsesGuiBrowser = value }))
            _ = ApplyBrowserEngineSettingsAsync();
    }

    private async Task ApplyBrowserEngineSettingsAsync()
    {
        try
        {
            SettingsMessage = await RestartAllSessionsAsync()
                ? Loc.T("Saved. omp restarted with the new settings.")
                : Loc.T("Saved. All chats will restart together after their current work finishes.");
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception e) { SettingsMessage = Loc.T("Couldn’t restart omp: ") + e.Message; }
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

    /// <summary>
    /// Settings › General › Agent browser: <c>nightly</c> (macOS default once the user agreed: pages render in the
    /// embedded Nightly runtime) or <c>preview</c> (the built-in WebKit/WebView2 preview). Saved when chosen; open pages
    /// move over. While Nightly cannot run (not installed, failed) the preview shows pages and says why
    /// (<see cref="PreviewViewModel.NightlyFallbackReason"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesNightlyBrowser))]
    private string _agentBrowserEngine = "preview";

    public bool UsesNightlyBrowser => AgentBrowserEngine == "nightly";

    /// <summary>The Nightly choice exists where the runtime can be embedded (macOS).</summary>
    public static bool NightlyBrowserAvailable => OperatingSystem.IsMacOS();

    /// <summary>
    /// The saved choice at start. Never chosen on macOS: the preview asks once (first-run consent card) whether pages
    /// may move to Nightly, and shows them in the built-in preview until answered.
    /// </summary>
    public void LoadAgentBrowserEngine(string? saved)
    {
        Action<bool> chosen = accepted => SetAgentBrowserEngine(accepted ? "nightly" : "preview");
        var ask = saved is null && NightlyBrowserAvailable;
        foreach (var p in new[] { Preview, Browser })
        {
            p.NightlyConsentChosen = chosen;
            p.NeedsNightlyConsent = ask;
        }
        AgentBrowserEngine = saved == "nightly" && NightlyBrowserAvailable ? "nightly" : "preview";
    }

    [RelayCommand]
    private void SetAgentBrowserEngine(string? engine)
    {
        if (engine is not ("preview" or "nightly") || (engine == "nightly" && !NightlyBrowserAvailable)) return;
        Preview.NeedsNightlyConsent = Browser.NeedsNightlyConsent = false;
        // Saved even when unchanged: an explicit choice answers the first-run question.
        Persist(o => o with { AgentBrowserEngine = engine });
        AgentBrowserEngine = engine;
    }

    partial void OnAgentBrowserEngineChanged(string value) =>
        Preview.WantsNightly = Browser.WantsNightly = value == "nightly" && NightlyBrowserAvailable;

    private void PostToUi(Action a)
    {
        if (IsClosing) return;
        UiDispatcher.Post(() =>
        {
            if (!IsClosing) a();
        });
    }

    [RelayCommand]
    private async Task OpenModelMenuAsync()
    {
        if (IsReadOnly) return;
        try
        {
            IsModelMenuOpen = true;
            ModelFilter = "";
            _openModelFamily = null;
            // omp lists the models; before it has started there is no one to ask (the menu fills in once it is ready:
            // OnPhaseChanged, MainViewModel.cs)
            ModelsNotice = Phase is SessionPhase.NotStarted or SessionPhase.Starting ? Loc.T("Models appear here once omp has started.") : null;
            if (ModelsNotice is not null) return;
            ModelsLoading = true;
            try { _allModels = await Session.GetModelsAsync(_cts.Token); }
            finally { ModelsLoading = false; }
            // The current model's provider opens (the others stay as the user left them in this run of the app)
            if (_allModels.FirstOrDefault(m => m.Key == _open.Last?.Model) is { } current) _expandedModelProviders.Add(current.Provider);
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
        if (IsReadOnly) return;
        try
        {
            if (item is null) return;
            if (item.Kind != ModelRowKind.Model)
            {
                // A provider opens or closes, a family opens to its versions, back returns to the families; the menu stays open
                if (item.IsProvider) ToggleModelProvider(item);
                else if (item.IsFamily) OpenModelFamily(item);
                else CloseModelFamily();
                return;
            }
            IsModelMenuOpen = false;
            var open = _open;
            if (open.Controller.Snapshot().Phase != SessionPhase.Ready) return;
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

    /// <summary>The providers whose models the menu lists under their heading (the others show the heading only).</summary>
    private readonly HashSet<string> _expandedModelProviders = new(StringComparer.Ordinal);

    /// <summary>A provider's heading opens or closes its models; the keyboard stays on the heading.</summary>
    public void ToggleModelProvider(ModelItemViewModel provider) => SetModelProviderOpen(provider, !provider.IsExpanded);

    /// <summary>Opens (→) or closes (←) a provider's heading; false when it already was.</summary>
    public bool SetModelProviderOpen(ModelItemViewModel provider, bool open)
    {
        if (!provider.IsProvider || provider.IsExpanded == open) return false;
        if (open) _expandedModelProviders.Add(provider.Provider);
        else _expandedModelProviders.Remove(provider.Provider);
        ApplyModelFilter();
        ModelRowsNavigated?.Invoke(Math.Max(0, Models.ToList().FindIndex(m => m.IsProvider && m.Provider == provider.Provider)));
        return true;
    }

    /// <summary>← on a model or family under a heading: the keyboard goes to the heading. False when there is none.</summary>
    public bool FocusModelProviderOf(ModelItemViewModel row)
    {
        if (row.IsProvider || _openModelFamily is not null || !row.UnderProvider) return false;
        var at = Models.ToList().FindIndex(m => m.IsProvider && m.Provider == row.Provider);
        if (at < 0) return false;
        ModelRowsNavigated?.Invoke(at);
        return true;
    }

    /// <summary>A search lists every matching model, flat. Otherwise one row per family ("Claude Sonnet"; a family
    /// of one is just its model) that opens to its versions, newest first; with more than one provider, the families
    /// sit under their provider's heading, which opens and closes them.</summary>
    private void ApplyModelFilter()
    {
        var f = ModelFilter.Trim();
        Models.Clear();
        // Two entries under one name (an alias and its dated id) show their ids
        var sharedNames = _allModels.GroupBy(m => (m.Provider, m.Name)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        ModelItemViewModel Row(OmpModel m, bool under = false) =>
            new(m) { IsCurrent = m.Key == _open.Last?.Model, ShowId = sharedNames.Contains((m.Provider, m.Name)), UnderProvider = under };
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
        var providers = families.GroupBy(g => g.Key.Provider).ToList(); // in omp's order
        var headings = providers.Count > 1;
        foreach (var provider in providers)
        {
            var expanded = !headings || _expandedModelProviders.Contains(provider.Key);
            if (headings)
            {
                var current = provider.SelectMany(g => g.Versions).FirstOrDefault(m => m.Key == _open.Last?.Model);
                Models.Add(new ModelItemViewModel(current ?? provider.First().Versions[0])
                {
                    Kind = ModelRowKind.Provider, Count = provider.Sum(g => g.Versions.Count), IsExpanded = expanded,
                    IsCurrent = current is not null && !expanded, // closed over the current model: marked, the one to open
                });
            }
            if (!expanded) continue;
            foreach (var (key, versions) in provider)
                Models.Add(versions.Count == 1 ? Row(versions[0], headings) : new ModelItemViewModel(versions[0])
                {
                    Kind = ModelRowKind.Family, Family = key.Family, Versions = versions.Count, UnderProvider = headings,
                    IsCurrent = versions.Any(m => m.Key == _open.Last?.Model),
                });
        }
    }

    // Concurrent: a choice made while an earlier one waits (for the runs to end, or for Shift+Tab to pause) replaces it
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SetApprovalModeAsync(string mode)
    {
        if (IsReadOnly) return;
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
            foreach (var open in _opens.Values)
                if (!open.Controller.CurrentLaunch.ReadOnly) open.PendingApprovalMode = mode;
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
                if (s.ApprovalMode != mode)
                {
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
                }
                // Keep the explicit choice visible throughout the restart. Core's launch request changes before the
                // UI sees its new phase/mode; clearing early let the next Shift+Tab cycle from the previous mode.
                ApplyIfShown(open);
                if (open.PendingApprovalMode != mode) continue; // a newer choice arrived while this restart awaited
                open.PendingApprovalMode = null;
                if (open == _open) PendingApprovalMode = null;
            }
            if (working == 0 || told) continue;
            told = true;
            ShowBrief(ApprovalModeIcons.IconKey(_chosenApprovalMode), ApprovalLabel,
                working == 1 ? Loc.T("A chat that is working (marked in the sidebar) switches when its run ends: omp restarts then.")
                    : Loc.F("{0} chats that are working (marked in the sidebar) switch when their runs end: omp restarts then.", working));
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
            LoadLanguages(o);
            NotificationsEnabled = o.Notifications ?? true;
            WatchGitRemotes = o.WatchGitRemotes ?? true;
            AgentUsesGuiBrowser = o.AgentUsesGuiBrowser ?? true;
            SettingsMessage = "";
            ProvidersMessage = "";
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
            bool ok;
            // Strict network privacy lets this provider's sign-in and API addresses through while the user signs in
            using (Privacy?.AllowSignIn(p.Id))
                ok = await open.Controller.LoginAsync(p.Id, _cts.Token);
            p.IsBusy = false;
            p.IsAuthenticated = ok || p.IsAuthenticated;
            if (ok) _providerUsage?.MarkStale();
            ApplyIfShown(open);
            // omp contacts a provider only once it is added (NetworkPrivacy): its models come with the next start
            if (ok && Privacy?.LastGate?.Added.Contains(p.Id) != true) _ = RestartOmpToApplyAsync();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    /// <summary>Opens (or closes) the sign-out panel under a provider: the accounts omp stored for it.</summary>
    [RelayCommand]
    private async Task AskSignOutAsync(LoginProviderViewModel? p)
    {
        if (p is null || p.IsBusy) return;
        if (p.IsSignOutOpen) { p.IsSignOutOpen = false; return; }
        p.IsBusy = true;
        ProvidersMessage = "";
        try
        {
            var accounts = await Session.GetLogoutAccountsAsync(p.Id, _cts.Token);
            p.Accounts = [.. accounts.Select(a => new LogoutAccountViewModel(p, a))];
            p.SignOutNote = accounts.Count > 0 ? ""
                : Loc.F("omp has no stored sign-in for {0}: it comes from an API key in the environment or omp's settings. Remove that key to sign out.", p.Name);
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException or OmpNotRunningException)
        {
            p.Accounts = [];
            p.SignOutNote = SignOutFailure(e);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
        finally { p.IsBusy = false; }
        p.IsSignOutOpen = true;
    }

    [RelayCommand]
    private void CancelSignOut(LoginProviderViewModel? p)
    {
        if (p is not null) p.IsSignOutOpen = false;
    }

    /// <summary>Removes one stored account (omp's <c>logout</c>), then lists the providers again.</summary>
    [RelayCommand]
    private async Task SignOutAsync(LogoutAccountViewModel? a)
    {
        if (a is null || a.Owner.IsBusy) return;
        var p = a.Owner;
        var open = _open;
        p.IsBusy = true;
        try
        {
            var remaining = await open.Controller.LogoutAsync(p.Id, a.Account, _cts.Token);
            ApplyIfShown(open);
            await LoadLoginProvidersAsync();
            ProvidersMessage = remaining is null
                ? Loc.F("Signed out of {0} ({1}).", p.Name, a.Label)
                : Loc.F("Signed out of {0} ({1}). omp still signs in to it with {2}.", p.Name, a.Label, remaining);
            _ = ProviderUsage.LoadAsync(force: true);
            // Without any sign-in left the provider is no longer added: omp stops contacting it from its next start
            if (remaining is null) _ = RestartOmpToApplyAsync();
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException or OmpNotRunningException)
        {
            p.IsBusy = false;
            p.SignOutNote = SignOutFailure(e);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    /// <summary>An omp without sign-out over RPC (18.2.0 had none): its terminal's <c>/logout</c> still works.</summary>
    private static string SignOutFailure(Exception e) =>
        e is RpcCommandException { Error: var error } && error.StartsWith("Unknown command", StringComparison.Ordinal)
            ? Loc.T("This omp can't sign out from here. Use /logout in omp's terminal.")
            : Loc.F("Could not sign out: {0}", e is RpcCommandException r ? r.Error : e.Message);

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
            SettingsMessage = Loc.T("Saved. Restarting omp…");
            if (!await RestartAllSessionsAsync())
                SettingsMessage = Loc.T("Saved. All chats will restart together after their current work finishes.");
            else
                SettingsMessage = Phase == SessionPhase.Ready ? Loc.T("Saved. omp restarted with the new settings.") : Loc.T("Saved, but omp did not start: see the conversation for the error.");
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

    [RelayCommand(CanExecute = nameof(CanOpenOnCurrentModel))]
    private async Task OpenOnCurrentModelAsync()
    {
        var open = _open;
        try
        {
            await open.Controller.OpenOnCurrentModelAsync(open.Lifetime.Token);
            ApplyIfShown(open);
        }
        catch (OperationCanceledException) when (open.Lifetime.IsCancellationRequested) { }
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

    /// <summary>The variables Settings → Advanced sets for omp (a null value removes one).</summary>
    internal IReadOnlyDictionary<string, string?> RuntimeEnvironment() => LoadSettingsOrDefault().Environment;

    private CustomProvidersViewModel? _customProviders;

    /// <summary>Settings → Model providers → Local and custom (omp's models.yml).</summary>
    public CustomProvidersViewModel CustomProviders => _customProviders ??= new CustomProvidersViewModel(this);

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
            SettingsMessage = Loc.F("Settings not saved: {0}", e.Message);
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
        CurrentModel = s.Model ?? Loc.T("no model");
        ApprovalMode = s.ApprovalMode;
        _applyingThinking = true;
        SelectedThinking = (s.NextThinkingLevel ?? s.ThinkingLevel) is { } t && ThinkingOptions.Contains(t) ? t : null;
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
        var recover = (CanRecover, s.StartProblem, s.StartFailed, s.LastError, s.AutoRecover);
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
        OnPropertyChanged(nameof(CanOpenOnCurrentModel));
        OpenOnCurrentModelCommand.NotifyCanExecuteChanged();
        ApplyRuntimeState();
        ApplyAutoRecover(); // MainViewModel.AutoRecover.cs
    }

    private (bool, StartProblem, bool, string?, AutoRecoverStatus?)? _recoverShown;

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
                PostToUi(() => SettingsMessage = Loc.F("Settings not saved: {0}", e.Message));
            }
        });
    }

    private string? _rememberedProject;
}
