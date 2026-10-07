using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>An installed plugin (npm package or marketplace plugin).</summary>
public sealed partial class PluginRowViewModel : ObservableObject
{
    private readonly PluginsViewModel _page;
    private bool _syncing;

    internal PluginRowViewModel(PluginsViewModel page, InstalledPluginInfo info)
    {
        _page = page;
        Info = info;
        _syncing = true;
        IsEnabled = info.Enabled;
        _syncing = false;
    }

    internal InstalledPluginInfo Info { get; private set; }

    public string Id => Info.Id;
    public string Name => Info.Name;
    public string Version => Info.Version.Length > 0 ? "v" + Info.Version : "";
    public bool HasVersion => Info.Version.Length > 0;
    public bool IsMarketplace => Info.Marketplace is not null;
    public string SourceLabel => Info.Marketplace ?? "npm";
    public string SourceTip => Info.Marketplace is { } m ? $"From the marketplace {m}" + (Info.Scope == "project" ? ", installed for this project" : "") : "An npm package (or a linked folder)";
    public bool IsProjectScope => Info.Scope == "project";
    public bool IsShadowed => Info.Shadowed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    private string _description = "";

    public bool HasDescription => Description.Length > 0;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConfirmingUninstall;

    /// <summary>The marketplace's catalog lists a newer version.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    private string _updateVersion = "";

    public bool HasUpdate => UpdateVersion.Length > 0;
    public string UninstallQuestion => $"Uninstall “{Name}”? Its files are removed; its skills, commands and tools go away.";

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_syncing) _ = _page.SetPluginEnabledAsync(this, value);
    }

    internal void Update(InstalledPluginInfo info)
    {
        Info = info;
        _syncing = true;
        IsEnabled = info.Enabled;
        _syncing = false;
        foreach (var p in new[] { nameof(Version), nameof(HasVersion), nameof(SourceLabel), nameof(SourceTip), nameof(IsProjectScope), nameof(IsShadowed) })
            OnPropertyChanged(p);
    }

    internal void SetEnabledQuietly(bool value)
    {
        _syncing = true;
        IsEnabled = value;
        _syncing = false;
    }

    [RelayCommand] private void AskUninstall() => IsConfirmingUninstall = true;
    [RelayCommand] private void CancelUninstall() => IsConfirmingUninstall = false;
    [RelayCommand] private Task Uninstall() => _page.UninstallAsync(this);
    [RelayCommand] private Task Upgrade() => _page.UpgradeAsync(this);
}

/// <summary>A configured marketplace; opened, it lists its plugins.</summary>
public sealed partial class MarketplaceRowViewModel(PluginsViewModel page, MarketplaceInfo info) : ObservableObject
{
    public string Name => info.Name;
    public string Source => info.Source;
    public ObservableCollection<MarketplacePluginRowViewModel> Plugins { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrowseLabel))]
    private bool _isOpen;

    public string BrowseLabel => IsOpen ? "Hide plugins" : "Browse";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConfirmingRemove;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = "";

    public bool HasMessage => Message.Length > 0;
    public string RemoveQuestion => $"Remove the marketplace “{Name}”? Plugins already installed from it stay installed.";

    [RelayCommand] private Task Browse() => page.BrowseAsync(this);
    [RelayCommand] private Task Refresh() => page.UpdateMarketplaceAsync(this);
    [RelayCommand] private void AskRemove() => IsConfirmingRemove = true;
    [RelayCommand] private void CancelRemove() => IsConfirmingRemove = false;
    [RelayCommand] private Task Remove() => page.RemoveMarketplaceAsync(this);
}

/// <summary>A plugin a marketplace offers.</summary>
public sealed partial class MarketplacePluginRowViewModel(PluginsViewModel page, MarketplaceRowViewModel marketplace, MarketplacePluginInfo info) : ObservableObject
{
    public string Name => info.Name;
    public string Version => info.Version is { Length: > 0 } v ? "v" + v : "";
    public bool HasVersion => Version.Length > 0;
    public string Description => info.Description ?? "";
    public bool HasDescription => Description.Length > 0;
    public string Id => $"{info.Name}@{marketplace.Name}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _isBusy;

    public bool CanInstall => !IsInstalled && !IsBusy;

    [RelayCommand] private Task Install() => page.InstallFromMarketplaceAsync(this);
}

