using System.Text.Json;

namespace OmpGui.ClientCore;

/// <summary>
/// omp's pinned sessions (<c>session-pins.json</c> in its agent folder, next to <c>sessions/</c>): a JSON array of
/// session ids. omp's own <c>/pin</c> and resume picker use the same file, so a pin made in the sidebar is a pin in omp
/// too. Written as omp writes it (omp 18.x <c>toggleSessionPin</c>): read, change and replace atomically under the
/// advisory lock on <c>session-pins.json.lock</c> (flock on macOS and the BSDs, which .NET takes for
/// <see cref="FileShare.None"/>; elsewhere the atomic replace still keeps the file whole).
/// </summary>
public static class SessionPins
{
    public const string FileName = "session-pins.json";

    /// <summary>The pins file for the sessions folder that holds <paramref name="sessionFile"/>, or null.</summary>
    public static string? PathFor(string? sessionFile) =>
        SessionCatalog.SessionsRootOf(sessionFile) is { } root && Path.GetDirectoryName(root) is { } agentDir
            ? Path.Combine(agentDir, FileName)
            : null;

    /// <summary>The pinned session ids; empty when the file is missing or unreadable (as omp treats it).</summary>
    public static HashSet<string> Read(string? pinsFile)
    {
        if (pinsFile is null) return [];
        try
        {
            if (!File.Exists(pinsFile)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(pinsFile));
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? [.. doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    /// <summary>Pins or unpins <paramref name="sessionId"/>; returns the ids pinned afterwards.</summary>
    /// <exception cref="IOException">The lock stayed taken or the file could not be written.</exception>
    public static HashSet<string> Set(string pinsFile, string sessionId, bool pinned)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pinsFile)!);
        using var _ = AcquireLock(pinsFile + ".lock");
        var list = ReadOrdered(pinsFile);
        if (pinned == list.Contains(sessionId)) return [.. list];
        if (pinned) list.Add(sessionId);
        else list.RemoveAll(id => id == sessionId);
        var tmp = $"{pinsFile}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(list));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, pinsFile, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return [.. list];
    }

    /// <summary>The ids in the file's order (omp keeps insertion order).</summary>
    private static List<string> ReadOrdered(string pinsFile)
    {
        try
        {
            if (!File.Exists(pinsFile)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(pinsFile));
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? [.. doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Distinct()]
                : [];
        }
        catch (JsonException) { return []; } // a corrupt file reads as empty in omp too: it is replaced
    }

    /// <summary>The lock omp takes (50 tries, 100 ms apart, as omp's default).</summary>
    private static FileStream AcquireLock(string lockFile)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 49) { Thread.Sleep(100); }
        }
    }
}
