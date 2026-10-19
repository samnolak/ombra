using System.Diagnostics.CodeAnalysis;
using Ombra.ClientCore.Teams;

namespace Ombra.Teams.Tests;

/// <summary>
/// SD-10 key stores on Windows (DPAPI, CurrentUser) and Linux (Secret Service via libsecret). Each test runs only on its
/// OS (skipped elsewhere); native acceptance therefore needs a Windows and a Linux desktop session.
/// </summary>
public sealed class PlatformKeyStoreTests
{
    [Fact]
    [SuppressMessage("Interoperability", "CA1416", Justification = "Runs only on Windows: guarded and skipped elsewhere.")]
    public void Dpapi_round_trip_keeps_only_ciphertext_on_disk_and_binds_the_account()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows DPAPI");
            return;
        }
        var dir = Path.Combine(Path.GetTempPath(), "ombra-dpapi-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WindowsDpapiStore(dir);
            var secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            Assert.Null(store.Read("state-key/server"));
            store.Write("state-key/server", secret);
            Assert.Equal(secret, store.Read("state-key/server"));
            var file = Directory.GetFiles(dir).Single();
            var onDisk = File.ReadAllBytes(file);
            Assert.False(onDisk.AsSpan().IndexOf(secret) >= 0, "plaintext secret found in the DPAPI file");
            // The blob is bound to its account (entropy): copied under another account it does not open.
            File.Copy(file, Path.Combine(dir, Convert.ToHexStringLower("install-secret"u8.ToArray()) + ".dpapi"));
            Assert.Throws<KeyStoreUnavailableException>(() => store.Read("install-secret"));
            store.Delete("state-key/server");
            Assert.Null(store.Read("state-key/server"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    [SuppressMessage("Interoperability", "CA1416", Justification = "Runs only on Linux: guarded and skipped elsewhere.")]
    public void Secret_service_round_trip_or_fail_closed_without_a_keyring()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux Secret Service");
            return;
        }
        var account = "test/" + Guid.NewGuid().ToString("N");
        LinuxSecretStore store;
        try { store = new LinuxSecretStore(TeamsKeyStore.Service + ".test"); }
        catch (KeyStoreUnavailableException)
        {
            // No libsecret installed: fail closed is the required behaviour; nothing else to check here.
            return;
        }
        var secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        try
        {
            store.Write(account, secret);
        }
        catch (KeyStoreUnavailableException)
        {
            // No D-Bus session / no unlocked collection: the store refuses instead of falling back.
            Assert.Throws<KeyStoreUnavailableException>(() => store.Read(account));
            return;
        }
        Assert.Equal(secret, store.Read(account));
        store.Delete(account);
        Assert.Null(store.Read(account));
    }

    [Fact]
    [SuppressMessage("Interoperability", "CA1416", Justification = "Each branch is guarded by its OS check.")]
    public void Platform_selection_matches_the_os()
    {
        ITeamsKeyStore? store = null;
        try { store = TeamsKeyStore.ForCurrentPlatform(); }
        catch (KeyStoreUnavailableException) when (OperatingSystem.IsLinux()) { } // no libsecret: fail closed
        if (OperatingSystem.IsMacOS()) Assert.IsType<MacKeychain>(store);
        else if (OperatingSystem.IsWindows()) Assert.IsType<WindowsDpapiStore>(store);
        else if (OperatingSystem.IsLinux()) Assert.True(store is null or LinuxSecretStore);
    }

    [Fact]
    public void Linux_machine_identity_reads_machine_id_and_changes_the_install_id()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ombra-machine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a"), "0123456789abcdef0123456789abcdef\n");
            File.WriteAllText(Path.Combine(dir, "b"), "fedcba9876543210fedcba9876543210\n");
            var a = new LinuxMachineIdentity(Path.Combine(dir, "a"), Path.Combine(dir, "missing")).MachineId();
            var b = new LinuxMachineIdentity(Path.Combine(dir, "missing"), Path.Combine(dir, "b")).MachineId();
            var secret = new byte[32];
            Assert.NotEqual(InstallId.Derive(secret, a), InstallId.Derive(secret, b));
            Assert.Throws<KeyStoreUnavailableException>(() => new LinuxMachineIdentity(Path.Combine(dir, "x"), Path.Combine(dir, "y")).MachineId());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
