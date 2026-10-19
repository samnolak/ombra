using Avalonia.Headless.XUnit;
using Ombra.App.Localization;

namespace Ombra.Tests;

public sealed class PluralFormsTests
{
    [AvaloniaTheory]
    [InlineData(0, "файлов")]
    [InlineData(1, "файл")]
    [InlineData(2, "файла")]
    [InlineData(4, "файла")]
    [InlineData(5, "файлов")]
    [InlineData(11, "файлов")]
    [InlineData(12, "файлов")]
    [InlineData(14, "файлов")]
    [InlineData(21, "файл")]
    [InlineData(22, "файла")]
    [InlineData(25, "файлов")]
    [InlineData(101, "файл")]
    [InlineData(-21, "файл")]
    [InlineData(long.MinValue, "файлов")]
    public void Russian_cardinal_forms_follow_cldr_integer_rules(long count, string ending)
    {
        var previous = Loc.Current;
        var settings = Path.Combine(TestProcesses.TempDir("plural"), "settings.json");
        try
        {
            Loc.Load("ru", settings);
            Assert.Equal($"{count} {ending}", Loc.Plural(count, "1 file", "{0} files"));
        }
        finally { Loc.Load(previous, settings); }
    }

    [AvaloniaFact]
    public void English_and_user_languages_keep_existing_fallback_and_extra_arguments()
    {
        var previous = Loc.Current;
        var settings = Path.Combine(TestProcesses.TempDir("plural-fallback"), "settings.json");
        try
        {
            Loc.Load("en", settings);
            Assert.Equal("1 file", Loc.Plural(1, "1 file", "{0} files"));
            Assert.Equal("21 files", Loc.Plural(21, "1 file", "{0} files"));
            Directory.CreateDirectory(Loc.UserFolder(settings));
            File.WriteAllText(Path.Combine(Loc.UserFolder(settings), "xx.json"), """{"name":"Test","strings":{"{0} items for {1}":"{1}: {0} many","{0} item for {1}":"{1}: {0} one"}}""");
            Loc.Load("xx", settings);
            Assert.Equal("Alice: 3 many", Loc.Plural(3, "{0} item for {1}", "{0} items for {1}", "Alice"));
            Assert.Equal("Alice: 1 one", Loc.Plural(1, "{0} item for {1}", "{0} items for {1}", "Alice"));
        }
        finally { Loc.Load(previous, settings); }
    }
}
