using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OmpGui.App.Services.Runtime;
using OmpGui.ClientCore;

namespace OmpGui.App.Services;

/// <summary>
/// A zip the user can attach to a bug report: versions, platform, the client settings with every environment value
/// and API-key argument removed, the omp runtime record and install log, the state of the omp session, event types
/// with timestamps and the last error. No conversation text, no event contents. Every entry passes through
/// <see cref="SecretRedactor"/> (known values from the settings + credential patterns; the home folder becomes ~).
/// </summary>
public sealed class DiagnosticsBundle
{
    public required string SettingsPath { get; init; }
    public SessionSnapshot? Session { get; init; }
    public RuntimeInstaller? Installer { get; init; }
    public string? StartupLogPath { get; init; }
    public string Home { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);

    public static readonly IReadOnlyList<string> Entries = ["about.txt", "settings.json", "runtime.txt", "session.txt", "install.log", "startup.log"];

    public async Task WriteAsync(Stream output, CancellationToken ct = default)
    {
        var (settingsText, secrets) = ReadSettings();
        var redactor = new SecretRedactor(secrets, Home);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        async Task Add(string name, string? text)
        {
            if (text is null) return;
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            await using var s = entry.Open();
            await s.WriteAsync(Encoding.UTF8.GetBytes(redactor.Redact(text)), ct).ConfigureAwait(false);
        }

        await Add("about.txt", About()).ConfigureAwait(false);
        await Add("settings.json", settingsText).ConfigureAwait(false);
        await Add("runtime.txt", RuntimeText()).ConfigureAwait(false);
        await Add("session.txt", SessionText()).ConfigureAwait(false);
        if (Installer?.FindInstalled() is { } rt) await Add("install.log", ReadTail(Path.Combine(rt.Directory, "install.log"), 200_000)).ConfigureAwait(false);
        if (StartupLogPath is not null) await Add("startup.log", ReadTail(StartupLogPath, 200_000)).ConfigureAwait(false);
    }

    private string About()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OMP GUI {typeof(DiagnosticsBundle).Assembly.GetName().Version} ({InformationalVersion()})");
        sb.AppendLine($"created   {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"os        {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"process   {RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}");
        sb.AppendLine($"platform  {Platform.RuntimePlatform.Key(out _) ?? "no pinned runtime build"}");
        sb.AppendLine($"settings  {SettingsPath}");
        sb.AppendLine($"runtimes  {RuntimePack.DefaultRoot}");
        return sb.ToString();
    }

    private static string InformationalVersion() =>
        typeof(DiagnosticsBundle).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";

    /// <summary>
    /// The settings file with every secret replaced; those values are also returned as known secrets for the other
    /// entries. Secret: anything under an <c>environment</c> key or a key-named field (<c>apiKey</c>, <c>token</c>…), at any
    /// depth and in any shape (object, list, one string, nested), and the value of a credential flag in any argument list.
    /// A hand-edited file of an unexpected shape is masked the same way; one that does not parse is left out.
    /// </summary>
    private (string? Text, List<string> Secrets) ReadSettings()
    {
        var secrets = new List<string>();
        if (!File.Exists(SettingsPath)) return ("(no settings file: defaults in use)", secrets);
        var raw = File.ReadAllText(SettingsPath);
        try
        {
            var node = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return (Masked(node, false, secrets)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null", secrets);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            // Not parseable (or a duplicate key, found while reading it): not included at all (it could hold anything).
            // Its environment values are still secrets for the other entries (a log may echo one), so every quoted
            // value in it is treated as one.
            secrets.Clear();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(raw, "\"((?:[^\"\\\\]|\\\\.)*)\""))
                AddSecret(secrets, m.Groups[1].Value);
            return ($"(settings file does not parse: {e.Message}; not included)", secrets);
        }
    }

