using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// What a settings section says after an action, next to the controls that did it: what was done, what went wrong
/// (omp's own words) and, when omp applies the change only when it starts, the restart offer.
/// </summary>
public sealed partial class PageFeedbackViewModel(Func<Task> restart) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice), nameof(IsVisible))]
    private string _notice = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsVisible))]
    private string _error = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    private bool _showRestart;

    [ObservableProperty] private string _restartText = "";
    [ObservableProperty] private bool _isRestarting;

    public bool HasNotice => Notice.Length > 0;
    public bool HasError => Error.Length > 0;
    public bool IsVisible => HasNotice || HasError || ShowRestart;

    [RelayCommand] private Task Restart() => restart();
}

/// <summary>What a connector row says about the server (a status badge).</summary>
public enum ConnectorState { Off, Connected, NotConnected, Pending, Testing, Failed, Replaced }

/// <summary>One MCP server of the Connectors page: omp's <c>/mcp list</c> line plus its entry in the config file.</summary>
public sealed partial class McpServerRowViewModel : ObservableObject
{
    private readonly ConnectorsViewModel _page;
    private bool _syncing;

    internal McpServerRowViewModel(ConnectorsViewModel page, McpServerEntry entry)
    {
        _page = page;
        Entry = entry;
        SetEnabledQuietly(entry.Enabled);
    }

    internal McpServerEntry Entry { get; set; }

    /// <summary>The badge shows a test result (kept until omp starts again).</summary>
    internal bool Tested { get; set; }

    /// <summary>How many of this session's tools are the server's (<c>/tools</c>): omp connected to it when it started.</summary>
    internal int SessionTools { get; set; }

    /// <summary>The server's entry in its mcp.json (null when the file could not be read).</summary>
    internal JsonObject? Config { get; set; }

    /// <summary>For a server of the project folder's own mcp.json / .mcp.json: that file's name.</summary>
    internal string? SourceFile { get; set; }

    public string Name => Entry.Name;
    public string Scope => Entry.Scope;

    /// <summary>A server of the project folder's mcp.json / .mcp.json: omp loads it, but its /mcp add, remove and test
    /// work on omp's own files only; the switch works (omp's disabledServers).</summary>
    public bool IsFromProjectFile => Entry.Scope == "file";

    // The same words as the SSH hosts page and the form's choice: "All projects" / "This project"
    public string ScopeLabel => Entry.Scope switch { "project" => "This project", "file" => SourceFile ?? "Project file", _ => "All projects" };

    public string ScopeTip => Entry.Scope switch
    {
        "project" => "In this project's .omp/mcp.json",
        "file" => $"In the project folder's {SourceFile}, a file other tools (Claude Code…) read too",
        _ => "In your omp settings: all projects",
    };

    public string FileNote => $"From the project's {SourceFile}: change it in that file.";
    public string Transport => Entry.Transport;
    public string TransportLabel => Entry.Transport switch { "http" => "HTTP", "sse" => "SSE", _ => "stdio" };
    public bool IsRemote => Entry.Transport is "http" or "sse";

