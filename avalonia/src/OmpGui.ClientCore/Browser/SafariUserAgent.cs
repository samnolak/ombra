namespace OmpGui.ClientCore.Browser;

/// <summary>
/// The user agent of the macOS browser pane: WKWebView's own, plus the <c>Version/… Safari/605.1.15</c> token Safari adds
/// and WKWebView leaves out (set as WebKit's <c>applicationNameForUserAgent</c>, so WebKit still writes the platform
/// part). The result is Safari's user agent byte for byte; sites see the engine that really runs (BROWSER_PLAN decision 5,
/// <c>browser-stealth.md</c> §4.6).
/// </summary>
public static class SafariUserAgent
{
    /// <summary>The token Safari puts after the engine part (its WebKit version is frozen at 605.1.15).</summary>
    public const string SafariToken = "Safari/605.1.15";

    /// <summary>
    /// "Version/&lt;v&gt; Safari/605.1.15". <c>&lt;v&gt;</c> is Safari's version when the WebKit loaded in this process is
    /// Safari's own (Safari updates ship a staged WebKit every app's WKWebView loads; same <c>CFBundleVersion</c>), else
    /// the Safari version that came with this macOS: from macOS 26 on, the same numbers; macOS 14 and 15 shipped Safari
    /// 17 and 18 with the same minor version.
    /// </summary>
    /// <param name="safariVersion">Safari.app's <c>CFBundleShortVersionString</c> ("27.0"), null when not readable.</param>
    /// <param name="safariBuild">Safari.app's <c>CFBundleVersion</c> ("21625.1.29.18.28").</param>
    /// <param name="webKitBuild">The loaded WebKit.framework's <c>CFBundleVersion</c>.</param>
    /// <param name="os">The macOS version.</param>
    public static string ApplicationName(string? safariVersion, string? safariBuild, string? webKitBuild, Version os) =>
        $"Version/{VersionFor(safariVersion, safariBuild, webKitBuild, os)} {SafariToken}";

    internal static string VersionFor(string? safariVersion, string? safariBuild, string? webKitBuild, Version os)
    {
        if (!string.IsNullOrWhiteSpace(safariVersion) && !string.IsNullOrWhiteSpace(safariBuild)
            && string.Equals(safariBuild.Trim(), webKitBuild?.Trim(), StringComparison.Ordinal)
            && IsVersion(safariVersion.Trim()))
            return safariVersion.Trim();
        var major = os.Major switch
        {
            14 => 17,
            15 => 18,
            _ => os.Major,
        };
        return $"{major}.{Math.Max(0, os.Minor)}";
    }

    private static bool IsVersion(string s) => s.Length <= 16 && s.All(c => char.IsAsciiDigit(c) || c == '.') && char.IsAsciiDigit(s[0]);
}
