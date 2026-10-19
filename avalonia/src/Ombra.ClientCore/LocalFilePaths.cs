namespace Ombra.ClientCore;

/// <summary>Lexical gate for agent-supplied local file references. Never probes the file system.</summary>
public static class LocalFilePaths
{
    /// <summary>Resolves local paths only, checking before URI and OS normalization can hide a network namespace.</summary>
    public static string? Resolve(string? root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('`', '"', '\'');
        try
        {
            if (p.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                // Strip only the empty authority of file:///..., before URI dot-segment normalization.
                var uriPath = Uri.UnescapeDataString(p[5..]).Replace('\\', '/');
                if (uriPath.StartsWith("///", StringComparison.Ordinal)) uriPath = uriPath[2..];
                if (!IsLocal(uriPath)) return null;
                if (!Uri.TryCreate(p, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc || uri.Host.Length != 0)
                    return null;
                p = uri.LocalPath;
            }
            if (!IsLocal(p)) return null;
            if (p.StartsWith("~/", StringComparison.Ordinal) || p.StartsWith("~\\", StringComparison.Ordinal) || p == "~")
                p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Length > 2 ? p[2..] : "");
            if (!Path.IsPathRooted(p))
            {
                if (string.IsNullOrWhiteSpace(root) || !IsLocal(root)) return null;
                p = Path.Combine(root, p);
            }
            if (!IsLocal(p)) return null;
            var full = Path.GetFullPath(p);
            return IsLocal(full) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>Checks Windows namespaces on every OS, including mixed separators and URI-escaped spellings.</summary>
    public static bool IsLocal(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0) return false;
        // Only classify decoded text; ordinary paths containing '%' keep their literal file name when resolved.
        var decoded = Uri.UnescapeDataString(path).Replace('\\', '/');
        if (decoded.StartsWith("//", StringComparison.Ordinal) || decoded.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return false;
        return !decoded.StartsWith("/??/", StringComparison.Ordinal)
            && !decoded.StartsWith("/Device/", StringComparison.OrdinalIgnoreCase)
            && !decoded.StartsWith("/GLOBAL??/", StringComparison.OrdinalIgnoreCase)
            && !decoded.StartsWith("/GLOBALROOT/", StringComparison.OrdinalIgnoreCase)
            && !decoded.StartsWith("/DosDevices/", StringComparison.OrdinalIgnoreCase)
            && !decoded.StartsWith("/SystemRoot/", StringComparison.OrdinalIgnoreCase);
    }
}