    /// <summary>Command and arguments (stdio) or the address (HTTP/SSE, without its query, as omp lists it).</summary>
    public string Detail
    {
        get
        {
            if (IsRemote) return Entry.Location;
            var command = McpConfigFile.GetString(Config, "command") ?? Entry.Location;
            var args = McpConfigFile.GetArgs(Config);
            return args.Count == 0 ? command : command + " " + string.Join(' ', args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));
        }
    }

    /// <summary>Names only (values can be secrets): "Environment: API_KEY, REGION".</summary>
    public string Extras
    {
        get
        {
            var names = McpConfigFile.GetMap(Config, IsRemote ? "headers" : "env").Select(kv => kv.Key).ToList();
            return names.Count == 0 ? "" : (IsRemote ? "Headers: " : "Environment: ") + string.Join(", ", names);
        }
    }

    public bool HasExtras => Extras.Length > 0;
    public bool UsesOAuth => McpConfigFile.UsesOAuth(Config);
    public bool CanEdit => Config is not null && !IsFromProjectFile;
    public bool HasMenu => !IsFromProjectFile;

    [ObservableProperty] private bool _isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsFailed), nameof(IsWorking), nameof(CanTest))]
    private ConnectorState _state;

    [ObservableProperty] private string _stateText = "";

    /// <summary>omp's reason when the test failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure), nameof(SuggestSignIn))]
    private string _failure = "";

    /// <summary>The tools the last test listed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTools))]
    private string _tools = "";

    [ObservableProperty] private bool _isConfirmingRemove;
    [ObservableProperty] private bool _isSignInOpen;

    public bool IsConnected => State == ConnectorState.Connected;
    public bool IsFailed => State == ConnectorState.Failed;
    public bool IsWorking => State is ConnectorState.Testing or ConnectorState.Pending;
    public bool HasFailure => Failure.Length > 0;
    public bool HasTools => Tools.Length > 0;
    public bool CanTest => IsEnabled && State != ConnectorState.Testing && !IsFromProjectFile;
    public bool CanToggle => !IsBusy;

    /// <summary>A remote server that needs a sign-in (OAuth configured, or the test was refused).</summary>
    public bool SuggestSignIn => IsRemote && (UsesOAuth || Failure.Contains("401", StringComparison.Ordinal)
                                              || Failure.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
                                              || Failure.Contains("OAuth", StringComparison.OrdinalIgnoreCase));

    public string ReauthCommand => "/mcp reauth " + OmpMcp.Quote(Name);
    public string RemoveQuestion => $"Remove “{Name}” from {(Scope == "project" ? "this project's" : "your")} connectors?";

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(CanTest));
        if (!_syncing) _ = _page.SetEnabledAsync(this, value);
    }

    internal void SetEnabledQuietly(bool value)
    {
        _syncing = true;
        IsEnabled = value;
        _syncing = false;
    }

    internal void Notify()
    {
        foreach (var p in new[] { nameof(Detail), nameof(Extras), nameof(HasExtras), nameof(UsesOAuth), nameof(CanEdit), nameof(SuggestSignIn),
                     nameof(Transport), nameof(TransportLabel), nameof(IsRemote), nameof(Name), nameof(Scope), nameof(ScopeLabel), nameof(ScopeTip),
                     nameof(IsFromProjectFile), nameof(HasMenu), nameof(FileNote), nameof(CanTest) })
            OnPropertyChanged(p);
    }

    [RelayCommand] private Task Test() => _page.TestAsync(this);
    [RelayCommand] private void Edit() => _page.OpenEditForm(this);

    [RelayCommand]
    private void AskRemove()
    {
        IsSignInOpen = false;
        IsConfirmingRemove = true;
    }

    [RelayCommand] private void CancelRemove() => IsConfirmingRemove = false;
    [RelayCommand] private Task Remove() => _page.RemoveAsync(this);

    [RelayCommand]
    private void ShowSignIn()
    {
        IsConfirmingRemove = false;
        IsSignInOpen = !IsSignInOpen;
    }

    [RelayCommand] private void CopyReauth() => _page.Copy(ReauthCommand);
    [RelayCommand] private void OpenTerminal() => _page.OpenOmpTerminal();
}

/// <summary>A resource or prompt a connected server offers.</summary>
public sealed partial class McpItemViewModel(ConnectorsViewModel page, McpItem item, bool canUse) : ObservableObject
{
    public string Server => item.Server;
    public string Name => item.Name;
    public string Description => item.Description ?? "";
    public bool HasDescription => Description.Length > 0;

    /// <summary>omp offers each MCP prompt as the command /server:prompt.</summary>
    public bool CanUse => canUse;

    public string Command => $"/{item.Server}:{item.Name}";
    public string Label => $"{item.Server} / {item.Name}";

    [RelayCommand] private void Use() => page.UseInComposer(Command + " ");
}

/// <summary>A Smithery registry result.</summary>
public sealed partial class SmitheryResultViewModel(ConnectorsViewModel page, SmitheryResult result) : ObservableObject
{
    public string DisplayName => result.DisplayName;
    public string QualifiedName => result.QualifiedName;
    public string Description => result.Description ?? "";
    public bool HasDescription => Description.Length > 0;

    [ObservableProperty] private bool _isAddOpen;

    [RelayCommand] private void ToggleAdd() => IsAddOpen = !IsAddOpen;
    [RelayCommand] private void CopySearch() => page.Copy(page.SmitheryCommand);
    [RelayCommand] private void OpenTerminal() => page.OpenOmpTerminal();
}

