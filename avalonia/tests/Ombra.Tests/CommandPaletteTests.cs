using System.Text.Json;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Ombra.App;
using Ombra.App.Controls;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>The command palette (⌘K everything, ⌘P files): found by partial words, run with Enter, chats and files
/// opened from it, Esc giving the keyboard back, prefixes ranked above scattered letters, what cannot run greyed.</summary>
public sealed class CommandPaletteTests
{
    private static void Frames(int n = 3)
    {
        for (var i = 0; i < n; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Frames(1);
            await Task.Delay(15);
        }
        Frames();
    }

    private static async Task<(MainWindow W, MainViewModel Vm)> OpenAsync(string project, string scenario = "normal", string? sessions = null)
    {
        var s = new SessionController(TestProcesses.FakeFactory(scenario, sessions ?? TestProcesses.TempDir("palette-sessions")), new LaunchRequest(project));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1200, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        return (w, vm);
    }

    private static RawInputModifiers Command(Window w) => (RawInputModifiers)w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers;

    private static TextBox Box(MainWindow w) => w.FindControl<CommandPalette>("CommandPalette")!.FindControl<TextBox>("PaletteBox")!;

    private static void Press(Window w, Key key, PhysicalKey physical, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.KeyPress(key, mods, physical, null);
        Frames();
    }

    /// <summary>⌘K (or ⌘P), then <paramref name="query"/> typed into the palette's box.</summary>
    private static async Task TypeInPaletteAsync(MainWindow w, MainViewModel vm, string query, bool files = false)
    {
        Press(w, files ? Key.P : Key.K, files ? PhysicalKey.P : PhysicalKey.K, Command(w));
        await Until(() => vm.IsPaletteOpen && Box(w).IsFocused, "the palette open with the keyboard in its box");
        Assert.Equal(files ? PaletteMode.Files : PaletteMode.All, vm.PaletteMode);
        if (query.Length == 0) return;
        w.KeyTextInput(query);
        await Until(() => vm.PaletteQuery == query, "the query typed");
    }

