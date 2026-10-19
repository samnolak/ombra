namespace Ombra.ClientCore.Teams;

/// <summary>
/// The OS key store for team secrets (SERVER_DESIGN §2.1 SD-10): the 32-byte key of the encrypted ombra-core state and
/// the install secret. Never synchronised to other machines; there is no plaintext fallback — when the store is
/// unavailable, team sign-in is unavailable (<see cref="KeyStoreUnavailableException"/>).
/// </summary>
public interface ITeamsKeyStore
{
    /// <summary>The secret, or null when there is no item.</summary>
    byte[]? Read(string account);

    /// <summary>Creates or replaces the item (non-synchronisable, this device only).</summary>
    void Write(string account, ReadOnlySpan<byte> secret);

    void Delete(string account);
}

/// <summary>The OS key store failed or does not exist on this platform. Team features stay off; nothing is written to disk.</summary>
public sealed class KeyStoreUnavailableException(string message, int status = 0) : Exception(message)
{
    /// <summary>Platform status code (macOS OSStatus), 0 when not applicable.</summary>
    public int Status { get; } = status;
}

public static class TeamsKeyStore
{
    public const string Service = "app.ombra.teams";
    /// <summary>
    /// The platform store: macOS Keychain, Windows DPAPI (CurrentUser), Linux Secret Service (libsecret). Anything
    /// else fails closed. Construction itself throws <see cref="KeyStoreUnavailableException"/> when the store is absent.
    /// </summary>
    public static ITeamsKeyStore ForCurrentPlatform()
    {
        if (OperatingSystem.IsMacOS()) return new MacKeychain(Service);
        if (OperatingSystem.IsWindows()) return new WindowsDpapiStore(WindowsDpapiStore.DefaultDirectory);
        if (OperatingSystem.IsLinux()) return new LinuxSecretStore(Service);
        throw new KeyStoreUnavailableException("no supported OS key store on this platform");
    }
}
