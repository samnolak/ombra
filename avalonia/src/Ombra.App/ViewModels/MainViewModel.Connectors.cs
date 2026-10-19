using CommunityToolkit.Mvvm.Input;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>Settings → Plugins (plugins, MCP servers and skills on tabs), and the menu entries that open it on a tab.</summary>
public partial class MainViewModel
{
    private ConnectorsViewModel? _connectors;
    private PluginsViewModel? _plugins;
    private PluginsHubViewModel? _pluginsHub;

    /// <summary>The Plugins page's MCP tab (omp's MCP servers). Created with the settings page.</summary>
    public ConnectorsViewModel Connectors => _connectors ??= new ConnectorsViewModel(this);

    /// <summary>The Plugins page's Plugins and Skills tabs.</summary>
    public PluginsViewModel Plugins => _plugins ??= new PluginsViewModel(this);

    /// <summary>The Plugins page: its tabs, search, catalog and details pages.</summary>
    public PluginsHubViewModel PluginsHub => _pluginsHub ??= new PluginsHubViewModel(this);

    /// <summary>The Plugins page on its MCP tab (the "+" menu's MCP servers, a sign-in prompt).</summary>
    [RelayCommand] private Task OpenConnectorsPage() => OpenPluginsTabAsync("mcp");

    /// <summary>The Plugins page on its Plugins tab (the rail, the "+" menu).</summary>
    [RelayCommand] private Task OpenPluginsPage() => OpenPluginsTabAsync("plugins");

    /// <summary>The Plugins page on its Skills tab.</summary>
    [RelayCommand] private Task OpenSkillsPage() => OpenPluginsTabAsync("skills");

    private Task OpenPluginsTabAsync(string tab)
    {
        PluginsHub.Show(tab);
        return OpenSettingsAtAsync("plugins");
    }

    /// <summary>omp's current command catalog (builtins, skills, MCP prompts…) as the session last reported it.</summary>
    internal IReadOnlyList<SlashCommand> OmpCommandCatalog => Session.Snapshot().Commands;

    /// <summary>Puts a command at the start of the message box and returns to the conversation ("Use" on a skill).</summary>
    internal void UseInComposer(string command)
    {
        var rest = ComposerText.TrimStart();
        IsSettingsOpen = false;
        ComposerText = rest.Length == 0 ? command : command + rest;
        ComposerCaretIndex = ComposerText.Length;
        FocusComposerRequested?.Invoke();
    }

    /// <summary>omp's own terminal UI for what it offers only there (OAuth sign-in, Smithery), shown over the conversation.</summary>
    internal void OpenOmpTerminalFromSettings()
    {
        IsSettingsOpen = false;
        OpenOmpTuiCommand.Execute(null);
    }
}