    /// <summary>A saved session as omp writes it: title, header, one exchange.</summary>
    private static string WriteSession(string root, string cwd, string title, DateTimeOffset at)
    {
        var id = Guid.NewGuid().ToString();
        var folder = Path.Combine(root, "-" + string.Concat(cwd.Select(c => char.IsLetterOrDigit(c) ? c : '-')));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, id + ".jsonl");
        File.WriteAllLines(file, [
            JsonSerializer.Serialize(new { type = "title", v = 1, title }),
            JsonSerializer.Serialize(new { type = "session", version = 3, id, cwd }),
            JsonSerializer.Serialize(new { type = "message", message = new { role = "user", content = "hello", timestamp = at.ToUnixTimeMilliseconds() } }),
            JsonSerializer.Serialize(new
            {
                type = "message",
                message = new { role = "assistant", content = new object[] { new { type = "text", text = "hi" } }, timestamp = at.ToUnixTimeMilliseconds() + 1 },
            }),
        ]);
        return file;
    }

    [AvaloniaFact]
    public async Task In_another_language_command_K_also_finds_entries_by_their_English_names()
    {
        var settingsPath = Path.Combine(TestProcesses.TempDir("palette-language"), "settings.json");
        var previous = Loc.Current;
        Directory.CreateDirectory(Loc.UserFolder(settingsPath));
        File.WriteAllText(Path.Combine(Loc.UserFolder(settingsPath), "xx.json"),
            """{ "name": "Test", "strings": { "Find in chat": "Найти в чате", "Background tasks": "Фоновые задачи", "Panel: {0}": "Панель: {0}" } }""");
        Loc.Load("xx", settingsPath);
        try
        {
            var (w, vm) = await OpenAsync(TestProcesses.TempDir("palette-project"));
            w.FindControl<TextBox>("Composer")!.Focus();
            await TypeInPaletteAsync(w, vm, "find in");
            await Until(() => vm.PaletteItems.Any(i => i.Id == "find"), "the localized command found by its English name");
            Assert.Equal("Найти в чате", vm.PaletteItems.First(i => i.Id == "find").Title);
            Box(w).Text = "background";
            await Until(() => vm.PaletteItems.Any(i => i.Id == "panel:tasks"), "the localized panel found by its English name");
            Box(w).Text = "найти";
            await Until(() => vm.PaletteItems.FirstOrDefault()?.Id == "find", "and by its own name");
            await vm.DisposeAsync();
            w.Close();
        }
        finally { Loc.Load(previous, settingsPath); }
    }

    [AvaloniaFact]
    public async Task Command_K_finds_Settings_Permissions_by_partial_words_runs_it_and_lists_it_first_next_time()
    {
        var (w, vm) = await OpenAsync(TestProcesses.TempDir("palette-project"));
        w.FindControl<TextBox>("Composer")!.Focus();
        await TypeInPaletteAsync(w, vm, "set perm");
        var permissions = Loc.F("Settings: {0}", Loc.T("Permissions"));
        var first = vm.PaletteItems[0];
        Assert.Equal(permissions, first.Title);
        Assert.Same(first, vm.SelectedPaletteItem);
        // "Set" and "Perm" are the highlighted characters; in another language the typed English finds the entry but
        // marks nothing of the shown title (MainViewModel.Palette.cs)
        Assert.Equal(Loc.Current == Loc.English ? "SetPerm" : "", string.Concat(first.Matches.Select(i => permissions[i])));

        Press(w, Key.Enter, PhysicalKey.Enter);
        await Until(() => vm.IsSettingsOpen && vm.SettingsCategory == "permissions", "Settings → Permissions shown");
        Assert.False(vm.IsPaletteOpen);

        // Run last: the first entry of the empty palette next time
        vm.CloseSettingsCommand.Execute(null);
        Frames();
        await TypeInPaletteAsync(w, vm, "");
        Assert.Equal("settings:permissions", vm.PaletteItems[0].Id);
        Press(w, Key.Escape, PhysicalKey.Escape);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Esc_closes_the_palette_and_gives_the_keyboard_back_without_stopping_the_run()
    {
        var (w, vm) = await OpenAsync(TestProcesses.TempDir("palette-project"), "queue-stream");
        var composer = w.FindControl<TextBox>("Composer")!;
        vm.ComposerText = "long task";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Running, "running");
        composer.Focus();
        await TypeInPaletteAsync(w, vm, "fil");
        Assert.False(composer.IsFocused);
        // ↓ moves the choice, ↑ back
        var top = vm.SelectedPaletteItem;
        Press(w, Key.Down, PhysicalKey.ArrowDown);
        Assert.NotSame(top, vm.SelectedPaletteItem);
        Press(w, Key.Up, PhysicalKey.ArrowUp);
        Assert.Same(top, vm.SelectedPaletteItem);

        Press(w, Key.Escape, PhysicalKey.Escape);
        Assert.False(vm.IsPaletteOpen);
        Assert.False(w.FindControl<CommandPalette>("CommandPalette")!.IsVisible);
        await Until(() => composer.IsFocused, "the keyboard back in the message box");
        Assert.Equal(SessionPhase.Running, vm.Phase); // Esc only closed the palette
        Assert.Equal("", vm.ComposerText); // and nothing typed in the palette landed in the message box
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_chat_is_found_by_its_title_and_opened()
    {
        var root = TestProcesses.TempDir("palette-sessions");
        var project = SessionCatalog.ResolveLinks(TestProcesses.TempDir("palette-project"));
        var now = DateTimeOffset.UtcNow;
        var zebra = WriteSession(root, project, "Zebra crossing notes", now.AddMinutes(-30));
        WriteSession(root, project, "Weekend plans", now.AddMinutes(-5));
        var (w, vm) = await OpenAsync(project, sessions: root);
        vm.RequestCatalogRefresh();
        await Until(() => vm.Sessions.Any(r => r.Model.Path == zebra), "listed");
        await vm.CatalogRefreshTask;

        await TypeInPaletteAsync(w, vm, "zebra cross");
        var chat = vm.PaletteItems[0];
        Assert.Equal(PaletteKind.Chat, chat.Kind);
        Assert.Equal("Zebra crossing notes", chat.Title);
        Assert.DoesNotContain(vm.PaletteItems, i => i.Title == "Weekend plans");
        Press(w, Key.Enter, PhysicalKey.Enter);
        await Until(() => SessionCatalog.SamePath(vm.Session.SessionFile ?? vm.Session.CurrentLaunch.ResumeSessionFile, zebra), "the chat shown");
        Assert.False(vm.IsPaletteOpen);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_file_of_the_project_is_found_and_opens_in_the_Files_pane()
    {
        var project = TestProcesses.TempDir("palette-files");
        Directory.CreateDirectory(Path.Combine(project, "src", "parsing"));
        File.WriteAllText(Path.Combine(project, "src", "parsing", "alpha_parser.py"), "def parse():\n    return 1\n");
        File.WriteAllText(Path.Combine(project, "src", "beta.py"), "print('beta')\n");
        File.WriteAllText(Path.Combine(project, "README.md"), "# readme\n");
        var (w, vm) = await OpenAsync(project);

        // ⌘K lists the project's files under the other entries
        await TypeInPaletteAsync(w, vm, "alpha_par");
        await Until(() => vm.PaletteItems.Any(i => i.Kind == PaletteKind.File && i.Title == "alpha_parser.py"), "the file among everything");
        Press(w, Key.Escape, PhysicalKey.Escape);

        // ⌘P: the files alone
        await TypeInPaletteAsync(w, vm, "alpa", files: true);
        await Until(() => vm.PaletteItems.Count > 0, "files found");
        Assert.All(vm.PaletteItems, i => Assert.Equal(PaletteKind.File, i.Kind));
        var file = vm.PaletteItems[0];
        Assert.Equal("alpha_parser.py", file.Title);
        Assert.Equal("src/parsing", file.Detail);
        Assert.Equal("alpa", string.Concat(file.Matches.Select(i => file.Title[i])));
        Press(w, Key.Enter, PhysicalKey.Enter);
        await Until(() => vm.IsFilesPaneShown && vm.Files.Viewer is { } v && Path.GetFileName(v.FullPath) == "alpha_parser.py", "the file open in Files");
        Assert.False(vm.IsPaletteOpen);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Prefix_matches_rank_above_scattered_ones_and_what_cannot_run_is_greyed_with_why()
    {
        var (w, vm) = await OpenAsync(TestProcesses.TempDir("palette-project"));
        await TypeInPaletteAsync(w, vm, "set");
        var titles = vm.PaletteItems.Select(i => i.Title).ToList();
        var sidebar = titles.IndexOf(Loc.T("Show or hide the sidebar")); // S…e…t, scattered
        Assert.True(sidebar > 0, string.Join(" | ", titles));
        // "set" is English: in another language it matches the English names (EnglishTitle), which rank the same way
        var english = vm.PaletteItems.Select(i => i.EnglishTitle ?? i.Title).ToList();
        var prefixed = english.Select((t, i) => (t, i)).Where(x => x.t.StartsWith("set", StringComparison.OrdinalIgnoreCase)).Select(x => x.i).ToList();
        Assert.True(prefixed.Count >= SettingsPages(vm));
        Assert.All(prefixed, i => Assert.True(i < sidebar, $"{titles[i]} after the scattered match"));

        // Nothing runs, so Stop omp is listed greyed with why, and Enter leaves the palette open
        Box(w).Clear();
        w.KeyTextInput("stop omp");
        await Until(() => vm.PaletteQuery == "stop omp", "typed");
        var stop = vm.PaletteItems[0];
        Assert.Equal(Loc.T("Stop omp"), stop.Title);
        Assert.False(stop.IsEnabled);
        Assert.Equal(Loc.T("omp is not working"), stop.DetailText);
        vm.SelectedPaletteItem = stop;
        Press(w, Key.Enter, PhysicalKey.Enter);
        Assert.True(vm.IsPaletteOpen);
        Assert.Equal(SessionPhase.Ready, vm.Phase);
        Press(w, Key.Escape, PhysicalKey.Escape);
        await vm.DisposeAsync();
        w.Close();
    }

    private static int SettingsPages(MainViewModel vm) => vm.SettingsCategories.Count;

    [Fact]
    public void Scores_order_prefix_then_word_starts_then_substring_then_scattered_letters()
    {
        var prefix = PaletteMatch.Score("Terminal", "term");
        var words = PaletteMatch.Score("Panel: Terminal", "term");
        var inside = PaletteMatch.Score("Determined", "term");
        var positions = new List<int>();
        var scattered = PaletteMatch.Score("The error message", "term", positions);
        Assert.True(prefix > words && words > inside && inside > scattered && scattered > 0, $"{prefix} {words} {inside} {scattered}");
        Assert.Equal("term", string.Concat(positions.Select(i => "The error message"[i])).ToLowerInvariant());
        Assert.Equal(-1, PaletteMatch.Score("Settings", "xyz"));
        // Several words: each at a word start, in order
        Assert.True(PaletteMatch.Score("Settings: Permissions", "set perm") > PaletteMatch.Score("Settings: Model providers", "set perm"));
    }
}
