using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Ombra.App.Localization;

/// <summary>One language the app can show: built in (an embedded <c>Localization/&lt;code&gt;.json</c>) or the user's
/// (<c>&lt;settings folder&gt;/languages/&lt;code&gt;.json</c>, which wins over a built-in one with the same code).</summary>
public sealed record LanguageInfo(string Code, string Name, string? File);

/// <summary>
/// The app's text in the chosen language. Keys are the English text itself, so a string without a translation shows in
/// English and nothing is ever blank. A language file is JSON:
/// <c>{ "name": "Русский", "strings": { "New chat": "Новый чат", "New session in {0}": "Новая сессия в {0}" } }</c>.
/// The language is set once at start (<see cref="Load"/>), before any window: changing it takes effect after a restart.
/// </summary>
public static class Loc
{
    public const string English = "en";
    private static Dictionary<string, string> _strings = new(StringComparer.Ordinal);

    /// <summary>The code of the language shown (<see cref="English"/> when none is loaded).</summary>
    public static string Current { get; private set; } = English;

    /// <summary><paramref name="english"/> in the current language (itself when untranslated).</summary>
    public static string T(string english) => _strings.TryGetValue(english, out var t) && t.Length > 0 ? t : english;

    /// <summary><see cref="T"/>, then <see cref="string.Format(IFormatProvider, string, object[])"/>.</summary>
    public static string F(string english, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(english), args);

    /// <summary>
    /// A count in words: <paramref name="one"/> when <paramref name="n"/> is 1, else <paramref name="other"/> (English's two
    /// forms; each is its own key, so a language with other rules can word both neutrally). Formatted with
    /// <paramref name="n"/> as <c>{0}</c> and <paramref name="args"/> as <c>{1}</c>, <c>{2}</c>….
    /// </summary>
    public static string Plural(long n, string one, string other, params object?[] args) =>
        F(n == 1 ? one : other, [n, .. args]);

    /// <summary>Where the user's language files go.</summary>
    public static string UserFolder(string settingsPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath)) ?? ".", "languages");

    /// <summary>English, the built-in languages, then the user's (by name).</summary>
    public static IReadOnlyList<LanguageInfo> Available(string settingsPath)
    {
        var found = new Dictionary<string, LanguageInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in BuiltIn())
            if (ReadBuiltIn(code) is { } doc) found[code] = new LanguageInfo(code, doc.Name ?? code, null);
        var folder = UserFolder(settingsPath);
        if (Directory.Exists(folder))
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                var code = Path.GetFileNameWithoutExtension(file);
                if (code.Equals(English, StringComparison.OrdinalIgnoreCase) || code.Equals("template", StringComparison.OrdinalIgnoreCase)) continue;
                if (ReadFile(file) is { } doc) found[code] = new LanguageInfo(code, doc.Name ?? code, file);
            }
        return [new LanguageInfo(English, "English", null), .. found.Values.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The code a saved choice means: null follows the system (a language there is one for, else English).</summary>
    public static string Resolve(string? saved, string settingsPath)
    {
        var available = Available(settingsPath);
        if (saved is { Length: > 0 } && available.Any(l => l.Code.Equals(saved, StringComparison.OrdinalIgnoreCase))) return saved;
        var system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return available.FirstOrDefault(l => l.Code.Equals(system, StringComparison.OrdinalIgnoreCase))?.Code ?? English;
    }

    /// <summary>Shows the app in <paramref name="code"/>.</summary>
    public static void Load(string code, string settingsPath)
    {
        _strings = StringsFor(code, settingsPath);
        Current = code;
    }

    /// <summary>The text of <paramref name="code"/>: the built-in strings, then the user's file over them.</summary>
    public static Dictionary<string, string> StringsFor(string code, string settingsPath)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (code.Equals(English, StringComparison.OrdinalIgnoreCase)) return strings;
        if (ReadBuiltIn(code)?.Strings is { } b)
            foreach (var (k, v) in b) strings[k] = v;
        var user = Path.Combine(UserFolder(settingsPath), code + ".json");
        if (File.Exists(user) && ReadFile(user)?.Strings is { } u)
            foreach (var (k, v) in u) strings[k] = v;
        return strings;
    }

    /// <summary>
    /// Writes <c>languages/template.json</c>: every key the built-in Russian file has, each with its English text, as a
    /// start for a language of one's own (copy it to <c>&lt;code&gt;.json</c> and translate the values). Returns the folder.
    /// </summary>
    public static string WriteTemplate(string settingsPath)
    {
        var folder = UserFolder(settingsPath);
        Directory.CreateDirectory(folder);
        IEnumerable<string> keys = ReadBuiltIn("ru")?.Strings?.Keys ?? Enumerable.Empty<string>();
        var doc = new LanguageFile { Name = "My language", Strings = keys.ToDictionary(k => k, k => k, StringComparer.Ordinal) };
        File.WriteAllText(Path.Combine(folder, "template.json"),
            JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return folder;
    }

    private sealed class LanguageFile
    {
        public string? Name { get; set; }
        public Dictionary<string, string>? Strings { get; set; }
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    private const string ResourcePrefix = "localization/";

    private static IEnumerable<string> BuiltIn() =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..^".json".Length]);

    private static LanguageFile? ReadBuiltIn(string code)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + code.ToLowerInvariant() + ".json");
        if (s is null) return null;
        try { return JsonSerializer.Deserialize<LanguageFile>(s, ReadOptions); }
        catch (JsonException) { return null; }
    }

    /// <summary>A user's file that cannot be read or parsed is skipped (the language is not offered).</summary>
    private static LanguageFile? ReadFile(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            return JsonSerializer.Deserialize<LanguageFile>(s, ReadOptions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
