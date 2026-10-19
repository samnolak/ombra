using System.Security.Cryptography;
using System.Text;

namespace Ombra.ClientCore;

/// <summary>
/// Early application admission, before legacy data migration can create or move the settings directory. The OS owns
/// the mutex; there is no PID, timeout, or stale-lock deletion. Acquire and dispose on the same startup/UI thread.
/// DraftStore separately leases its on-disk journal for consumers that do not launch the application. Neither lease
/// locks omp's session files or excludes older application versions that do not implement this protocol.
/// </summary>
public sealed class ClientInstanceLease : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private ClientInstanceLease(Mutex mutex) => _mutex = mutex;

    public static string SettingsPath(string? explicitPath = null) => SessionCatalog.ResolveLinks(Path.GetFullPath(
        explicitPath ?? Environment.GetEnvironmentVariable(OmpRuntimeOptions.ConfigEnvVar) ?? OmpRuntimeOptions.DefaultConfigPath));

    /// <summary>Null means another process owns this configuration/journal. Other failures are not contention.</summary>
    public static ClientInstanceLease? TryAcquire(string settingsPath)
    {
        // Two configuration extensions may share the same .drafts.json, so key admission by the journal as well.
        var path = DraftStore.PathFor(settingsPath);
        var ancestor = Path.GetDirectoryName(path)!;
        while (!Directory.Exists(ancestor) && Path.GetDirectoryName(ancestor) is { } parent) ancestor = parent;
        var swapped = new string([.. ancestor.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c))]);
        if (swapped != ancestor && SessionCatalog.SamePath(ancestor, swapped)) path = path.ToUpperInvariant();
        var name = "Ombra.Config." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
        var mutex = new Mutex(false, name, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });
        try
        {
            try
            {
                if (mutex.WaitOne(0)) return new ClientInstanceLease(mutex);
            }
            catch (AbandonedMutexException) { return new ClientInstanceLease(mutex); }
            mutex.Dispose();
            return null;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _mutex.ReleaseMutex();
        _disposed = true;
        _mutex.Dispose();
    }
}
