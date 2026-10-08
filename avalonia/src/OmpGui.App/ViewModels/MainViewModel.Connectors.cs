using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>The Connectors and Plugins and skills settings pages, and the composer's "+" menu entries that open them.</summary>
public partial class MainViewModel
{
    private ConnectorsViewModel? _connectors;
    private PluginsViewModel? _plugins;

    /// <summary>Settings → Connectors (omp's MCP servers). Created with the settings page.</summary>
    public ConnectorsViewModel Connectors => _connectors ??= new ConnectorsViewModel(this);

    /// <summary>Settings → Plugins and skills.</summary>
    public PluginsViewModel Plugins => _plugins ??= new PluginsViewModel(this);

    [RelayCommand] private Task OpenConnectorsPage() => OpenSettingsAtAsync("connectors");
    [RelayCommand] private Task OpenPluginsPage() => OpenSettingsAtAsync("plugins");

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
