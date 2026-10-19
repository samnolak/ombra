using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.App.Services;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>A line of a details page: what a plugin brings (skills, commands, servers, code) or a fact about it.</summary>
public sealed record PluginFactViewModel(string Icon, string Title, string Detail)
{
    public bool HasDetail => Detail.Length > 0;
    public Avalonia.Media.Geometry? IconData => Avalonia.Application.Current?.TryGetResource(Icon, null, out var v) == true ? v as Avalonia.Media.Geometry : null;
}

public sealed class BlockedItemViewModel(Ombra.Rpc.OmpProjectItem item)
{
    public string Kind => item.Kind;
    public string Name => item.Name;
    public string Source => item.Source;
    public bool HasSource => item.Source.Length > 0;
}

/// <summary>One of an npm plugin's settings on its details page (<c>omp plugin config</c>).</summary>
public sealed partial class PluginSettingRowViewModel(PluginsHubViewModel hub, PluginRowViewModel plugin, PluginSettingInfo info) : ObservableObject
{
    public string Key => info.Key;
    public string Description => info.Description ?? "";
    public bool HasDescription => Description.Length > 0;
    public bool IsSecret => info.Secret;
    public bool IsBoolean => info.Type == "boolean";
    public bool IsChoice => info.Choices.Count > 0;
    public bool IsText => !IsBoolean && !IsChoice;
    public IReadOnlyList<string> Choices => info.Choices;
    public char SecretChar => IsSecret ? '•' : '\0';

    [ObservableProperty] private string _value = info.Value ?? "";
    [ObservableProperty] private bool _isOn = info.Value == "true";
    [ObservableProperty] private bool _isSaving;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = "";

    public bool HasError => Error.Length > 0;

    partial void OnIsOnChanged(bool value)
    {
        if (IsBoolean) _ = SaveAsync(value ? "true" : "false");
    }

    partial void OnValueChanged(string value)
    {
        if (IsChoice) _ = SaveAsync(value);
    }

    [RelayCommand] private Task Save() => SaveAsync(Value.Trim());

    private async Task SaveAsync(string value)
    {
        IsSaving = true;
        try { Error = await hub.SaveSettingAsync(plugin, Key, value) ?? ""; }
        finally { IsSaving = false; }
    }
}