/// <summary>A skill omp offers as /skill:name (or one switched off in omp's disabledExtensions).</summary>
public sealed partial class SkillRowViewModel : ObservableObject
{
    private readonly PluginsViewModel _page;
    private bool _syncing;

    internal SkillRowViewModel(PluginsViewModel page, string name, string description, bool enabled)
    {
        _page = page;
        Name = name;
        Description = description;
        _syncing = true;
        IsEnabled = enabled;
        _syncing = false;
    }

    public string Name { get; }
    public string Description { get; }
    public bool HasDescription => Description.Length > 0;
    public string Command => "/skill:" + Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUse))]
    private bool _isEnabled;

    [ObservableProperty] private bool _isBusy;

    /// <summary>Only a skill omp has loaded can be used now (a switched-on skill loads when omp restarts).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUse))]
    private bool _isLoaded = true;

    public bool CanUse => IsEnabled && IsLoaded;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_syncing) _ = _page.SetSkillEnabledAsync(this, value);
    }

    internal void SetEnabledQuietly(bool value)
    {
        _syncing = true;
        IsEnabled = value;
        _syncing = false;
    }

    [RelayCommand] private void Use() => _page.UseSkill(this);
}

/// <summary>A switch for one of omp's <c>skills.*</c> settings.</summary>
public sealed partial class SkillSourceViewModel : ObservableObject
{
    private readonly PluginsViewModel _page;
    private bool _syncing;

    internal SkillSourceViewModel(PluginsViewModel page, string key, string title, string description, bool fallback)
    {
        _page = page;
        Key = key;
        Title = title;
        Description = description;
        Fallback = fallback;
        SetQuietly(fallback);
    }

    public string Key { get; }
    public string Title { get; }
    public string Description { get; }
    internal bool Fallback { get; }

    [ObservableProperty] private bool _isOn;

    partial void OnIsOnChanged(bool value)
    {
        if (!_syncing) _ = _page.SetSettingAsync(this, value);
    }

    internal void SetQuietly(bool value)
    {
        _syncing = true;
        IsOn = value;
        _syncing = false;
    }
}

/// <summary>A folder in <c>skills.customDirectories</c>.</summary>
public sealed partial class SkillDirectoryViewModel(PluginsViewModel page, string path) : ObservableObject
{
    public string Path => path;
    [RelayCommand] private Task Remove() => page.RemoveDirectoryAsync(this);
}

/// <summary>
/// Settings → Plugins and skills: omp's plugins through its CLI (<c>omp plugin list|install|uninstall|enable|disable|
/// upgrade|discover|marketplace</c>), then <c>/reload-plugins</c> over RPC so skills and commands apply at once; skills
/// from omp's command catalog, switched with <c>disabledExtensions</c> (ids <c>skill:&lt;name&gt;</c>, as omp's own
/// extensions dashboard does) and the <c>skills.*</c> settings through <c>omp config set</c>.
/// </summary>
public sealed partial class PluginsViewModel : ObservableObject
{
    private static readonly TimeSpan CliWait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InstallWait = TimeSpan.FromMinutes(10);

    private readonly MainViewModel _owner;
    private readonly Dictionary<string, string> _skillDescriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<MarketplacePluginInfo>> _catalogs = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _disabledExtensions = [];
    private IReadOnlyList<string> _directories = [];
    private int _loadVersion;
    private bool _syncingAutoUpdate;

    public PluginsViewModel(MainViewModel owner)
    {
        _owner = owner;
        PluginsFeedback = new PageFeedbackViewModel(RestartOmpAsync);
        MarketplacesFeedback = new PageFeedbackViewModel(RestartOmpAsync);
        SkillsFeedback = new PageFeedbackViewModel(RestartOmpAsync);
        SourcesFeedback = new PageFeedbackViewModel(RestartOmpAsync);
        owner.SettingsCategoryShown += key =>
        {
            if (key != "plugins") return;
            ClearFeedback();
            _ = RefreshAsync();
        };
        owner.PropertyChanged += OnOwnerChanged;
        SkillSources =
        [
            new(this, "skills.enabled", "Use skills", "omp offers skills to the model and as /skill: commands", true),
            new(this, "skills.enableSkillCommands", "Skill commands", "Each skill as a /skill:name command (the list above shows those)", true),
            new(this, "skills.enablePiUser", "omp skills", "~/.omp/agent/skills (or your profile's)", true),
            new(this, "skills.enablePiProject", "omp project skills", ".omp/skills in the project", true),
            new(this, "skills.enableClaudeUser", "Claude Code skills", "~/.claude/skills", false),
            new(this, "skills.enableClaudeProject", "Claude Code project skills", ".claude/skills in the project", true),
            new(this, "skills.enableAgentsUser", "Agent skills", "~/.agents/skills and ~/.agent/skills", true),
            new(this, "skills.enableAgentsProject", "Agent project skills", ".agents/skills and .agent/skills in the project", true),
            new(this, "skills.enableCodexUser", "Codex skills", "~/.codex/skills", false),
        ];
        // Created while the page is already shown (the page asked for it late): load now.
        if (owner.IsSettingsOpen && owner.SettingsCategory == "plugins") _ = RefreshAsync();
    }

