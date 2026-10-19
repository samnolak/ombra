using Ombra.ClientCore;
using Ombra.ClientCore.Network;

namespace Ombra.App.ViewModels;

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

    /// <summary>Restarts the shared engine once every logical session is idle.</summary>
    internal Task<bool> RestartEnginesAsync() => RestartAllSessionsAsync();

    /// <summary>All open chats wait together for a pending process-wide settings change.</summary>
    internal int PendingEngineRestarts => _engineRestartPending ? _opens.Count : 0;

    /// <summary>omp terminal tabs already open: they keep the environment they started with.</summary>
    internal int OpenOmpTerminals => Terminals.Count(t => t.Kind == TerminalKind.OmpTui);

    /// <summary>The current model's provider address as omp listed it (no key or header is read); null when unknown.</summary>
    internal string? CurrentModelBaseUrl => _allModels.FirstOrDefault(m => m.Key == _open.Last?.Model)?.BaseUrl;
}
