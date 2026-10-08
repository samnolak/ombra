namespace OmpGui.ClientCore.Network;

/// <summary>
/// Settings › Advanced › Corporate network certificates: makes the omp engine (Bun) trust the certificate authorities
/// of a network that inspects HTTPS, by Bun's own environment variables only. Nothing happens unless the user set it up
/// (<see cref="OmpRuntimeOptions.CorporateTrust"/>), and every omp launch adds the variable here
/// (<see cref="OmpRuntimeOptions.ToLaunchSpec"/>, <see cref="OmpRuntimeOptions.ToTuiLaunchSpec"/>, and the CLI through it):
/// <list type="bullet">
/// <item><see cref="SystemMode"/> (macOS): <c>NODE_USE_SYSTEM_CA=1</c>, Bun then asks macOS (Keychain and its trust
/// settings) to evaluate a chain; Bun's own roots still apply.</item>
/// <item><see cref="PemMode"/> (every platform): <c>NODE_EXTRA_CA_CERTS</c> naming the app's own validated copy of a
/// file the user chose (<see cref="CorporateCaFile"/>).</item>
/// </list>
/// A variable the user set themselves (inherited environment or the settings' environment, a null value included) is
/// never replaced: then the app adds nothing and the page says what is set. Certificate verification is never turned off.
/// </summary>
public static class CorporateTrust
{
    public const string SystemMode = "system";
    public const string PemMode = "pem";

    public const string SystemCaVariable = "NODE_USE_SYSTEM_CA";
    public const string ExtraCaVariable = "NODE_EXTRA_CA_CERTS";
    public const string NodeOptionsVariable = "NODE_OPTIONS";
    public const string UseSystemCaFlag = "--use-system-ca";

    /// <summary>The app's folder (next to its settings file) and the bundle in it: the only file the feature writes.</summary>
    public const string FolderName = "network-trust";
    public const string BundleFileName = "corporate-ca.pem";

    /// <summary>Node's CA-store flags in <c>NODE_OPTIONS</c>: any of them is the user's own choice of store.</summary>
    public static readonly IReadOnlyList<string> CaStoreFlags = [UseSystemCaFlag, "--use-openssl-ca", "--use-bundled-ca"];

    /// <summary>The OS evaluates the chain: verified with Bun 1.4.2 on macOS only (Windows and Linux use the file mode).</summary>
    public static bool SystemModeSupported => OperatingSystem.IsMacOS();

    public static string BundlePathFor(string settingsPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, FolderName, BundleFileName);

    /// <summary>What the user's own environment sets about CA trust (the settings' environment wins over the inherited one).</summary>
    public static UserTrustSettings Inspect(IReadOnlyDictionary<string, string?> settingsEnvironment, Func<string, string?> inherited, string? ourBundle = null)
    {
        var systemCa = Lookup(settingsEnvironment, inherited, SystemCaVariable);
        var extraCa = Lookup(settingsEnvironment, inherited, ExtraCaVariable);
        // A copy of ours in the inherited environment (an app started from a process the app started) is not the user's
        if (extraCa is { InAppSettings: false } e && ourBundle is not null && SamePath(e.Value, ourBundle)) extraCa = null;
        UserCaSetting? flag = null;
        if (Lookup(settingsEnvironment, inherited, NodeOptionsVariable) is { Value: { } options } nodeOptions)
        {
            // The last flag wins in Node; any of them is a choice
            var found = options.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault(CaStoreFlags.Contains);
            if (found is not null) flag = nodeOptions with { Value = found };
        }
        return new UserTrustSettings(systemCa, flag, extraCa);
    }

    /// <summary>Adds the trust variable of the chosen mode to an omp launch's environment, unless the user set their own.</summary>
    public static void Apply(Dictionary<string, string?> environment, OmpRuntimeOptions options, Func<string, string?> inherited)
    {
        switch (options.CorporateTrust)
        {
            case SystemMode when SystemModeSupported:
                if (!Inspect(options.Environment, inherited).SetsSystemCa) environment[SystemCaVariable] = "1";
                break;
            case PemMode when options.CorporateTrustBundle is { } bundle && File.Exists(bundle):
                if (!Inspect(options.Environment, inherited, bundle).SetsExtraCa) environment[ExtraCaVariable] = bundle;
                break;
        }
    }

    private static UserCaSetting? Lookup(IReadOnlyDictionary<string, string?> settings, Func<string, string?> inherited, string variable)
    {
        foreach (var (key, value) in settings)
            if (string.Equals(key, variable, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return new UserCaSetting(variable, value, InAppSettings: true);
        return inherited(variable) is { Length: > 0 } v ? new UserCaSetting(variable, v, InAppSettings: false) : null;
    }

    private static bool SamePath(string? a, string b)
    {
        if (a is null) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

/// <summary>One variable the user set: in the app's settings environment (a null value: removed for omp) or inherited.</summary>
public sealed record UserCaSetting(string Variable, string? Value, bool InAppSettings)
{
    /// <summary>"NODE_USE_SYSTEM_CA=0 (in your environment)": for the settings page only, never for logs.</summary>
    public string Describe() =>
        (Variable == CorporateTrust.NodeOptionsVariable ? $"NODE_OPTIONS contains {Value}"
            : Value is null ? Variable + " is removed"
            : Variable == CorporateTrust.ExtraCaVariable ? Variable + " is set"
            : $"{Variable}={Value}")
        + (InAppSettings ? " (in the app's settings environment)" : " (in the environment the app was started with)");
}

/// <summary>The user's own CA-trust variables, as <see cref="CorporateTrust.Inspect"/> found them.</summary>
public sealed record UserTrustSettings(UserCaSetting? SystemCa, UserCaSetting? NodeOptionsFlag, UserCaSetting? ExtraCa)
{
    /// <summary>The user chose a CA store themselves: the app adds no <c>NODE_USE_SYSTEM_CA</c>.</summary>
    public bool SetsSystemCa => SystemCa is not null || NodeOptionsFlag is not null;

    public bool SetsExtraCa => ExtraCa is not null;

    /// <summary>A choice other than "use the system's store" (e.g. <c>NODE_USE_SYSTEM_CA=0</c>, <c>--use-bundled-ca</c>).</summary>
    public bool SystemCaConflict =>
        SystemCa is { } s && s.Value != "1" || NodeOptionsFlag is { } f && f.Value != CorporateTrust.UseSystemCaFlag;

    /// <summary>The user's own settings already make Bun use the system's store.</summary>
    public bool SystemCaAlreadyOn => SetsSystemCa && !SystemCaConflict;

    public IEnumerable<UserCaSetting> SystemCaSettings => new[] { SystemCa, NodeOptionsFlag }.OfType<UserCaSetting>();
}