/// <summary>
/// Settings → Plugins: one page for omp's plugins, MCP servers and skills, as tabs over one list (name, a line of what it
/// does, where it comes from, its switch, its details). The catalog shows what the configured marketplaces offer and
/// Smithery's MCP servers; the details page shows what an item brings, where from, its trust and its actions. The work
/// stays in <see cref="PluginsViewModel"/> and <see cref="ConnectorsViewModel"/>; this model only arranges them.
/// </summary>
public sealed partial class PluginsHubViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly HashSet<SkillRowViewModel> _looked = [];
    private IReadOnlyList<Ombra.Rpc.OmpProjectItem> _blocked = [];

    public PluginsHubViewModel(MainViewModel owner)
    {
        _owner = owner;
        Plugins = owner.Plugins;
        Connectors = owner.Connectors;
        Plugins.Plugins.CollectionChanged += OnListChanged;
        Plugins.Skills.CollectionChanged += OnListChanged;
        Connectors.Servers.CollectionChanged += OnListChanged;
        Connectors.PropertyChanged += OnConnectorsChanged;
        owner.SettingsCategoryShown += key =>
        {
            if (key == "plugins") Refresh();
        };
        Refresh();
    }

    public PluginsViewModel Plugins { get; }
    public ConnectorsViewModel Connectors { get; }

    // ── Tabs, views, search ──

    /// <summary>plugins | mcp | skills.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPluginsTab), nameof(IsMcpTab), nameof(IsSkillsTab), nameof(SearchPlaceholder), nameof(ShowNoMatches), nameof(ListTitle))]
    private string _tab = "plugins";

    /// <summary>list | catalog | details.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowList), nameof(ShowCatalog), nameof(ShowDetails))]
    private string _mode = "list";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    private string _search = "";

    /// <summary>The Add menu's "Plugin": the install field over the list.</summary>
    [ObservableProperty] private bool _isInstallOpen;

    /// <summary>Where omp looks for skills (the Skills tab's folders and sources), shown under the list.</summary>
    [ObservableProperty] private bool _isSkillSourcesOpen;

    /// <summary>The MCP tab's resources, prompts and option, shown under the list.</summary>
    [ObservableProperty] private bool _isMcpMoreOpen;

    public bool IsPluginsTab => Tab == "plugins";
    public bool IsMcpTab => Tab == "mcp";
    public bool IsSkillsTab => Tab == "skills";
    public bool ShowList => Mode == "list";
    public bool ShowCatalog => Mode == "catalog";
    public bool ShowDetails => Mode == "details";
    public bool IsSearching => Search.Trim().Length > 0;
    public string ListTitle => Tab switch { "mcp" => Loc.T("MCP"), "skills" => Loc.T("Skills"), _ => Loc.T("Plugins") };
    public string SearchPlaceholder => Tab switch { "mcp" => Loc.T("Search MCP servers"), "skills" => Loc.T("Search skills"), _ => Loc.T("Search plugins") };

    public ObservableCollection<PluginRowViewModel> ShownPlugins { get; } = [];
    public ObservableCollection<McpServerRowViewModel> ShownServers { get; } = [];
    public ObservableCollection<SkillRowViewModel> ShownSkills { get; } = [];

    /// <summary>The current tab's items omp left out of an untrusted folder.</summary>
    public ObservableCollection<BlockedItemViewModel> ShownBlocked { get; } = [];

    public int PluginsCount => Plugins.Plugins.Count + _blocked.Count(b => b.Kind == "plugin");
    public int McpCount => Connectors.Servers.Count + _blocked.Count(b => b.Kind == "mcp");
    public int SkillsCount => Plugins.Skills.Count + _blocked.Count(b => b.Kind == "skill");
    public bool HasShownPlugins => ShownPlugins.Count > 0;
    public bool HasShownServers => ShownServers.Count > 0;
    public bool HasShownSkills => ShownSkills.Count > 0;
    public bool HasShownBlocked => ShownBlocked.Count > 0;

    /// <summary>The search left nothing in a tab that has items.</summary>
    public bool ShowNoMatches => IsSearching && Tab switch
    {
        "mcp" => ShownServers.Count == 0 && ShownBlocked.Count == 0 && McpCount > 0,
        "skills" => ShownSkills.Count == 0 && ShownBlocked.Count == 0 && SkillsCount > 0,
        _ => ShownPlugins.Count == 0 && ShownBlocked.Count == 0 && PluginsCount > 0,
    };

    partial void OnSearchChanged(string value) => Sync();

    partial void OnTabChanged(string value) => Sync();

    [RelayCommand]
    private void ShowTab(string? tab)
    {
        if (tab is not ("plugins" or "mcp" or "skills")) return;
        Tab = tab;
        Mode = "list";
        ClearSelection();
    }

    [RelayCommand]
    private async Task OpenCatalog()
    {
        ClearSelection();
        Mode = "catalog";
        await Plugins.OpenAllMarketplacesAsync();
    }

    [RelayCommand]
    private void BackToList()
    {
        Mode = "list";
        ClearSelection();
    }

    [RelayCommand]
    private async Task RefreshAll()
    {
        await Task.WhenAll(Plugins.RefreshAsync(), Connectors.RefreshAsync());
        Refresh();
    }

    // ── Add ──

    [RelayCommand]
    private void AddPlugin()
    {
        ShowTab("plugins");
        IsInstallOpen = true;
    }

    [RelayCommand]
    private void AddMcpServer()
    {
        ShowTab("mcp");
        if (!Connectors.IsFormOpen) Connectors.OpenAddFormCommand.Execute(null);
    }

    [RelayCommand]
    private void AddSkill()
    {
        ShowTab("skills");
        IsSkillSourcesOpen = true;
        SkillFolderRequested?.Invoke();
    }

    [RelayCommand] private void CloseInstall() => IsInstallOpen = false;
    [RelayCommand] private void ToggleSkillSources() => IsSkillSourcesOpen = !IsSkillSourcesOpen;
    [RelayCommand] private void ToggleMcpMore() => IsMcpMoreOpen = !IsMcpMoreOpen;

    /// <summary>The view puts the caret in the skill folder field (Add → Skill).</summary>
    public event Action? SkillFolderRequested;

    /// <summary>Opens the page on a tab (the "+" menu, a sign-in prompt, the palette).</summary>
    internal void Show(string tab)
    {
        Tab = tab;
        Mode = "list";
        ClearSelection();
    }

    // ── Lists ──

    private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void OnConnectorsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Add → MCP server, or Edit on a server's page: the form is on the MCP tab's list
        if (e.PropertyName == nameof(ConnectorsViewModel.IsFormOpen) && Connectors.IsFormOpen && Mode != "list") Show("mcp");
    }

    /// <summary>Trust marks, the tabs' counts and the shown rows, after any list changed.</summary>
    internal void Refresh()
    {
        ReadTrust();
        Sync();
        foreach (var p in new[] { nameof(PluginsCount), nameof(McpCount), nameof(SkillsCount) }) OnPropertyChanged(p);
        _ = FindSkillFoldersAsync();
        // An item removed while its page was shown: back to the list
        if (Mode == "details" && (SelectedPlugin is { } p1 && !Plugins.Plugins.Contains(p1) || SelectedServer is { } s1 && !Connectors.Servers.Contains(s1)))
            BackToList();
    }

    /// <summary>What omp left out of the project folder this chat started in without trust.</summary>
    private void ReadTrust() => MarkTrust(_owner.Session.ProjectLaunch);

    /// <summary>Marks the project's items of a restricted start, and lists what omp left out that no list shows.</summary>
    internal void MarkTrust(ProjectLaunch? launch)
    {
        var restricted = launch?.Restricted == true;
        foreach (var p in Plugins.Plugins) p.NeedsTrust = restricted && p.IsProjectScope;
        foreach (var s in Connectors.Servers) s.NeedsTrust = restricted && s.IsProjectItem;
        var listed = new HashSet<string>(Plugins.Skills.Select(s => "skill:" + s.Name)
            .Concat(Connectors.Servers.Select(s => "mcp:" + s.Name))
            .Concat(Plugins.Plugins.Select(p => "plugin:" + p.Name)), StringComparer.Ordinal);
        _blocked = restricted
            ? [.. (launch!.LeftOut ?? []).Where(i => i.Kind is "skill" or "mcp" or "plugin" && !listed.Contains(i.Kind + ":" + i.Name))
                .DistinctBy(i => i.Kind + ":" + i.Name)]
            : [];
    }

    private bool Matches(string name, string description)
    {
        var q = Search.Trim();
        return q.Length == 0 || name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || description.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    private void Sync()
    {
        Fill(ShownPlugins, Plugins.Plugins.Where(p => Matches(p.Name, p.Description)));
        Fill(ShownServers, Connectors.Servers.Where(s => Matches(s.Name, s.Detail)));
        Fill(ShownSkills, Plugins.Skills.Where(s => Matches(s.Name, s.Description)));
        var kind = Tab switch { "mcp" => "mcp", "skills" => "skill", _ => "plugin" };
        ShownBlocked.Clear();
        foreach (var b in _blocked.Where(b => b.Kind == kind && Matches(b.Name, b.Source))) ShownBlocked.Add(new BlockedItemViewModel(b));
        foreach (var p in new[] { nameof(HasShownPlugins), nameof(HasShownServers), nameof(HasShownSkills), nameof(HasShownBlocked), nameof(ShowNoMatches) })
            OnPropertyChanged(p);
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear();
        foreach (var item in list) target.Add(item);
    }

    /// <summary>Each skill's folder, looked up once (its scope in the list, its source on its page).</summary>
    private async Task FindSkillFoldersAsync()
    {
        var skills = Plugins.Skills.Where(_looked.Add).ToList();
        if (skills.Count == 0) return;
        var folders = await _owner.FindSkillFoldersAsync([.. skills.Select(s => s.Name)]);
        foreach (var skill in skills)
        {
            skill.Folder = folders.GetValueOrDefault(skill.Name);
            skill.IsProject = skill.Folder is { } f && _owner.ProjectFolder is { } root && ProjectFiles.Relative(root, f) is { Length: > 0 };
        }
    }

    // ── Details ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPluginDetails))]
    private PluginRowViewModel? _selectedPlugin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerDetails))]
    private McpServerRowViewModel? _selectedServer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSkillDetails))]
    private SkillRowViewModel? _selectedSkill;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlockedDetails))]
    private BlockedItemViewModel? _selectedBlocked;

    public bool IsPluginDetails => SelectedPlugin is not null;
    public bool IsServerDetails => SelectedServer is not null;
    public bool IsSkillDetails => SelectedSkill is not null;
    public bool IsBlockedDetails => SelectedBlocked is not null;

    /// <summary>What the shown item brings (skills, commands, servers, code that runs).</summary>
    public ObservableCollection<PluginFactViewModel> Parts { get; } = [];

    /// <summary>Where it comes from: source, version, folder, scope, type.</summary>
    public ObservableCollection<PluginFactViewModel> Facts { get; } = [];

    /// <summary>The shown npm plugin's settings.</summary>
    public ObservableCollection<PluginSettingRowViewModel> PluginSettings { get; } = [];

    [ObservableProperty] private bool _isReadingDetails;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettingsError))]
    private string _pluginSettingsError = "";

    /// <summary>What running the item means on this computer (shown under Access).</summary>
    [ObservableProperty] private string _accessText = "";

    public bool HasParts => Parts.Count > 0;
    public bool HasPluginSettings => PluginSettings.Count > 0;
    public bool HasSettingsError => PluginSettingsError.Length > 0;
    public string BackLabel => ListTitle;

    private void ClearSelection()
    {
        SelectedPlugin = null;
        SelectedServer = null;
        SelectedSkill = null;
        SelectedBlocked = null;
        Parts.Clear();
        Facts.Clear();
        PluginSettings.Clear();
        PluginSettingsError = "";
        NotifyDetails();
    }

    private void NotifyDetails()
    {
        OnPropertyChanged(nameof(HasParts));
        OnPropertyChanged(nameof(HasPluginSettings));
        OnPropertyChanged(nameof(BackLabel));
    }

    private void Fact(string icon, string title, string? detail)
    {
        if (detail is { Length: > 0 }) Facts.Add(new PluginFactViewModel(icon, title, detail));
    }

    [RelayCommand]
    private async Task OpenPlugin(PluginRowViewModel? row)
    {
        if (row is null) return;
        Show("plugins");
        SelectedPlugin = row;
        Mode = "details";
        Fact("IconStore", Loc.T("Source"), row.IsMarketplace ? Loc.F("Marketplace {0}", row.SourceLabel) : Loc.T("npm package"));
        Fact("IconPackage", Loc.T("Version"), row.HasVersion ? row.Version : null);
        Fact("IconUser", Loc.T("Installed for"), row.IsProjectScope ? Loc.T("This project") : Loc.T("You, in every project"));
        Fact("IconFolder", Loc.T("Folder"), row.Info.Path is { } path ? GitProbe.Tilde(path) : null);
        AccessText = row.NeedsTrust
            ? Loc.T("This project's folder is not trusted, so omp leaves this plugin out. Trust the folder from the chat's card, or in Settings → Permissions.")
            : Loc.T("A plugin can run code on this computer when omp loads it: its tools, hooks and MCP servers. Install plugins only from sources you trust.");
        NotifyDetails();
        IsReadingDetails = true;
        try
        {
            var parts = await Task.Run(() => PluginParts.Read(row.Info));
            if (!ReferenceEquals(SelectedPlugin, row)) return;
            foreach (var part in parts) Parts.Add(part);
            if (!row.IsMarketplace)
            {
                var (settings, error) = await Plugins.ReadPluginSettingsAsync(row);
                if (!ReferenceEquals(SelectedPlugin, row)) return;
                PluginSettingsError = settings is null ? error : "";
                foreach (var s in settings ?? []) PluginSettings.Add(new PluginSettingRowViewModel(this, row, s));
            }
        }
        finally
        {
            IsReadingDetails = false;
            NotifyDetails();
        }
    }

    [RelayCommand]
    private void OpenServer(McpServerRowViewModel? row)
    {
        if (row is null) return;
        Show("mcp");
        SelectedServer = row;
        Mode = "details";
        Fact(row.IsRemote ? "IconGlobe" : "IconTerminal", row.IsRemote ? Loc.T("Address") : Loc.T("Runs"), row.Detail);
        Fact("IconUser", Loc.T("Available in"), row.ScopeTip);
        Fact("IconKey", Loc.T("Settings it gets"), row.Extras);
        AccessText = row.NeedsTrust
            ? Loc.T("This project's folder is not trusted, so omp doesn't start this server. Trust the folder from the chat's card, or in Settings → Permissions.")
            : row.IsRemote
                ? Loc.T("omp sends this server what its tools need, and the server's answers reach the model.")
                : Loc.T("omp starts this command on this computer, with your user's access.");
        NotifyDetails();
    }

    [RelayCommand]
    private async Task OpenSkill(SkillRowViewModel? row)
    {
        if (row is null) return;
        Show("skills");
        SelectedSkill = row;
        Mode = "details";
        Fact("IconSkills", Loc.T("Command"), row.Command);
        AccessText = Loc.T("A skill is instructions for the model; it runs nothing by itself. omp loads it when a task needs it.");
        NotifyDetails();
        row.Folder ??= await _owner.FindSkillFolderAsync(row.Name);
        if (!ReferenceEquals(SelectedSkill, row)) return;
        Fact("IconFolder", Loc.T("Folder"), row.Folder is { } f ? GitProbe.Tilde(f) : Loc.T("Built into omp, or from a plugin"));
    }

    [RelayCommand]
    private void OpenBlocked(BlockedItemViewModel? item)
    {
        if (item is null) return;
        SelectedBlocked = item;
        Mode = "details";
        Fact("IconFolder", Loc.T("File"), item.Source);
        AccessText = Loc.T("This item comes from the project's folder, which you haven't trusted, so omp leaves it out. Trust the folder from the chat's card (Trust and restart), or in Settings → Permissions.");
        NotifyDetails();
    }

    internal Task<string?> SaveSettingAsync(PluginRowViewModel plugin, string key, string value) => Plugins.SetPluginSettingAsync(plugin, key, value);

    // ── Catalog ──

    /// <summary>Installed plugin of a catalog row: its details page ("⋯" on an installed one).</summary>
    [RelayCommand]
    private Task OpenInstalled(MarketplacePluginRowViewModel? row) =>
        OpenPlugin(Plugins.Plugins.FirstOrDefault(p => p.IsMarketplace && p.Name == row?.Name && p.SourceLabel == row.MarketplaceName));
}

