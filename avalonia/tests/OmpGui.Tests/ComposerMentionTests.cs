using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// "@" in the message box: the project's files and folders in the command menu, narrowed as the name is typed, chosen
/// with the keys, written as the Files pane's "@" button writes them; Esc closes only the menu.
/// </summary>
public sealed class ComposerMentionTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMPGUI_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static async Task<(MainWindow w, MainViewModel vm, TextBox composer)> OpenAsync(string root, string scenario = "normal")
    {
        var s = new SessionController(TestProcesses.FakeFactory(scenario, TestProcesses.TempDir("mention-sessions")), new LaunchRequest(root));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await FileTestRepo.Until(() => vm.Phase == SessionPhase.Ready, "ready");
        var composer = w.FindControl<TextBox>("Composer")!;
        composer.Focus();
        return (w, vm, composer);
    }

    private static void Key(Window w, Key key, PhysicalKey physical, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.KeyPress(key, mods, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Shot(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Directory.CreateDirectory(ShotDir);
        using var frame = w.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(ShotDir, name + ".png"));
        frame?.Save(file, new PngBitmapEncoderOptions());
    }

    private static string[] Labels(MainViewModel vm) => [.. vm.CommandSuggestions.Select(c => c.Label)];

    [AvaloniaFact]
    public async Task At_lists_the_project_narrows_as_typed_and_inserts_the_reference_at_the_caret()
    {
        var root = FileTestRepo.Create();
        var (w, vm, composer) = await OpenAsync(root);

        // "@" alone: the project's top level, folders first (git's list: what .gitignore excludes is not offered)
        w.KeyTextInput("explain @");
        await FileTestRepo.Until(() => vm.IsCommandMenuOpen && vm.CommandSuggestions.Count > 0, "the @ menu");
        Assert.True(w.FindControl<Border>("CommandMenu")!.IsEffectivelyVisible);
        Assert.All(vm.CommandSuggestions, c => Assert.True(c.IsMention));
        Assert.Contains("src/", Labels(vm));
        Assert.Contains("README.md", Labels(vm));
        Assert.DoesNotContain("node_modules/", Labels(vm));
        Assert.DoesNotContain("debug.log", Labels(vm));
        var firstFile = vm.CommandSuggestions.ToList().FindIndex(c => c.IsFile);
        Assert.True(vm.CommandSuggestions.Take(firstFile).All(c => c.IsFolder), "folders come first");

        // A name narrows it: the best match first, with its folder beside it
        w.KeyTextInput("app");
        await FileTestRepo.Until(() => vm.CommandSuggestions.FirstOrDefault()?.Label == "app.py", "narrowed to app.py");
        Assert.Equal("src", vm.CommandSuggestions[0].Description);
        Assert.True(vm.CommandSuggestions[0].IsSelected);
        Shot(w, "ui-composer-mention");

        // ↓ / ↑ move the choice; Enter puts the reference in place of "@app", then a space
        if (vm.CommandSuggestions.Count > 1)
        {
            Key(w, Avalonia.Input.Key.Down, PhysicalKey.ArrowDown);
            Assert.True(vm.CommandSuggestions[1].IsSelected);
            Key(w, Avalonia.Input.Key.Up, PhysicalKey.ArrowUp);
        }
        Key(w, Avalonia.Input.Key.Enter, PhysicalKey.Enter);
        Assert.Equal("explain @src/app.py ", vm.ComposerText);
        Assert.Equal(vm.ComposerText.Length, composer.CaretIndex);
        Assert.False(vm.IsCommandMenuOpen);
        Assert.Equal(SessionPhase.Ready, vm.Phase); // Enter chose the file, it did not send

        // Anywhere in the message: between other words, Tab completes a folder and lists what is in it
        vm.ComposerText = "explain  @src/app.py ";
        composer.CaretIndex = "explain ".Length;
        Dispatcher.UIThread.RunJobs();
        w.KeyTextInput("@sr");
        await FileTestRepo.Until(() => vm.CommandSuggestions.FirstOrDefault()?.Label == "src/", "the src folder");
        Key(w, Avalonia.Input.Key.Tab, PhysicalKey.Tab);
        Assert.Equal("explain @src/ @src/app.py ", vm.ComposerText);
        await FileTestRepo.Until(() => vm.IsCommandMenuOpen && vm.CommandSuggestions.Count > 0 && vm.CommandSuggestions.All(c => c.Description == "src"),
            "the folder's entries");
        Assert.Contains("util.py", Labels(vm));
        Assert.Contains("my notes.txt", Labels(vm));

        // A name with a space is quoted, as the Files pane writes it; the caret goes past the space that follows
        w.KeyTextInput("my");
        await FileTestRepo.Until(() => vm.CommandSuggestions.FirstOrDefault()?.Label == "my notes.txt", "my notes.txt");
        Key(w, Avalonia.Input.Key.Tab, PhysicalKey.Tab);
        Assert.Equal("explain @\"src/my notes.txt\" @src/app.py ", vm.ComposerText);
        Assert.Equal("@\"src/my notes.txt\"", FilesViewModel.Mention("src/my notes.txt"));
        Assert.Equal("explain @\"src/my notes.txt\" ".Length, composer.CaretIndex);
        Assert.False(vm.IsCommandMenuOpen);

        // An e-mail address is no reference; a name that matches nothing says so in the menu's place
        vm.ComposerText = "";
        w.KeyTextInput("mail me@example");
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsCommandMenuOpen);
        w.KeyTextInput(" @zzqqxx");
        await FileTestRepo.Until(() => vm.IsCommandMenuOpen && vm.ShowNoCommandMatch, "no match row");
        Assert.Equal("No files match “zzqqxx”", vm.NoMatchText);
        Assert.True(w.FindControl<Border>("CommandNoMatch")!.IsEffectivelyVisible);

        // The slash menu still works and says its own "no match"
        vm.ComposerText = "";
        w.KeyTextInput("/zzz");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.ShowNoCommandMatch);
        Assert.Equal("No matching commands", vm.NoMatchText);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Esc_closes_only_the_files_menu_and_it_stays_closed_for_that_word()
    {
        var root = FileTestRepo.Create();
        var (w, vm, composer) = await OpenAsync(root, "queue-stream");
        vm.ComposerText = "long task";
        vm.SendCommand.Execute(null);
        await FileTestRepo.Until(() => vm.Phase == SessionPhase.Running, "running");

        composer.Focus();
        w.KeyTextInput("see @READ");
        await FileTestRepo.Until(() => vm.IsCommandMenuOpen && vm.CommandSuggestions.Any(c => c.Label == "README.md"), "the @ menu");
        Key(w, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
        Assert.False(vm.IsCommandMenuOpen);
        Assert.Equal("see @READ", vm.ComposerText);       // the text stays
        Assert.Equal(SessionPhase.Running, vm.Phase);      // omp was not stopped

        // Typing on in the same word does not bring it back
        w.KeyTextInput("ME");
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsCommandMenuOpen);

        // Esc before the list has even appeared closes the search too, not omp's run
        w.KeyTextInput(" @src");
        Key(w, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsCommandMenuOpen);
        Assert.Equal(SessionPhase.Running, vm.Phase);

        // A new "@" opens it again; with nothing open, Esc stops omp as before
        w.KeyTextInput(" @");
        await FileTestRepo.Until(() => vm.IsCommandMenuOpen, "the @ menu again");
        Key(w, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
        Assert.Equal(SessionPhase.Running, vm.Phase);
        Key(w, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
        await FileTestRepo.Until(() => vm.Phase == SessionPhase.Ready, "stopped by the next Esc");
        await vm.DisposeAsync();
        w.Close();
    }

    [Fact]
    public void The_reference_at_the_caret_is_found_where_omp_reads_one()
    {
        Assert.Equal(new MainViewModel.MentionToken(4, 8, "abc"), MainViewModel.FindMention("see @abc", 8));
        Assert.Equal(new MainViewModel.MentionToken(0, 9, "src"), MainViewModel.FindMention("@src/app. rest", 4));
        Assert.Equal(new MainViewModel.MentionToken(1, 3, "a"), MainViewModel.FindMention("(@a", 3));
        Assert.Null(MainViewModel.FindMention("me@example", 10));   // an e-mail address
        Assert.Null(MainViewModel.FindMention("see @abc ", 9));     // the word ended
        Assert.Null(MainViewModel.FindMention("@\"my no", 7));      // quoted: not completed
        Assert.Null(MainViewModel.FindMention("abc", -1));
    }
}
