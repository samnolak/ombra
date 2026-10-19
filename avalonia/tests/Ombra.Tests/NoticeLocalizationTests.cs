using Avalonia.Headless.XUnit;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>How a dialog ended (ConversationState.RecordAnswer writes it in English): shown in the UI's language.
/// On the UI thread like every other test that swaps <see cref="Loc"/>'s global language: a thread-pool test restoring
/// its language in the middle of an Avalonia test's switched one broke that test.</summary>
public sealed class NoticeLocalizationTests
{
    [AvaloniaTheory]
    [InlineData("Approved by you — bash", "Одобрено вами — bash")]
    [InlineData("Denied by you — task", "Отклонено вами — task")]
    [InlineData("Denied by you — bash: “not now”", "Отклонено вами — bash: «not now»")]
    [InlineData("Answered by you — Name?", "Отвечено вами — Name?")]
    [InlineData("Answered by you: “Yes” — Continue?", "Ответ: «Yes» — Continue?")]
    [InlineData("Confirmed by you — Delete?", "Подтверждено вами — Delete?")]
    [InlineData("Declined by you — Delete?", "Отклонено вами — Delete?")]
    [InlineData("Something omp said", "Something omp said")]
    public void Answer_notices_are_shown_in_the_ui_language(string recorded, string shown)
    {
        var settingsPath = Path.Combine(TestProcesses.TempDir("notice-loc"), "settings.json");
        var previous = Loc.Current;
        Directory.CreateDirectory(Loc.UserFolder(settingsPath));
        File.WriteAllText(Path.Combine(Loc.UserFolder(settingsPath), "xx.json"), """
            { "name": "Test", "strings": {
              "Approved by you — {0}": "Одобрено вами — {0}",
              "Denied by you — {0}": "Отклонено вами — {0}",
              "Denied by you — {0}: “{1}”": "Отклонено вами — {0}: «{1}»",
              "Answered by you — {0}": "Отвечено вами — {0}",
              "Answered by you: “{0}” — {1}": "Ответ: «{0}» — {1}",
              "Confirmed by you — {0}": "Подтверждено вами — {0}",
              "Declined by you — {0}": "Отклонено вами — {0}"
            } }
            """);
        try
        {
            Loc.Load("xx", settingsPath);
            Assert.Equal(shown, new NoticeRowViewModel(new NoticeItem(1, NoticeLevel.Info, recorded)).Text);
        }
        finally { Loc.Load(previous, settingsPath); }
    }
}