/// <summary>
/// Settings → Connectors: omp's MCP servers, managed through omp's own <c>/mcp</c> builtin over RPC (list, add, enable,
/// disable, remove, test, resources, prompts, Smithery search) and <c>omp config</c> for the MCP option. omp reads
/// mcp.json only when it starts, so every change says so and offers the restart.
/// </summary>
public sealed partial class ConnectorsViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private string? _userConfigPath;
    private int _loadVersion;
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private McpServerRowViewModel? _editing;
    private SessionPhase _lastPhase;
    private bool _syncingOptions;

    public ConnectorsViewModel(MainViewModel owner)
    {
        _owner = owner;
        _lastPhase = owner.Phase;
        Feedback = new PageFeedbackViewModel(RestartOmpAsync);
        OptionsFeedback = new PageFeedbackViewModel(RestartOmpAsync);
        owner.SettingsCategoryShown += key =>
        {
            if (key != "connectors") return;
            ClearFeedback();
            _ = RefreshAsync();
        };
        owner.PropertyChanged += OnOwnerChanged;
        // Created while the page is already shown (the page asked for it late): load now.
        if (owner.IsSettingsOpen && owner.SettingsCategory == "connectors") _ = RefreshAsync();
    }

    public ObservableCollection<McpServerRowViewModel> Servers { get; } = [];
    public ObservableCollection<McpItemViewModel> Resources { get; } = [];
    public ObservableCollection<McpItemViewModel> Prompts { get; } = [];
    public ObservableCollection<SmitheryResultViewModel> SearchResults { get; } = [];

    /// <summary>Feedback for the list, its rows and the form.</summary>
    public PageFeedbackViewModel Feedback { get; }

    /// <summary>Feedback for the options at the end of the page.</summary>
    public PageFeedbackViewModel OptionsFeedback { get; }

    // ── Page state ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotRunning), nameof(ShowEmpty), nameof(ShowList), nameof(CanChange))]
    private bool _isOmpRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowList), nameof(ShowLoading))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotRunning), nameof(ShowEmpty), nameof(ShowLoading))]
    private bool _hasLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError), nameof(ShowEmpty), nameof(ShowList))]
    private string _loadError = "";

    /// <summary>A change omp applies only when it starts again (the section that made it offers the restart).</summary>
    [ObservableProperty] private bool _needsRestart;

    /// <summary>MCP tools of this session that no listed server claims (servers configured in other tools' files).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherTools))]
    private string _otherTools = "";

    public bool ShowNotRunning => HasLoaded && !IsOmpRunning;
    public bool ShowLoading => IsLoading && !HasLoaded;
    public bool ShowEmpty => HasLoaded && IsOmpRunning && !IsLoading && LoadError.Length == 0 && Servers.Count == 0;
    public bool ShowList => IsOmpRunning && LoadError.Length == 0 && Servers.Count > 0;
    public bool HasLoadError => LoadError.Length > 0;
    public bool HasOtherTools => OtherTools.Length > 0;
    public bool CanChange => IsOmpRunning;
    public bool CanStartOmp => _owner.CanRecover;

    // ── Add / edit form ──
    [ObservableProperty] private bool _isFormOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormTitle), nameof(FormSubmitLabel), nameof(CanEditName))]
    private bool _isEditing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormTitle))]
    private string _formName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormIsStdio), nameof(FormIsRemote), nameof(FormIsHttp), nameof(FormIsSse))]
    private string _formTransport = "stdio";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormIsUser), nameof(FormIsProject), nameof(FormScopeLabel))]
    private string _formScope = "user";

    [ObservableProperty] private string _formCommand = "";
    [ObservableProperty] private string _formArgs = "";
    [ObservableProperty] private string _formEnv = "";
    [ObservableProperty] private string _formUrl = "";
    [ObservableProperty] private string _formToken = "";
    [ObservableProperty] private string _formHeaders = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormError))]
    private string _formError = "";

    [ObservableProperty] private bool _isSubmitting;

    public string FormTitle => IsEditing ? $"Edit “{FormName}”" : "Add a connector";
    public string FormSubmitLabel => IsEditing ? "Save" : "Add connector";
    public bool CanEditName => !IsEditing;
    public bool FormIsStdio => FormTransport == "stdio";
    public bool FormIsRemote => !FormIsStdio;
    public bool FormIsHttp => FormTransport == "http";
    public bool FormIsSse => FormTransport == "sse";
    public bool FormIsUser => FormScope == "user";
    public bool FormIsProject => FormScope == "project";
    public string FormScopeLabel => FormScope == "project" ? "This project only (.omp/mcp.json)" : "All projects (your omp settings)";
    public bool HasFormError => FormError.Length > 0;

    // ── Resources and prompts ──
    [ObservableProperty] private bool _isLoadingItems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItemsMessage))]
    private string _itemsMessage = "";

    public bool HasItemsMessage => ItemsMessage.Length > 0;
    public bool HasResources => Resources.Count > 0;
    public bool HasPrompts => Prompts.Count > 0;

    // ── Smithery ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SmitheryCommand))]
    private string _searchQuery = "";

    [ObservableProperty] private bool _isSearching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchMessage))]
    private string _searchMessage = "";

    [ObservableProperty] private bool _searchNeedsSignIn;

    public bool HasSearchMessage => SearchMessage.Length > 0;
    public bool HasSearchResults => SearchResults.Count > 0;
    public string SmitheryCommand => "/mcp smithery-search " + (SearchQuery.Trim().Length > 0 ? SearchQuery.Trim() : "<keyword>");
    public string SmitheryLoginCommand => "/mcp smithery-login";

    // ── Option (omp config) ──
    [ObservableProperty] private bool _projectServers = true;
    [ObservableProperty] private bool _hasOptions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOptionsError))]
    private string _optionsError = "";

    public bool HasOptionsError => OptionsError.Length > 0;

    /// <summary>Raised with text to copy (the view puts it on the clipboard).</summary>
    public event Action<string>? CopyRequested;

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Phase)) return;
        var phase = _owner.Phase;
        var was = _lastPhase;
        _lastPhase = phase;
        OnPropertyChanged(nameof(CanStartOmp));
        // omp starts again (a restart, a crash, another session): it reads every file anew.
        if (phase == SessionPhase.Starting) RestartDone();
        if (phase == SessionPhase.Ready && was is SessionPhase.Starting or SessionPhase.Stopped or SessionPhase.Faulted or SessionPhase.NotStarted
            && _owner.IsSettingsOpen && _owner.SettingsCategory == "connectors")
            _ = RefreshAsync();
    }

    /// <summary>Loads the list from omp (<c>/mcp list</c>), the tools this session has, and each server's file entry.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        var version = ++_loadVersion;
        IsOmpRunning = _owner.CanRunOmpCommands;
        OnPropertyChanged(nameof(CanStartOmp));
        if (!IsOmpRunning)
        {
            Servers.Clear();
            OtherTools = "";
            LoadError = "";
            HasLoaded = true;
            NotifyList();
            return;
        }
        IsLoading = true;
        try
        {
            var pathTask = LoadUserConfigPathAsync();
            _ = LoadOptionsAsync();
            var list = await _owner.RunOmpCommandAsync("/mcp list");
            if (version != _loadVersion) return;
            if (!list.Ok || list.AgentInvoked)
            {
                LoadError = list.AgentInvoked ? "omp did not run /mcp list as a command." : list.Error ?? "omp did not answer.";
                return;
            }
            if (OmpMcp.ParseList(list.Output) is not { } entries)
            {
                LoadError = list.Output.Length > 0 ? list.Output : "omp printed nothing for /mcp list.";
                return;
            }
            var tools = await _owner.RunOmpCommandAsync("/tools");
            if (version != _loadVersion) return;
            LoadError = "";
            // omp also loads the project folder's own mcp.json and .mcp.json (discovery/mcp-json.ts), which /mcp list
            // leaves out; a name omp's files already use wins.
            var fileServers = new List<(McpServerEntry Entry, System.Text.Json.Nodes.JsonObject Config, string File)>();
            var off = DisabledNames();
            if (_owner.ProjectFolder is { } folder)
                foreach (var file in new[] { "mcp.json", ".mcp.json" })
                    foreach (var (name, config) in McpConfigFile.ReadServers(Path.Combine(folder, file)))
                        if (entries.All(e => e.Name != name) && fileServers.All(f => f.Entry.Name != name))
                            fileServers.Add((McpConfigFile.EntryOf(name, config, "file", off), config, file));
            var sessionTools = tools.Ok ? OmpMcp.McpToolsIn(tools.Output) : [];
            var (byServer, other) = OmpMcp.AttributeTools(entries.Select(e => e.Name).Concat(fileServers.Select(f => f.Entry.Name)), sessionTools);
            OtherTools = other.Count == 0 ? "" : string.Join(", ", other);
            var previous = Servers.ToDictionary(r => r.Name + "\u0001" + r.Scope, StringComparer.Ordinal);
            Servers.Clear();
            foreach (var entry in entries)
            {
                var row = previous.TryGetValue(entry.Name + "\u0001" + entry.Scope, out var old) ? old : new McpServerRowViewModel(this, entry);
                row.Entry = entry;
                row.SetEnabledQuietly(entry.Enabled);
                row.Config = ConfigPath(entry.Scope) is { } path ? McpConfigFile.ReadServer(path, entry.Name) : null;
                row.Notify();
                row.IsBusy = false;
                row.IsConfirmingRemove = false;
                row.SessionTools = byServer.TryGetValue(entry.Name, out var t) ? t.Count : 0;
                ApplySessionState(row, keepTest: old is not null);
                Servers.Add(row);
            }
            foreach (var (entry, config, file) in fileServers)
            {
                var row = previous.TryGetValue(entry.Name + "\u0001file", out var old) ? old : new McpServerRowViewModel(this, entry);
                row.Entry = entry;
                row.SourceFile = file;
                row.Config = config;
                row.SetEnabledQuietly(entry.Enabled);
                row.Notify();
                row.IsBusy = false;
                row.SessionTools = byServer.TryGetValue(entry.Name, out var t) ? t.Count : 0;
                ApplySessionState(row, keepTest: false);
                Servers.Add(row);
            }
            NotifyList();
            // The user file's place comes from omp's CLI (profiles, XDG): its rows get their details when it answers.
            await pathTask;
            if (version != _loadVersion) return;
            foreach (var row in Servers.Where(r => r.Scope == "user" && r.Config is null && _userConfigPath is not null))
            {
                row.Config = McpConfigFile.ReadServer(_userConfigPath!, row.Name);
                row.Notify();
            }
            // Project-file servers switched off in omp's user file (disabledServers)
            var disabled = DisabledNames();
            foreach (var row in Servers.Where(r => r.IsFromProjectFile && r.IsEnabled && disabled.Contains(r.Name)))
            {
                row.Entry = row.Entry with { Enabled = false };
                row.SetEnabledQuietly(false);
                ApplySessionState(row, keepTest: false);
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
                HasLoaded = true;
                NotifyList();
            }
        }
    }

    private void NotifyList()
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowList));
    }

    /// <summary>The badge from what this session has: tools of the server mean omp connected to it when it started.</summary>
    private void ApplySessionState(McpServerRowViewModel row, bool keepTest)
    {
        var toolCount = row.SessionTools;
        if (keepTest && row.Tested && row.IsEnabled && !_pending.Contains(row.Name)) return;
        row.Tested = false;
        row.Failure = "";
        row.Tools = "";
        if (!row.IsEnabled)
            (row.State, row.StateText) = (ConnectorState.Off, _pending.Contains(row.Name) ? "Off after omp restarts" : "Off");
        else if (_pending.Contains(row.Name))
            (row.State, row.StateText) = (ConnectorState.Pending, "Connects when omp restarts");
        else if (OmpMcp.IsExa(row.Name, row.Entry.Location))
            (row.State, row.StateText) = (ConnectorState.Replaced, "omp uses its built-in Exa search instead");
        else if (toolCount > 0)
            (row.State, row.StateText) = (ConnectorState.Connected, string.Create(CultureInfo.InvariantCulture, $"Connected · {toolCount} {(toolCount == 1 ? "tool" : "tools")}"));
        else if (row.Scope is "project" or "file" && HasOptions && !ProjectServers)
            (row.State, row.StateText) = (ConnectorState.Off, "Skipped: servers from project files are off");
        else
            (row.State, row.StateText) = (ConnectorState.NotConnected, "Not connected in this session");
    }

    /// <summary>omp's list of servers switched off by name (<c>disabledServers</c> in the user file).</summary>
    private IReadOnlyList<string> DisabledNames() => _userConfigPath is { } path ? McpConfigFile.ReadNames(path, "disabledServers") : [];

    private async Task LoadUserConfigPathAsync()
    {
        if (_userConfigPath is not null) return;
        var r = await _owner.RunOmpCliAsync(["config", "path"], TimeSpan.FromSeconds(30));
        var dir = r.Ok ? r.Stdout.Trim().Split('\n').LastOrDefault()?.Trim() : null;
        if (!string.IsNullOrEmpty(dir)) _userConfigPath = Path.Combine(dir, "mcp.json");
    }

    /// <summary>omp's file for a scope: the profile's agent folder (<c>omp config path</c>) or &lt;project&gt;/.omp.</summary>
    internal string? ConfigPath(string scope) =>
        scope == "project"
            ? _owner.ProjectFolder is { } project ? Path.Combine(project, ".omp", "mcp.json") : null
            : _userConfigPath;

    private async Task LoadOptionsAsync()
    {
        var r = await _owner.RunOmpCliAsync(["config", "get", "mcp.enableProjectConfig", "--json"], TimeSpan.FromSeconds(30));
        var value = OmpPlugins.ParseConfigGet(r.Stdout, out var ok);
        if (!r.Ok || !ok)
        {
            HasOptions = false;
            OptionsError = "omp's settings could not be read: " + Why(r);
            return;
        }
        _syncingOptions = true;
        ProjectServers = value is not JsonValue v || !v.TryGetValue<bool>(out var b) || b;
        _syncingOptions = false;
        OptionsError = "";
        HasOptions = true;
        foreach (var row in Servers.Where(r => r.Scope is "project" or "file")) ApplySessionState(row, keepTest: true);
    }

    private static string Why(OmpCliResult r) =>
        OmpPlugins.Clean(r.Stderr.Trim().Length > 0 ? r.Stderr : r.Stdout) is { Length: > 0 } s ? s : $"omp exited with code {r.ExitCode}.";

    partial void OnProjectServersChanged(bool value)
    {
        if (!_syncingOptions) _ = SetProjectServersAsync(value);
    }

    private async Task SetProjectServersAsync(bool value)
    {
        ClearFeedback();
        var r = await _owner.RunOmpCliAsync(["config", "set", "mcp.enableProjectConfig", value ? "true" : "false", "--json"], TimeSpan.FromSeconds(30));
        if (!r.Ok)
        {
            _syncingOptions = true;
            ProjectServers = !value;
            _syncingOptions = false;
            OptionsFeedback.Error = Why(r);
            return;
        }
        OptionsFeedback.Notice = value ? "omp will load servers from project files." : "omp will skip servers from project files.";
        RequireRestart(OptionsFeedback);
    }

    // ── Row actions ──

    private static string Said(SlashCommandResult r) => r.Ok ? r.Output.Length > 0 ? r.Output : "omp printed nothing." : r.Error ?? "omp did not answer.";

    internal async Task SetEnabledAsync(McpServerRowViewModel row, bool enabled)
    {
        ClearFeedback();
        row.IsBusy = true;
        try
        {
            var r = await _owner.RunOmpCommandAsync($"/mcp {(enabled ? "enable" : "disable")} {OmpMcp.Quote(row.Name)}");
            if (!r.Ok || !OmpMcp.IsToggled(r.Output, row.Name, enabled))
            {
                row.SetEnabledQuietly(!enabled);
                Feedback.Error = Said(r);
                return;
            }
            row.Entry = row.Entry with { Enabled = enabled };
            _pending.Add(row.Name);
            ApplySessionState(row, keepTest: false);
            Feedback.Notice = enabled ? $"Turned on “{row.Name}”." : $"Turned off “{row.Name}”.";
            RequireRestart(Feedback);
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    internal async Task TestAsync(McpServerRowViewModel row)
    {
        row.IsConfirmingRemove = false;
        row.State = ConnectorState.Testing;
        row.StateText = "Testing…";
        row.Failure = "";
        row.Tools = "";
        var r = await _owner.RunOmpCommandAsync("/mcp test " + OmpMcp.Quote(row.Name), TimeSpan.FromMinutes(2));
        var result = r.Ok ? OmpMcp.ParseTest(r.Output) : new McpTestResult(false, 0, [], r.Error ?? "omp did not answer.");
        row.Tested = true;
        if (result.Connected)
        {
            row.State = ConnectorState.Connected;
            row.StateText = string.Create(CultureInfo.InvariantCulture, $"Test passed · {result.ToolCount} {(result.ToolCount == 1 ? "tool" : "tools")}");
            row.Tools = string.Join(", ", result.Tools);
        }
        else
        {
            row.State = ConnectorState.Failed;
            row.StateText = "Test failed";
            row.Failure = result.Message;
        }
    }

    internal async Task RemoveAsync(McpServerRowViewModel row)
    {
        ClearFeedback();
        row.IsBusy = true;
        try
        {
            var r = await _owner.RunOmpCommandAsync(OmpMcp.RemoveCommand(row.Name, row.Scope));
            if (!r.Ok || !OmpMcp.IsRemoved(r.Output, row.Name))
            {
                Feedback.Error = Said(r);
                return;
            }
            Servers.Remove(row);
            NotifyList();
            Feedback.Notice = $"Removed “{row.Name}”.";
            RequireRestart(Feedback);
        }
        finally
        {
            row.IsBusy = false;
            row.IsConfirmingRemove = false;
        }
    }

    // ── Add / edit ──

    [RelayCommand]
    private void OpenAddForm()
    {
        _editing = null;
        IsEditing = false;
        FormName = FormCommand = FormArgs = FormEnv = FormUrl = FormToken = FormHeaders = FormError = "";
        FormTransport = "stdio";
        FormScope = "user";
        IsFormOpen = true;
    }

    internal void OpenEditForm(McpServerRowViewModel row)
    {
        _editing = row;
        IsEditing = true;
        FormError = "";
        FormName = row.Name;
        FormScope = row.Scope;
        FormTransport = row.Transport;
        FormCommand = McpConfigFile.GetString(row.Config, "command") ?? "";
        FormArgs = string.Join("\n", McpConfigFile.GetArgs(row.Config));
        FormEnv = string.Join("\n", McpConfigFile.GetMap(row.Config, "env").Select(kv => $"{kv.Key}={kv.Value}"));
        FormUrl = McpConfigFile.GetString(row.Config, "url") ?? "";
        var headers = McpConfigFile.GetMap(row.Config, "headers").ToList();
        var auth = headers.FirstOrDefault(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && h.Value.StartsWith("Bearer ", StringComparison.Ordinal));
        FormToken = auth.Key is null ? "" : auth.Value["Bearer ".Length..];
        FormHeaders = string.Join("\n", headers.Where(h => auth.Key is null || h.Key != auth.Key).Select(h => $"{h.Key}: {h.Value}"));
        IsFormOpen = true;
    }

    [RelayCommand]
    private void CancelForm()
    {
        IsFormOpen = false;
        _editing = null;
        FormError = "";
    }

    [RelayCommand]
    private void SetTransport(string? t)
    {
        if (t is "stdio" or "http" or "sse") FormTransport = t;
    }

    [RelayCommand]
    private void SetScope(string? s)
    {
        if (!IsEditing && s is "user" or "project") FormScope = s;
    }

    /// <summary>The form as omp would take it, or the first problem in it.</summary>
    internal (string? Error, List<string> Args, List<KeyValuePair<string, string>> Env, List<KeyValuePair<string, string>> Headers) ReadForm()
    {
        var name = FormName.Trim();
        var args = Lines(FormArgs).ToList();
        var env = new List<KeyValuePair<string, string>>();
        var headers = new List<KeyValuePair<string, string>>();
        if (OmpMcp.ValidateName(name) is { } bad) return (bad, args, env, headers);
        if (!IsEditing && Servers.Any(s => s.Name == name && s.Scope == FormScope))
            return ($"There is already a connector named “{name}” in {(FormScope == "project" ? "this project" : "your settings")}.", args, env, headers);
        if (FormIsStdio)
        {
            if (FormCommand.Trim().Length == 0) return ("Enter the command that starts the server (for example npx).", args, env, headers);
            if (FormCommand.Contains('\n', StringComparison.Ordinal)) return ("The command is one line; put its arguments below, one per line.", args, env, headers);
            foreach (var line in Lines(FormEnv))
            {
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0) return ($"“{line}” is not NAME=value.", args, env, headers);
                env.Add(new(line[..eq].Trim(), line[(eq + 1)..]));
            }
        }
        else
        {
            var url = FormUrl.Trim();
            if (url.Length == 0) return ("Enter the server's URL.", args, env, headers);
            if (url.Any(char.IsWhiteSpace)) return ("The URL cannot contain spaces.", args, env, headers);
            foreach (var line in Lines(FormHeaders))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0) return ($"“{line}” is not Name: value.", args, env, headers);
                headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }
            if (FormToken.Trim().Any(char.IsWhiteSpace)) return ("The token cannot contain spaces.", args, env, headers);
        }
        return (null, args, env, headers);
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Replace("\r", "", StringComparison.Ordinal).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    [RelayCommand]
    private async Task SubmitFormAsync()
    {
        var (problem, args, env, headers) = ReadForm();
        if (problem is not null)
        {
            FormError = problem;
            return;
        }
        FormError = "";
        IsSubmitting = true;
        try
        {
            if (IsEditing && _editing is { } row) await SaveEditAsync(row, args, env, headers);
            else await AddAsync(args, env, headers);
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private async Task AddAsync(List<string> args, List<KeyValuePair<string, string>> env, List<KeyValuePair<string, string>> headers)
    {
        if (!_owner.CanRunOmpCommands)
        {
            FormError = "omp isn't running. Start it, then add the connector: omp checks it and saves it.";
            return;
        }
        var name = FormName.Trim();
        var stdio = FormIsStdio;
        var command = OmpMcp.AddCommand(name, FormScope, FormTransport, FormCommand.Trim(), args, FormUrl.Trim(), FormToken.Trim());
        var r = await _owner.RunOmpCommandAsync(command);
        if (!r.Ok || !OmpMcp.IsAdded(r.Output, name))
        {
            FormError = Said(r);
            return;
        }
        ClearFeedback();
        // omp's /mcp add has no environment or extra headers: they go into the entry omp just wrote.
        var extra = stdio ? env : headers;
        if (extra.Count > 0)
        {
            await LoadUserConfigPathAsync();
            var path = ConfigPath(FormScope);
            try
            {
                if (path is null) throw new IOException("omp's settings folder is unknown");
                McpConfigFile.UpdateServer(path, name, e =>
                {
                    if (stdio) McpConfigFile.SetMap(e, "env", env);
                    else McpConfigFile.SetMap(e, "headers", [.. McpConfigFile.GetMap(e, "headers"), .. headers]);
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                Feedback.Error = $"omp added “{name}”, but its {(stdio ? "environment variables" : "headers")} could not be saved: {e.Message}";
            }
        }
        _pending.Add(name);
        IsFormOpen = false;
        Feedback.Notice = $"Added “{name}”.";
        RequireRestart(Feedback);
        await RefreshAsync();
    }

    private async Task SaveEditAsync(McpServerRowViewModel row, List<string> args, List<KeyValuePair<string, string>> env, List<KeyValuePair<string, string>> headers)
    {
        await LoadUserConfigPathAsync();
        if (ConfigPath(row.Scope) is not { } path)
        {
            FormError = "omp's settings folder is unknown, so the file cannot be changed.";
            return;
        }
        var transport = FormTransport;
        var token = FormToken.Trim();
        var command = FormCommand.Trim();
        var url = FormUrl.Trim();
        try
        {
            McpConfigFile.UpdateServer(path, row.Name, e =>
            {
                e["type"] = transport;
                if (transport == "stdio")
                {
                    e.Remove("url");
                    e.Remove("headers");
                    e["command"] = command;
                    if (args.Count > 0) e["args"] = new JsonArray([.. args.Select(a => (JsonNode)JsonValue.Create(a))]);
                    else e.Remove("args");
                    McpConfigFile.SetMap(e, "env", env);
                }
                else
                {
                    e.Remove("command");
                    e.Remove("args");
                    e.Remove("env");
                    e.Remove("cwd");
                    // As omp's /mcp add: an address without a scheme is https.
                    e["url"] = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
                    var all = headers.Where(h => !(token.Length > 0 && h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))).ToList();
                    if (token.Length > 0) all.Insert(0, new("Authorization", "Bearer " + token));
                    McpConfigFile.SetMap(e, "headers", all);
                }
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            FormError = "The file could not be changed: " + e.Message;
            return;
        }
        ClearFeedback();
        _pending.Add(row.Name);
        IsFormOpen = false;
        _editing = null;
        Feedback.Notice = $"Saved “{row.Name}”.";
        RequireRestart(Feedback);
        await RefreshAsync();
    }

    // ── Resources and prompts ──

    [RelayCommand]
    private async Task LoadItemsAsync()
    {
        Resources.Clear();
        Prompts.Clear();
        ItemsMessage = "";
        if (!_owner.CanRunOmpCommands)
        {
            ItemsMessage = "omp isn't running.";
            NotifyItems();
            return;
        }
        IsLoadingItems = true;
        try
        {
            var resources = await _owner.RunOmpCommandAsync("/mcp resources", TimeSpan.FromMinutes(3));
            var prompts = await _owner.RunOmpCommandAsync("/mcp prompts", TimeSpan.FromMinutes(3));
            if (!resources.Ok || !prompts.Ok)
            {
                ItemsMessage = (resources.Ok ? prompts.Error : resources.Error) ?? "omp did not answer.";
                return;
            }
            var commands = _owner.OmpCommandCatalog.Where(c => c.Source == "mcp_prompt").Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            var resourceItems = OmpMcp.ParseItems(resources.Output, out var noResources);
            var promptItems = OmpMcp.ParseItems(prompts.Output, out _);
            foreach (var i in resourceItems) Resources.Add(new McpItemViewModel(this, i, false));
            foreach (var i in promptItems) Prompts.Add(new McpItemViewModel(this, i, commands.Contains($"{i.Server}:{i.Name}")));
            if (Resources.Count == 0 && Prompts.Count == 0)
                ItemsMessage = noResources == OmpMcp.NoServers ? "No connectors are set up yet." : "The connected servers offer no resources or prompts.";
        }
        finally
        {
            IsLoadingItems = false;
            NotifyItems();
        }
    }

    private void NotifyItems()
    {
        OnPropertyChanged(nameof(HasResources));
        OnPropertyChanged(nameof(HasPrompts));
    }

    // ── Smithery ──

    [RelayCommand]
    private async Task SearchAsync()
    {
        var q = SearchQuery.Trim();
        if (q.Length == 0) return;
        SearchResults.Clear();
        SearchMessage = "";
        SearchNeedsSignIn = false;
        if (!_owner.CanRunOmpCommands)
        {
            SearchMessage = "omp isn't running.";
            OnPropertyChanged(nameof(HasSearchResults));
            return;
        }
        IsSearching = true;
        try
        {
            // The keyword alone: omp reads a word starting with "--" as one of its options (and the next as its value).
            var keyword = string.Join(' ', q.Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeWhile(w => !w.StartsWith("--", StringComparison.Ordinal)).Select(OmpMcp.Quote));
            if (keyword.Length == 0) return;
            var r = await _owner.RunOmpCommandAsync($"/mcp smithery-search {keyword} --limit 20", TimeSpan.FromMinutes(1));
            if (!r.Ok)
            {
                SearchMessage = r.Error ?? "omp did not answer.";
                return;
            }
            if (OmpMcp.ParseSmithery(r.Output) is { } results)
                foreach (var x in results) SearchResults.Add(new SmitheryResultViewModel(this, x));
            else
            {
                SearchNeedsSignIn = OmpMcp.IsSmitherySignInNeeded(r.Output);
                SearchMessage = r.Output.Length > 0 ? r.Output : "omp printed nothing.";
            }
        }
        finally
        {
            IsSearching = false;
            OnPropertyChanged(nameof(HasSearchResults));
        }
    }

    [RelayCommand] private void CopySmitheryLogin() => Copy(SmitheryLoginCommand);

    // ── Feedback, restart, terminal, clipboard ──

    private void ClearFeedback()
    {
        foreach (var f in new[] { Feedback, OptionsFeedback })
        {
            f.Notice = "";
            f.Error = "";
        }
    }

    /// <summary>omp applies the change when it starts: the section that made it offers the restart.</summary>
    internal void RequireRestart(PageFeedbackViewModel where)
    {
        NeedsRestart = true;
        foreach (var f in new[] { Feedback, OptionsFeedback }) f.ShowRestart = ReferenceEquals(f, where);
        where.RestartText = _owner.IsRunning
            ? "omp reads connectors when it starts. Restart it once the reply finishes."
            : "omp reads connectors when it starts. Restart it to apply the change.";
    }

    private async Task RestartOmpAsync()
    {
        var where = Feedback.ShowRestart ? Feedback : OptionsFeedback;
        if (_owner.IsRunning)
        {
            where.RestartText = "omp is replying. Restart it once the reply finishes; the change waits until then.";
            return;
        }
        where.IsRestarting = true;
        try
        {
            if (await _owner.RestartOmpToApplyAsync())
            {
                RestartDone();
                _owner.Plugins.RestartDone();
                where.Notice = "omp restarted and read the connectors again.";
                await RefreshAsync();
            }
        }
        finally
        {
            where.IsRestarting = false;
        }
    }

    internal void RestartDone()
    {
        NeedsRestart = false;
        _pending.Clear();
        foreach (var row in Servers) row.Tested = false;
        foreach (var f in new[] { Feedback, OptionsFeedback }) f.ShowRestart = false;
    }

    [RelayCommand]
    private async Task StartOmpAsync()
    {
        await _owner.RestartOmpToApplyAsync();
        await RefreshAsync();
    }

    [RelayCommand] internal void OpenOmpTerminal() => _owner.OpenOmpTerminalFromSettings();

    internal void Copy(string text) => CopyRequested?.Invoke(text);

    internal void UseInComposer(string text) => _owner.UseInComposer(text);
}