    public ObservableCollection<PluginRowViewModel> Plugins { get; } = [];
    public ObservableCollection<MarketplaceRowViewModel> Marketplaces { get; } = [];
    public ObservableCollection<SkillRowViewModel> Skills { get; } = [];
    public IReadOnlyList<SkillSourceViewModel> SkillSources { get; }
    public ObservableCollection<SkillDirectoryViewModel> Directories { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPluginsEmpty), nameof(ShowPluginsLoading))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPluginsEmpty), nameof(ShowPluginsLoading))]
    private bool _hasLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPluginsError), nameof(ShowPluginsEmpty))]
    private string _pluginsError = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMarketplacesError), nameof(ShowMarketplacesEmpty))]
    private string _marketplacesError = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettingsError))]
    private string _settingsError = "";

    /// <summary>Feedback next to the plugin list, the marketplaces and the skills.</summary>
    public PageFeedbackViewModel PluginsFeedback { get; }
    public PageFeedbackViewModel MarketplacesFeedback { get; }
    public PageFeedbackViewModel SkillsFeedback { get; }
    public PageFeedbackViewModel SourcesFeedback { get; }

    /// <summary>A change omp applies only when it starts again (the section that made it offers the restart).</summary>
    [ObservableProperty] private bool _needsRestart;

    // Install
    [ObservableProperty] private string _installSpec = "";
    [ObservableProperty] private bool _isInstalling;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallOutput))]
    private string _installOutput = "";

    [ObservableProperty] private bool _installFailed;

    // Marketplaces
    [ObservableProperty] private string _marketplaceSource = "";
    [ObservableProperty] private bool _isAddingMarketplace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMarketplaceFormError))]
    private string _marketplaceFormError = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoUpdateOff), nameof(AutoUpdateNotify), nameof(AutoUpdateAuto))]
    private string _autoUpdate = "notify";

    [ObservableProperty] private bool _hasSettings;

    // Skills
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkillsMessage))]
    private string _skillsMessage = "";

    [ObservableProperty] private string _newDirectory = "";

    public bool ShowPluginsLoading => IsLoading && !HasLoaded;
    public bool ShowPluginsEmpty => HasLoaded && !IsLoading && PluginsError.Length == 0 && Plugins.Count == 0;
    public bool HasPlugins => Plugins.Count > 0;
    public bool HasPluginsError => PluginsError.Length > 0;
    public bool HasMarketplaces => Marketplaces.Count > 0;
    public bool ShowMarketplacesEmpty => HasLoaded && !PageFailed && MarketplacesError.Length == 0 && Marketplaces.Count == 0;

    /// <summary>None of omp's lists could be read (its command line failed): one message for the whole page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PluginsErrorTitle), nameof(ShowMarketplacesEmpty))]
    private bool _pageFailed;

    public string PluginsErrorTitle => PageFailed ? "Couldn't load plugins and skills from omp" : "omp couldn't list the plugins";
    public bool HasMarketplacesError => MarketplacesError.Length > 0;
    public bool HasMarketplaceFormError => MarketplaceFormError.Length > 0;
    public bool HasSettingsError => SettingsError.Length > 0;
    public bool HasInstallOutput => InstallOutput.Length > 0;
    public bool HasSkills => Skills.Count > 0;
    public bool HasSkillsMessage => SkillsMessage.Length > 0;
    public bool HasDirectories => Directories.Count > 0;
    public bool AutoUpdateOff => AutoUpdate == "off";
    public bool AutoUpdateNotify => AutoUpdate == "notify";
    public bool AutoUpdateAuto => AutoUpdate == "auto";

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Phase)) return;
        if (_owner.Phase == SessionPhase.Starting) RestartDone();
        if (_owner.Phase == SessionPhase.Ready && _owner.IsSettingsOpen && _owner.SettingsCategory == "plugins") RefreshSkills();
    }

    /// <summary>Installed plugins, marketplaces and omp's settings (three CLI calls at once), then the skills.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        var version = ++_loadVersion;
        IsLoading = true;
        try
        {
            var list = _owner.RunOmpCliAsync(["plugin", "list", "--json"], CliWait);
            var markets = _owner.RunOmpCliAsync(["plugin", "marketplace", "list"], CliWait);
            var config = _owner.RunOmpCliAsync(["config", "list", "--json"], CliWait);
            await Task.WhenAll(list, markets, config);
            if (version != _loadVersion) return;
            ApplyConfig(config.Result);
            ApplyMarketplaces(markets.Result);
            ApplyPlugins(list.Result);
            // omp's command line failed altogether (the same reason three times): say it once, at the top.
            PageFailed = !list.Result.Ok && !markets.Result.Ok && !config.Result.Ok;
            if (PageFailed)
            {
                MarketplacesError = "";
                SettingsError = "";
            }
            RefreshSkills();
            await LoadCatalogsAsync(version);
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
                HasLoaded = true;
                NotifyLists();
            }
        }
    }

    private void NotifyLists()
    {
        foreach (var p in new[] { nameof(ShowPluginsEmpty), nameof(HasPlugins), nameof(HasMarketplaces), nameof(ShowMarketplacesEmpty), nameof(HasSkills), nameof(HasDirectories) })
            OnPropertyChanged(p);
    }

    private static string Why(OmpCliResult r) =>
        OmpPlugins.Clean(r.Stderr.Trim().Length > 0 ? r.Stderr : r.Stdout) is { Length: > 0 } s ? s : $"omp exited with code {r.ExitCode}.";

    private void ApplyPlugins(OmpCliResult r)
    {
        var parsed = r.Ok ? OmpPlugins.ParseList(r.Stdout) : null;
        if (parsed is null)
        {
            PluginsError = r.Ok ? "omp's plugin list could not be read." : Why(r);
            Plugins.Clear();
            return;
        }
        PluginsError = "";
        var old = Plugins.ToDictionary(p => p.Id, StringComparer.Ordinal);
        Plugins.Clear();
        foreach (var info in parsed)
        {
            var row = old.TryGetValue(info.Id, out var existing) ? existing : new PluginRowViewModel(this, info);
            row.Update(info);
            row.IsBusy = false;
            row.IsConfirmingUninstall = false;
            if (info.Description is { } d) row.Description = d;
            Plugins.Add(row);
        }
        ApplyCatalogsToRows();
    }

    private void ApplyMarketplaces(OmpCliResult r)
    {
        var parsed = r.Ok ? OmpPlugins.ParseMarketplaces(r.Stdout) : null;
        if (parsed is null)
        {
            MarketplacesError = r.Ok ? "omp's marketplace list could not be read." : Why(r);
            Marketplaces.Clear();
            return;
        }
        MarketplacesError = "";
        var open = Marketplaces.Where(m => m.IsOpen).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Marketplaces.Clear();
        foreach (var m in parsed) Marketplaces.Add(new MarketplaceRowViewModel(this, m));
        foreach (var name in _catalogs.Keys.Where(k => parsed.All(m => m.Name != k)).ToList()) _catalogs.Remove(name);
        foreach (var m in Marketplaces.Where(m => open.Contains(m.Name)))
        {
            m.IsOpen = true;
            FillMarketplace(m);
        }
    }

    private void ApplyConfig(OmpCliResult r)
    {
        var config = r.Ok ? OmpPlugins.ParseConfigList(r.Stdout) : null;
        if (config is null)
        {
            SettingsError = "Couldn't read omp's settings: " + (r.Ok ? "unexpected output" : Why(r));
            HasSettings = false;
            return;
        }
        SettingsError = "";
        HasSettings = true;
        foreach (var s in SkillSources) s.SetQuietly(OmpPlugins.Bool(config, s.Key, s.Fallback));
        _disabledExtensions = OmpPlugins.Strings(config.GetValueOrDefault("disabledExtensions"));
        _directories = OmpPlugins.Strings(config.GetValueOrDefault("skills.customDirectories"));
        Directories.Clear();
        foreach (var d in _directories) Directories.Add(new SkillDirectoryViewModel(this, d));
        _syncingAutoUpdate = true;
        AutoUpdate = OmpPlugins.String(config, "marketplace.autoUpdate") ?? "notify";
        _syncingAutoUpdate = false;
    }

    /// <summary>omp's /skill: commands, plus the skills switched off in disabledExtensions (omp does not load those).</summary>
    internal void RefreshSkills()
    {
        var catalog = _owner.OmpCommandCatalog.Where(c => c.Source == "skill").ToList();
        foreach (var c in catalog) _skillDescriptions[SkillName(c.Name)] = c.Description ?? "";
        var loaded = catalog.Select(c => SkillName(c.Name)).ToHashSet(StringComparer.Ordinal);
        var off = OmpPlugins.DisabledSkills(_disabledExtensions).ToHashSet(StringComparer.Ordinal);
        var names = loaded.Concat(off).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var old = Skills.ToDictionary(s => s.Name, StringComparer.Ordinal);
        Skills.Clear();
        foreach (var name in names)
        {
            var row = old.TryGetValue(name, out var existing) && existing.IsEnabled == !off.Contains(name) && !existing.IsBusy
                ? existing
                : new SkillRowViewModel(this, name, _skillDescriptions.GetValueOrDefault(name, ""), !off.Contains(name));
            row.IsLoaded = loaded.Contains(name);
            Skills.Add(row);
        }
        SkillsMessage = Skills.Count > 0 ? ""
            : !_owner.CanRunOmpCommands ? "Skills appear while omp is running."
            : !SkillSources[0].IsOn ? "Skills are off (Skill sources below)."
            : !SkillSources[1].IsOn ? "Skill commands are off (Skill sources below), so omp lists no /skill: commands to show here."
            : "omp found no skills. Add SKILL.md folders to one of the places below, or install a plugin that brings skills.";
        OnPropertyChanged(nameof(HasSkills));
    }

    private static string SkillName(string command) => command.StartsWith("skill:", StringComparison.Ordinal) ? command[6..] : command;

    /// <summary>Each marketplace's catalog (for descriptions, updates and browsing).</summary>
    private async Task LoadCatalogsAsync(int version)
    {
        foreach (var m in Marketplaces.ToList())
        {
            var r = await _owner.RunOmpCliAsync(["plugin", "discover", m.Name], CliWait);
            if (version != _loadVersion) return;
            if (r.Ok && OmpPlugins.ParseDiscover(r.Stdout) is { } plugins) _catalogs[m.Name] = plugins;
        }
        ApplyCatalogsToRows();
        foreach (var m in Marketplaces.Where(m => m.IsOpen)) FillMarketplace(m);
    }

    private void ApplyCatalogsToRows()
    {
        foreach (var row in Plugins.Where(p => p.IsMarketplace))
        {
            var entry = _catalogs.GetValueOrDefault(row.SourceLabel)?.FirstOrDefault(p => p.Name == row.Name);
            if (entry?.Description is { } d && row.Description.Length == 0) row.Description = d;
            row.UpdateVersion = entry?.Version is { Length: > 0 } v && IsNewer(v, row.Info.Version) ? v : "";
        }
    }

    internal static bool IsNewer(string candidate, string installed) =>
        System.Version.TryParse(candidate.Split('-')[0], out var a) && System.Version.TryParse(installed.Split('-')[0], out var b) && a > b;

    private void FillMarketplace(MarketplaceRowViewModel m)
    {
        m.Plugins.Clear();
        if (!_catalogs.TryGetValue(m.Name, out var plugins)) return;
        var installed = Plugins.Where(p => p.IsMarketplace && p.SourceLabel == m.Name).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var p in plugins) m.Plugins.Add(new MarketplacePluginRowViewModel(this, m, p) { IsInstalled = installed.Contains(p.Name) });
        m.Message = plugins.Count == 0 ? "This marketplace lists no plugins." : "";
    }

    // ── After a change ──

    private IEnumerable<PageFeedbackViewModel> Areas => [PluginsFeedback, MarketplacesFeedback, SkillsFeedback, SourcesFeedback];

    private void ClearFeedback()
    {
        foreach (var f in Areas)
        {
            f.Notice = "";
            f.Error = "";
        }
    }

    /// <summary>Skills and commands reload at once (<c>/reload-plugins</c>); tools, hooks and MCP servers need a restart.</summary>
    private async Task ReloadAsync(PageFeedbackViewModel where, string done)
    {
        if (_owner.CanRunOmpCommands)
        {
            var r = await _owner.RunOmpCommandAsync("/reload-plugins");
            where.Notice = r.Ok ? done + " Skills and commands are updated." : done;
            await WaitForCatalogAsync();
            RefreshSkills();
        }
        else where.Notice = done;
        RequireRestart(where, "Plugin tools, hooks and MCP servers load when omp starts. Restart it to apply the change.");
    }

    /// <summary>omp's new command list follows /reload-plugins as its own update (at most a moment later).</summary>
    private async Task WaitForCatalogAsync()
    {
        var before = _owner.OmpCommandCatalog;
        for (var i = 0; i < 20 && ReferenceEquals(before, _owner.OmpCommandCatalog); i++) await Task.Delay(50);
    }

    internal void RequireRestart(PageFeedbackViewModel where, string text)
    {
        NeedsRestart = true;
        foreach (var f in Areas) f.ShowRestart = ReferenceEquals(f, where);
        where.RestartText = _owner.IsRunning ? text.Replace("Restart it", "Restart it once the reply finishes", StringComparison.Ordinal) : text;
    }

    private async Task RestartOmpAsync()
    {
        var where = Areas.FirstOrDefault(f => f.ShowRestart) ?? PluginsFeedback;
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
                _owner.Connectors.RestartDone();
                where.Notice = "omp restarted with the new plugins and settings.";
                await WaitForCatalogAsync();
                RefreshSkills();
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
        foreach (var f in Areas) f.ShowRestart = false;
    }

    // ── Plugins ──

    internal async Task SetPluginEnabledAsync(PluginRowViewModel row, bool enabled)
    {
        ClearFeedback();
        row.IsBusy = true;
        try
        {
            List<string> args = ["plugin", enabled ? "enable" : "disable", row.Id, "--json"];
            if (row.IsMarketplace && row.Info.Scope is { } scope) args.AddRange(["--scope", scope]);
            var r = await _owner.RunOmpCliAsync(args, CliWait);
            if (!r.Ok)
            {
                row.SetEnabledQuietly(!enabled);
                PluginsFeedback.Error = Why(r);
                return;
            }
            await ReloadAsync(PluginsFeedback, enabled ? $"Turned on “{row.Name}”." : $"Turned off “{row.Name}”.");
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    internal async Task UninstallAsync(PluginRowViewModel row)
    {
        ClearFeedback();
        row.IsBusy = true;
        try
        {
            List<string> args = ["plugin", "uninstall", row.Id];
            if (row.IsMarketplace && row.Info.Scope is { } scope) args.AddRange(["--scope", scope]);
            var r = await _owner.RunOmpCliAsync(args, InstallWait);
            if (!r.Ok)
            {
                PluginsFeedback.Error = Why(r);
                return;
            }
            Plugins.Remove(row);
            NotifyLists();
            foreach (var m in Marketplaces.Where(m => m.IsOpen)) FillMarketplace(m);
            await ReloadAsync(PluginsFeedback, $"Uninstalled “{row.Name}”.");
        }
        finally
        {
            row.IsBusy = false;
            row.IsConfirmingUninstall = false;
        }
    }

    internal async Task UpgradeAsync(PluginRowViewModel row)
    {
        ClearFeedback();
        row.IsBusy = true;
        try
        {
            List<string> args = ["plugin", "upgrade", row.Id];
            if (row.Info.Scope is { } scope) args.AddRange(["--scope", scope]);
            var r = await _owner.RunOmpCliAsync(args, InstallWait);
            if (!r.Ok)
            {
                PluginsFeedback.Error = Why(r);
                return;
            }
            var said = OmpPlugins.Clean(r.Stdout);
            ApplyPlugins(await _owner.RunOmpCliAsync(["plugin", "list", "--json"], CliWait));
            await ReloadAsync(PluginsFeedback, said.Length > 0 ? said + "." : $"Upgraded “{row.Name}”.");
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        var spec = InstallSpec.Trim();
        if (spec.Length == 0) return;
        ClearFeedback();
        InstallOutput = "";
        InstallFailed = false;
        IsInstalling = true;
        try
        {
            var r = await _owner.RunOmpCliAsync(["plugin", "install", spec], InstallWait);
            InstallFailed = !r.Ok;
            InstallOutput = r.Ok ? OmpPlugins.Clean(r.Stdout) : Why(r);
            if (!r.Ok) return;
            InstallSpec = "";
            ApplyPlugins(await _owner.RunOmpCliAsync(["plugin", "list", "--json"], CliWait));
            NotifyLists();
            await ReloadAsync(PluginsFeedback, $"Installed {spec}.");
        }
        finally
        {
            IsInstalling = false;
        }
    }

    // ── Marketplaces ──

    [RelayCommand]
    private async Task AddMarketplaceAsync()
    {
        var source = MarketplaceSource.Trim();
        if (source.Length == 0) return;
        MarketplaceFormError = "";
        ClearFeedback();
        IsAddingMarketplace = true;
        try
        {
            var r = await _owner.RunOmpCliAsync(["plugin", "marketplace", "add", source], InstallWait);
            if (!r.Ok)
            {
                MarketplaceFormError = Why(r);
                return;
            }
            MarketplaceSource = "";
            ApplyMarketplaces(await _owner.RunOmpCliAsync(["plugin", "marketplace", "list"], CliWait));
            NotifyLists();
            MarketplacesFeedback.Notice = "Added the marketplace. Open it to see its plugins.";
            await LoadCatalogsAsync(_loadVersion);
        }
        finally
        {
            IsAddingMarketplace = false;
        }
    }

    internal async Task BrowseAsync(MarketplaceRowViewModel m)
    {
        if (m.IsOpen)
        {
            m.IsOpen = false;
            return;
        }
        m.IsOpen = true;
        if (_catalogs.ContainsKey(m.Name))
        {
            FillMarketplace(m);
            return;
        }
        m.IsBusy = true;
        m.Message = "";
        try
        {
            var r = await _owner.RunOmpCliAsync(["plugin", "discover", m.Name], CliWait);
            if (r.Ok && OmpPlugins.ParseDiscover(r.Stdout) is { } plugins)
            {
                _catalogs[m.Name] = plugins;
                FillMarketplace(m);
                ApplyCatalogsToRows();
            }
            else m.Message = r.Ok ? OmpPlugins.Clean(r.Stdout) : Why(r);
        }
        finally
        {
            m.IsBusy = false;
        }
    }

    internal async Task UpdateMarketplaceAsync(MarketplaceRowViewModel m)
    {
        ClearFeedback();
        m.IsBusy = true;
        try
        {
            var r = await _owner.RunOmpCliAsync(["plugin", "marketplace", "update", m.Name], InstallWait);
            if (!r.Ok)
            {
                MarketplacesFeedback.Error = Why(r);
                return;
            }
            _catalogs.Remove(m.Name);
            var d = await _owner.RunOmpCliAsync(["plugin", "discover", m.Name], CliWait);
            if (d.Ok && OmpPlugins.ParseDiscover(d.Stdout) is { } plugins) _catalogs[m.Name] = plugins;
            ApplyCatalogsToRows();
            if (m.IsOpen) FillMarketplace(m);
            MarketplacesFeedback.Notice = $"Updated the catalog of “{m.Name}”.";
        }
        finally
        {
            m.IsBusy = false;
        }
    }

    internal async Task RemoveMarketplaceAsync(MarketplaceRowViewModel m)
    {
        ClearFeedback();
        m.IsBusy = true;
        try
        {
            var r = await _owner.RunOmpCliAsync(["plugin", "marketplace", "remove", m.Name], CliWait);
            if (!r.Ok)
            {
                MarketplacesFeedback.Error = Why(r);
                return;
            }
            Marketplaces.Remove(m);
            _catalogs.Remove(m.Name);
            NotifyLists();
            MarketplacesFeedback.Notice = $"Removed the marketplace “{m.Name}”.";
        }
        finally
        {
            m.IsBusy = false;
            m.IsConfirmingRemove = false;
        }
    }

    internal async Task InstallFromMarketplaceAsync(MarketplacePluginRowViewModel p)
    {
        ClearFeedback();
        p.IsBusy = true;
        try
        {
            var r = await _owner.RunOmpCliAsync(["plugin", "install", p.Id], InstallWait);
            if (!r.Ok)
            {
                MarketplacesFeedback.Error = Why(r);
                return;
            }
            p.IsInstalled = true;
            ApplyPlugins(await _owner.RunOmpCliAsync(["plugin", "list", "--json"], CliWait));
            NotifyLists();
            await ReloadAsync(MarketplacesFeedback, $"Installed “{p.Name}”.");
        }
        finally
        {
            p.IsBusy = false;
        }
    }

    partial void OnAutoUpdateChanged(string value)
    {
        if (!_syncingAutoUpdate) _ = SetConfigAsync(MarketplacesFeedback, "marketplace.autoUpdate", value, "Plugin updates at startup: " + value + ".");
    }

    [RelayCommand] private void SetAutoUpdate(string? mode) { if (mode is "off" or "notify" or "auto") AutoUpdate = mode; }

    // ── Skills ──

    internal async Task SetSkillEnabledAsync(SkillRowViewModel skill, bool enabled)
    {
        ClearFeedback();
        skill.IsBusy = true;
        try
        {
            // Read omp's current list first: other ids (mcp:…, rule:…) and changes made elsewhere stay.
            var current = await _owner.RunOmpCliAsync(["config", "get", "disabledExtensions", "--json"], CliWait);
            var value = OmpPlugins.ParseConfigGet(current.Stdout, out var ok);
            if (!current.Ok || !ok)
            {
                skill.SetEnabledQuietly(!enabled);
                SkillsFeedback.Error = "Couldn't read omp's settings: " + Why(current);
                return;
            }
            var id = "skill:" + skill.Name;
            var ids = OmpPlugins.Strings(value).Where(x => x != id).ToList();
            if (!enabled) ids.Add(id);
            var r = await _owner.RunOmpCliAsync(["config", "set", "disabledExtensions", OmpPlugins.ArrayValue(ids), "--json"], CliWait);
            if (!r.Ok)
            {
                skill.SetEnabledQuietly(!enabled);
                SkillsFeedback.Error = Why(r);
                return;
            }
            _disabledExtensions = ids;
            SkillsFeedback.Notice = enabled ? $"Turned on the skill “{skill.Name}”." : $"Turned off the skill “{skill.Name}”.";
            RequireRestart(SkillsFeedback, "omp reads skill settings when it starts. Restart it to apply the change.");
        }
        finally
        {
            skill.IsBusy = false;
        }
    }

    internal void UseSkill(SkillRowViewModel skill) => _owner.UseInComposer(skill.Command + " ");

    internal async Task SetSettingAsync(SkillSourceViewModel source, bool on)
    {
        ClearFeedback();
        var r = await _owner.RunOmpCliAsync(["config", "set", source.Key, on ? "true" : "false", "--json"], CliWait);
        if (!r.Ok)
        {
            source.SetQuietly(!on);
            SourcesFeedback.Error = Why(r);
            return;
        }
        SourcesFeedback.Notice = $"{source.Title}: {(on ? "on" : "off")}.";
        RequireRestart(SourcesFeedback, "omp reads skill settings when it starts. Restart it to apply the change.");
    }

    private async Task<bool> SetConfigAsync(PageFeedbackViewModel where, string key, string value, string done)
    {
        ClearFeedback();
        var r = await _owner.RunOmpCliAsync(["config", "set", key, value, "--json"], CliWait);
        if (!r.Ok)
        {
            where.Error = Why(r);
            return false;
        }
        where.Notice = done;
        RequireRestart(where, "omp reads this setting when it starts. Restart it to apply the change.");
        return true;
    }

    [RelayCommand]
    private async Task AddDirectoryAsync()
    {
        var dir = NewDirectory.Trim();
        if (dir.Length == 0 || _directories.Contains(dir)) return;
        List<string> next = [.. _directories, dir];
        if (await SetConfigAsync(SourcesFeedback, "skills.customDirectories", OmpPlugins.ArrayValue(next), $"omp will also look for skills in {dir}."))
        {
            _directories = next;
            Directories.Add(new SkillDirectoryViewModel(this, dir));
            NewDirectory = "";
            OnPropertyChanged(nameof(HasDirectories));
        }
    }

    internal async Task RemoveDirectoryAsync(SkillDirectoryViewModel d)
    {
        List<string> next = [.. _directories.Where(x => x != d.Path)];
        if (await SetConfigAsync(SourcesFeedback, "skills.customDirectories", OmpPlugins.ArrayValue(next), $"omp will no longer look for skills in {d.Path}."))
        {
            _directories = next;
            Directories.Remove(d);
            OnPropertyChanged(nameof(HasDirectories));
        }
    }
}
