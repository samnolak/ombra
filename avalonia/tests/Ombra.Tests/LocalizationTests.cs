using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Xunit;

namespace Ombra.Tests;

public sealed class LocalizationTests
{
    private static string Settings(string name) => Path.Combine(TestProcesses.TempDir(name), "settings.json");

    [Fact]
    public void A_user_file_adds_a_language_and_overrides_built_in_text()
    {
        var settings = Settings("loc-user");
        Directory.CreateDirectory(Loc.UserFolder(settings));
        File.WriteAllText(Path.Combine(Loc.UserFolder(settings), "de.json"),
            """{ "name": "Deutsch", "strings": { "New chat": "Neuer Chat", "New session in {0}": "Neue Sitzung in {0}" } }""");
        File.WriteAllText(Path.Combine(Loc.UserFolder(settings), "ru.json"), """{ "name": "Русский (мой)", "strings": { "New chat": "Беседа" } }""");
        File.WriteAllText(Path.Combine(Loc.UserFolder(settings), "xx.json"), "not json");
        try
        {
            var codes = Loc.Available(settings).Select(l => l.Code).ToList();
            Assert.Equal("en", codes[0]);
            Assert.Contains("de", codes);
            Assert.DoesNotContain("xx", codes); // unreadable: not offered
            Assert.Equal("Русский (мой)", Loc.Available(settings).Single(l => l.Code == "ru").Name);

            var de = Loc.StringsFor("de", settings);
            Assert.Equal("Neuer Chat", de["New chat"]);
            Assert.Equal("Neue Sitzung in {0}", de["New session in {0}"]);

            var ru = Loc.StringsFor("ru", settings);
            Assert.Equal("Беседа", ru["New chat"]); // the user's file wins
            Assert.Equal(BuiltInRu()["Settings"], ru["Settings"]); // the built-in text under it stays
            Assert.Empty(Loc.StringsFor("en", settings));
        }
        finally { Directory.Delete(Loc.UserFolder(settings), true); }
    }

    [Fact]
    public void Untranslated_text_shows_in_English() => Assert.Equal("Some text nobody translated", Loc.T("Some text nobody translated"));

    [Fact]
    public void A_saved_choice_wins_and_an_unknown_one_falls_back()
    {
        var settings = Settings("loc-resolve");
        Assert.Equal("ru", Loc.Resolve("ru", settings));
        Assert.Contains(Loc.Resolve("zz", settings), new[] { "en", "ru" }); // the system's language, else English
    }

    [Fact]
    public void The_template_lists_every_built_in_key_in_English()
    {
        var settings = Settings("loc-template");
        var folder = Loc.WriteTemplate(settings);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "template.json")));
        var strings = doc.RootElement.GetProperty("strings");
        Assert.Equal(BuiltInRu().Count, strings.EnumerateObject().Count());
        Assert.All(strings.EnumerateObject(), p =>
        {
            var separator = p.Name.LastIndexOf('|');
            var category = p.Name[(separator + 1)..];
            var expected = separator >= 0 && category is "one" or "few" or "many" or "other" ? p.Name[..separator] : p.Name;
            Assert.Equal(expected, p.Value.GetString());
        });
        Assert.DoesNotContain(Loc.Available(settings), l => l.Code == "template");
    }

    [Fact]
    public void Russian_keeps_every_placeholder()
    {
        var placeholder = new Regex(@"\{\d+(:[^}]*)?\}");
        Assert.All(BuiltInRu(), kv =>
            Assert.Equal(placeholder.Matches(kv.Key).Select(m => m.Value).Order(), placeholder.Matches(kv.Value).Select(m => m.Value).Order()));
    }

    [AvaloniaFact]
    public void The_permission_modes_speak_the_language_chosen_after_they_were_first_read()
    {
        var settings = Settings("loc-modes");
        var previous = Loc.Current;
        try
        {
            Loc.Load("en", settings);
            Assert.Equal("Accept edits", MainViewModel.ApprovalModes.Single(m => m.Mode == "write").Label);
            Loc.Load("ru", settings);
            var write = MainViewModel.ApprovalModes.Single(m => m.Mode == "write");
            Assert.Equal(BuiltInRu()["Accept edits"], write.Label);
            Assert.Equal(BuiltInRu()[write.EnglishDescription], write.Description);
        }
        finally { Loc.Load(previous, settings); }
    }

    /// <summary>
    /// A binding's StringFormat with words in it ('Remove {0}') bypasses the translation unless it is written
    /// StringFormat={loc:T '…'}; only pure formats ('{}{0}  ✕', '{0:N0}') may stay literal.
    /// </summary>
    [Fact]
    public void Every_string_format_with_words_in_the_views_is_translated()
    {
        var src = Path.GetDirectoryName(Path.GetDirectoryName(RuntimeInstallerTests.RepoFile(Path.Combine("avalonia", "src", "Ombra.App", "Ombra.App.csproj"))))!;
        var literal = new Regex(@"StringFormat\s*=\s*(?:'(?<f>[^']*)'|""(?<f>[^""]*)"")");
        var placeholder = new Regex(@"\{\}|\{\d+(:[^}]*)?\}");
        var untranslated = Directory.EnumerateFiles(src, "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(d => d is "bin" or "obj"))
            .SelectMany(f => literal.Matches(File.ReadAllText(f))
                .Where(m => placeholder.Replace(m.Groups["f"].Value, "").Any(char.IsLetter))
                .Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .ToList();
        Assert.True(untranslated.Count == 0, "Write these as StringFormat={loc:T '…'} and add the Russian:\n" + string.Join("\n", untranslated));
    }

    private static Dictionary<string, string> BuiltInRu()
    {
        using var s = typeof(Loc).Assembly.GetManifestResourceStream("localization/ru.json")!;
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.GetProperty("strings").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }
}

/// <summary><c>OMBRA_TEST_LANG=ru</c>: the whole suite (the layout audits and review shots above all) runs with the app
/// in that language, to see what longer text does to the layout.</summary>
internal static class TestLanguage
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Apply()
    {
        if (Environment.GetEnvironmentVariable("OMBRA_TEST_LANG") is { Length: > 0 } code)
            Loc.Load(code, Path.Combine(Path.GetTempPath(), "ombra-test-lang", "settings.json"));
    }
}