    /// <summary>A copy of <paramref name="node"/> with secret values replaced by the mask (collected into <paramref name="secrets"/>).</summary>
    private static JsonNode? Masked(JsonNode? node, bool secret, List<string> secrets)
    {
        switch (node)
        {
            case JsonObject o:
                var obj = new JsonObject();
                foreach (var (key, value) in o) obj[key] = Masked(value, secret || IsSecretName(key), secrets);
                return obj;
            case JsonArray a:
                var list = new JsonArray();
                for (var i = 0; i < a.Count; i++)
                {
                    // The value of a credential flag: after it (--api-key VALUE) or joined to it (--api-key=VALUE).
                    var afterFlag = i > 0 && Str(a[i - 1]) is { } prev && IsSecretFlag(prev) && !prev.Contains('=');
                    if (!secret && Str(a[i]) is { } arg && IsSecretFlag(arg) && arg.IndexOf('=') is var eq and > 0)
                    {
                        AddSecret(secrets, arg[(eq + 1)..]);
                        list.Add(arg[..(eq + 1)] + SecretRedactor.Mask);
                    }
                    else list.Add(Masked(a[i], secret || afterFlag, secrets));
                }
                return list;
            case JsonValue v when secret && v.GetValueKind() != JsonValueKind.Null:
                AddSecret(secrets, v.ToString());
                return SecretRedactor.Mask;
            default:
                return node?.DeepClone();
        }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.ToString() : null;

    /// <summary>A secret value, and for <c>NAME=value</c> also the value alone (a log echoes that).</summary>
    private static void AddSecret(List<string> secrets, string value)
    {
        secrets.Add(value);
        if (value.IndexOf('=') is var eq and > 0 && eq < value.Length - 1) secrets.Add(value[(eq + 1)..]);
    }

    /// <summary><c>environment</c> / <c>env</c>, or a field named like a credential (<c>apiKey</c>, <c>token</c>, <c>clientSecret</c>…).</summary>
    private static bool IsSecretName(string key)
    {
        var k = key.Replace("_", "").Replace("-", "").ToLowerInvariant();
        return k is "environment" or "env" || k.EndsWith("key", StringComparison.Ordinal) || k.EndsWith("keys", StringComparison.Ordinal)
            || k.Contains("token") || k.Contains("secret") || k.Contains("password") || k.Contains("credential");
    }

    /// <summary><c>--api-key</c>, <c>--token</c>, <c>--auth-token=…</c>, <c>--client-secret</c>, <c>--password</c>, <c>--x-key</c>…</summary>
    private static bool IsSecretFlag(string arg)
    {
        if (!arg.StartsWith("--", StringComparison.Ordinal)) return false;
        var name = (arg.IndexOf('=') is var eq and > 0 ? arg[2..eq] : arg[2..]).ToLowerInvariant();
        return name.Contains("key") || name.Contains("token") || name.Contains("secret") || name.Contains("password") || name.Contains("credential");
    }

    private string RuntimeText()
    {
        if (Installer is null) return "runtime installer: not available";
        var sb = new StringBuilder($"pinned pack {RuntimePack.Name}, platform {Installer.Platform ?? "unsupported"}\n");
        if (Installer.FindInstalled() is { } rt)
        {
            sb.AppendLine($"installed at {rt.Directory}");
            sb.AppendLine(ReadTail(Path.Combine(rt.Directory, "installed.json"), 10_000));
        }
        else sb.AppendLine("not installed");
        return sb.ToString();
    }

    private string SessionText()
    {
        if (Session is not { } s) return "no session";
        var sb = new StringBuilder();
        sb.AppendLine($"phase {s.Phase}; start failed {s.StartFailed} ({s.StartProblem}); protocol v{s.ProtocolVersion}; model {s.Model ?? "none"}");
        sb.AppendLine($"approval mode {s.ApprovalMode ?? "?"}; thinking {s.ThinkingLevel ?? "?"}; context {s.ContextPercent?.ToString("0", System.Globalization.CultureInfo.InvariantCulture) ?? "?"}%");
        sb.AppendLine($"frames {s.FramesReceived}, bytes {s.BytesReceived}, messages {s.MessagesCompleted}, stream mismatches {s.StreamMismatches}");
        sb.AppendLine($"transcript rows {s.Items.Count}; open dialogs {s.Dialogs.Count}; queued {s.Queued.Count}");
        sb.AppendLine("last error:");
        sb.AppendLine(s.LastError ?? "(none)");
        sb.AppendLine("recent events (type only; contents are not included):");
        foreach (var d in s.DebugTail.TakeLast(200)) sb.AppendLine($"{d.At:HH:mm:ss.fff} {d.Type}");
        return sb.ToString();
    }

    private static string ReadTail(string path, int maxChars)
    {
        try
        {
            if (!File.Exists(path)) return $"({Path.GetFileName(path)} not found)";
            var text = File.ReadAllText(path);
            return text.Length <= maxChars ? text : "…" + text[^maxChars..];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"({Path.GetFileName(path)}: {e.Message})";
        }
    }
}
