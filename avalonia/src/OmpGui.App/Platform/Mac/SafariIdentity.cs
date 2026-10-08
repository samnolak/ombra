using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OmpGui.ClientCore.Browser;

namespace OmpGui.App.Platform.Mac;

/// <summary>
/// What makes the pane's user agent Safari's (<see cref="SafariUserAgent"/>): Safari.app's version and build, read from
/// its Info.plist (readable without any permission), and the build of the WebKit loaded in this process. Read once.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class SafariIdentity
{
    private const string SafariInfoPlist = "/Applications/Safari.app/Contents/Info.plist";
    private const string WebKitFramework = "/System/Library/Frameworks/WebKit.framework/WebKit";

    private static readonly Lazy<string> Cached = new(Compute);

    /// <summary>"Version/27.0 Safari/605.1.15" (see <see cref="SafariUserAgent.ApplicationName"/>).</summary>
    public static string ApplicationNameForUserAgent => Cached.Value;

    /// <summary>The inputs of the rule, for the harness report.</summary>
    internal static (string? SafariVersion, string? SafariBuild, string? WebKitBuild) Inputs()
    {
        var pool = MacObjC.PoolPush();
        try
        {
            var (version, build) = Safari();
            return (version, build, LoadedWebKitBuild());
        }
        finally
        {
            MacObjC.PoolPop(pool);
        }
    }

    private static string Compute()
    {
        try
        {
            var (version, build, webKit) = Inputs();
            return SafariUserAgent.ApplicationName(version, build, webKit, System.Environment.OSVersion.Version);
        }
        catch (Exception)
        {
            return SafariUserAgent.ApplicationName(null, null, null, System.Environment.OSVersion.Version);
        }
    }

    private static (string? Version, string? Build) Safari()
    {
        var info = MacObjC.Send(MacObjC.Cls("NSDictionary"), "dictionaryWithContentsOfFile:", MacObjC.Str(SafariInfoPlist));
        if (info == 0) return (null, null);
        return (Value(info, "CFBundleShortVersionString"), Value(info, "CFBundleVersion"));
    }

    /// <summary><c>[[NSBundle bundleForClass:WKWebView.class] objectForInfoDictionaryKey:@"CFBundleVersion"]</c>: the WebKit actually loaded (Safari's staged one after a Safari update).</summary>
    private static string? LoadedWebKitBuild()
    {
        NativeLibrary.Load(WebKitFramework);
        var cls = MacObjC.Cls("WKWebView");
        if (cls == 0) return null;
        var bundle = MacObjC.Send(MacObjC.Cls("NSBundle"), "bundleForClass:", cls);
        return bundle == 0 ? null : StringOrNull(MacObjC.Send(bundle, "objectForInfoDictionaryKey:", MacObjC.Str("CFBundleVersion")));
    }

    private static string? Value(nint dictionary, string key) =>
        StringOrNull(MacObjC.Send(dictionary, "objectForKey:", MacObjC.Str(key)));

    private static string? StringOrNull(nint value) => MacObjC.IsKindOf(value, "NSString") ? MacObjC.ToStr(value) : null;
}
