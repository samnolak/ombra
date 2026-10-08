using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>Settings › Advanced › Corporate network certificates (<see cref="NetworkTrustViewModel"/>): what it needs from the window.</summary>
public sealed partial class MainViewModel
{
    private NetworkTrustViewModel? _networkTrust;

    public NetworkTrustViewModel NetworkTrust => _networkTrust ??= new NetworkTrustViewModel(this);

    internal ClientSettingsStore? SettingsStore => _settings;

    /// <summary>Every chat's omp starts again with the changed settings: idle ones now, working ones after their run.</summary>
    internal Task<bool> RestartEnginesAsync() => RestartAllSessionsAsync();

    /// <summary>Chats whose omp restarts once its run ends (<see cref="OpenSession.RestartWhenIdle"/>).</summary>
    internal int PendingEngineRestarts => _opens.Values.Count(o => o.RestartWhenIdle);

    /// <summary>omp terminal tabs already open: they keep the environment they started with.</summary>
    internal int OpenOmpTerminals => Terminals.Count(t => t.Kind == TerminalKind.OmpTui);

    /// <summary>The current model's provider address as omp listed it (no key or header is read); null when unknown.</summary>
    internal string? CurrentModelBaseUrl => _allModels.FirstOrDefault(m => m.Key == _open.Last?.Model)?.BaseUrl;
}
