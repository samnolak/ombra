using OmpGui.ClientCore;
using OmpGui.ClientCore.Network;

namespace OmpGui.App.ViewModels;

/// <summary>Settings › Advanced › Network: corporate certificates (<see cref="NetworkTrustViewModel"/>) and network
/// privacy (<see cref="NetworkPrivacyViewModel"/>): what they need from the window.</summary>
public sealed partial class MainViewModel
{
    private NetworkTrustViewModel? _networkTrust;
    private NetworkPrivacyViewModel? _privacySettings;

    public NetworkTrustViewModel NetworkTrust => _networkTrust ??= new NetworkTrustViewModel(this);

    public NetworkPrivacyViewModel PrivacySettings => _privacySettings ??= new NetworkPrivacyViewModel(this);

    /// <summary>The provider gate and strict network privacy every omp start goes through (null in tests that start omp directly).</summary>
    public NetworkPrivacy? Privacy { get; init; }

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