/// <summary>What a plugin's folder holds, read from disk: Claude Code style folders (skills/, commands/, agents/,
/// .mcp.json, hooks/) and an npm plugin's manifest (tools, hooks, extensions, commands).</summary>
internal static class PluginParts
{
    private const int Shown = 6;

    public static List<PluginFactViewModel> Read(InstalledPluginInfo info)
    {
        var parts = new List<PluginFactViewModel>();
        var dir = info.Path is { } p && Directory.Exists(p) ? p : null;
        var skills = dir is null ? [] : Folders(Path.Combine(dir, "skills")).Where(d => File.Exists(Path.Combine(d, "SKILL.md"))).Select(Path.GetFileName).OfType<string>().ToList();
        var commands = dir is null ? [] : Files(Path.Combine(dir, "commands"), "*.md").Select(f => "/" + Path.GetFileNameWithoutExtension(f)).ToList();
        var agents = dir is null ? [] : Files(Path.Combine(dir, "agents"), "*.md").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
        var servers = dir is null ? [] : McpServers(Path.Combine(dir, ".mcp.json"));
        var hooks = info.Manifest?.Hooks == true || dir is not null && File.Exists(Path.Combine(dir, "hooks", "hooks.json"));
        if (skills.Count > 0) parts.Add(new("IconSkills", Loc.Plural(skills.Count, "{0} skill", "{0} skills"), Names(skills)));
        var commandCount = Math.Max(commands.Count, info.Manifest?.Commands ?? 0);
        if (commandCount > 0) parts.Add(new("IconTerminal", Loc.Plural(commandCount, "{0} command", "{0} commands"), Names(commands)));
        if (agents.Count > 0) parts.Add(new("IconUsers", Loc.Plural(agents.Count, "{0} agent", "{0} agents"), Names(agents)));
        if (servers.Count > 0) parts.Add(new("IconPlug", Loc.Plural(servers.Count, "{0} MCP server", "{0} MCP servers"), Names(servers)));
        if (info.Manifest?.Tools == true) parts.Add(new("IconTools", Loc.T("Tools"), Loc.T("Code omp runs when the model calls them")));
        if (hooks) parts.Add(new("IconBolt", Loc.T("Hooks"), Loc.T("Code omp runs at moments of a session")));
        if (info.Manifest is { Extensions: > 0 } m) parts.Add(new("IconCode", Loc.Plural(m.Extensions, "{0} extension", "{0} extensions"), Loc.T("Code omp loads with the plugin")));
        return parts;
    }

    private static string Names(List<string> names) =>
        names.Count == 0 ? "" : string.Join(", ", names.Take(Shown)) + (names.Count > Shown ? " " + Loc.F("and {0} more", names.Count - Shown) : "");

    private static IEnumerable<string> Folders(string dir) => Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).Order(StringComparer.OrdinalIgnoreCase) : [];

    private static IEnumerable<string> Files(string dir, string pattern) => Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern).Order(StringComparer.OrdinalIgnoreCase) : [];

    private static List<string> McpServers(string file)
    {
        try
        {
            return File.Exists(file) && JsonNode.Parse(File.ReadAllText(file))?["mcpServers"] is JsonObject servers ? [.. servers.Select(kv => kv.Key)] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
}
