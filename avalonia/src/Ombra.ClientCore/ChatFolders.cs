namespace Ombra.ClientCore;

/// <summary>
/// Chats that belong to no project. omp always works in a folder, so each such chat gets a fresh, empty one of its own
/// under <see cref="Root"/> (never the home folder: the agent would roam it and the Files pane would list it). The sidebar
/// lists every chat whose folder is directly under the root together, as "Chats", rather than as projects named after a
/// random folder.
/// </summary>
public sealed class ChatFolders
{
    /// <summary>Overrides <see cref="DefaultRoot"/> (tests keep their chats out of the user's data folder).</summary>
    public const string RootEnvVar = "OMBRA_CHATS_DIR";

    /// <summary>Under the app's machine-local data folder (macOS: ~/Library/Application Support/Ombra/chats), beside the
    /// runtimes; never the omp profile or the client settings.</summary>
    public static string DefaultRoot =>
        Environment.GetEnvironmentVariable(RootEnvVar) is { Length: > 0 } root ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Ombra", "chats");

    /// <summary>Where OMP GUI (this app's former name) made chats: the folder moved to <see cref="DefaultRoot"/> and a
    /// link with the old name points there, but omp recorded those chats' folders by the old path. Null when
    /// <see cref="RootEnvVar"/> places the chats elsewhere.</summary>
    public static string? LegacyDefaultRoot =>
        Environment.GetEnvironmentVariable(RootEnvVar) is { Length: > 0 } ? null
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "OmpGui", "chats");

    public ChatFolders(string root, string? legacyRoot = null)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        LegacyRoot = legacyRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyRoot));
    }

    public string Root { get; }

    /// <summary>A second root whose chats count too (<see cref="LegacyDefaultRoot"/>); new chats go to <see cref="Root"/>.</summary>
    public string? LegacyRoot { get; }

    /// <summary>A new empty folder for one chat. Named by the time it was made, so the folders sort by age.</summary>
    /// <exception cref="IOException">The folder could not be made.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder could not be made.</exception>
    public string Create()
    {
        var dir = Path.Combine(Root, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Whether <paramref name="cwd"/> is a chat's folder (directly under the root; omp records it with links
    /// resolved, so the comparison resolves them too).</summary>
    public bool Contains(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return false;
        try
        {
            return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd))) is { } parent
                && (SessionCatalog.PathComparer.Equals(parent, Root) || LegacyRoot is not null && SessionCatalog.PathComparer.Equals(parent, LegacyRoot));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes a chat's folder that holds nothing (the chat was left before anything was said or made in it), with the
    /// empty folder omp keeps for that chat's sessions (<paramref name="sessionFile"/>'s, made when omp starts, while the
    /// file itself is written only with the first reply). Anything in either folder keeps both: a session or a file the
    /// agent made is never lost. True when the chat's folder is gone.
    /// </summary>
    public bool DeleteIfUnused(string? folder, string? sessionFile)
    {
        if (!Contains(folder) || !Directory.Exists(folder)) return false;
        try
        {
            var sessions = sessionFile is null ? null : Path.GetDirectoryName(sessionFile);
            if (sessions is not null && Directory.Exists(sessions) && Directory.EnumerateFileSystemEntries(sessions).Any()) return false;
            if (Directory.EnumerateFileSystemEntries(folder!).Any()) return false;
            Directory.Delete(folder!);
            if (sessions is not null && Directory.Exists(sessions)) Directory.Delete(sessions);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // something appeared meanwhile, or the folder is in use: it stays
        }
    }
}
