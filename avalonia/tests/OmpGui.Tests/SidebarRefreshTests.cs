using System.Collections.Specialized;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>A rescan of omp's session folder (at the start and end of every run) updates the sidebar in place: the rows
/// that stay keep their controls, so the hover, an open row menu and the scroll position survive it.</summary>
public sealed class SidebarRefreshTests
{
    // Whole milliseconds, as omp's message timestamps (and so the catalog's times) are
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static string Message(DateTimeOffset at) => JsonSerializer.Serialize(new
    {
        type = "message",
        message = new { role = "assistant", content = Array.Empty<object>(), timestamp = at.ToUnixTimeMilliseconds() },
    });

    /// <summary>A saved session as omp writes it (title, header, one message), under the project's folder.</summary>
    private static string WriteSession(string root, string cwd, string title, DateTimeOffset at, string? file = null)
    {
        var id = file is null ? Guid.NewGuid().ToString() : Path.GetFileNameWithoutExtension(file);
        if (file is null)
        {
            var project = Path.Combine(root, "-" + string.Concat(cwd.Select(c => char.IsLetterOrDigit(c) ? c : '-')));
            Directory.CreateDirectory(project);
            file = Path.Combine(project, id + ".jsonl");
        }
        File.WriteAllLines(file, [
            JsonSerializer.Serialize(new { type = "title", v = 1, title }),
            JsonSerializer.Serialize(new { type = "session", version = 3, id, cwd }),
            Message(at),
        ]);
        return file;
    }

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

    /// <summary><paramref name="perProject"/> saved sessions in each of <paramref name="projects"/> folders, ten minutes
    /// apart (newest first), listed in a window of <paramref name="height"/>.</summary>
    private static async Task<(MainWindow w, MainViewModel vm, string root)> OpenAsync(int projects, int perProject, double height)
    {
        var root = TestProcesses.TempDir("sidebar-refresh");
        var n = 0;
        for (var p = 0; p < projects; p++)
            for (var i = 0; i < perProject; i++, n++)
                WriteSession(root, $"/work/project-{p}", $"Session {n}", Now.AddMinutes(-10 * (n + 1)));
        var s = new SessionController(TestProcesses.FakeFactory("normal", root), new LaunchRequest(TestProcesses.TempDir("sidebar-refresh-project")));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1100, Height = height };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        vm.RequestCatalogRefresh();
        await Until(() => vm.Sessions.Count(x => x.Title.StartsWith("Session ", StringComparison.Ordinal)) == n, "listed");
        return (w, vm, root);
    }

    private static Dictionary<SessionItemViewModel, Panel> Rows(MainWindow w) =>
        w.GetVisualDescendants().OfType<Panel>().Where(p => p.Classes.Contains("session-row"))
            .ToDictionary(p => (SessionItemViewModel)p.DataContext!);

    private static Button[] Actions(Panel row) => row.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("session-action")).ToArray();

    /// <summary>The row's age or status dot, which the actions take the place of.</summary>
    private static Panel Meta(Panel row) => row.GetVisualDescendants().OfType<Panel>().Single(p => p.Classes.Contains("session-meta"));

    /// <summary>Every change made to the sidebar's collections (the sessions, the groups, each group's rows).</summary>
    private sealed class ChangeLog
    {
        public List<string> Changes { get; } = [];

        public ChangeLog(MainViewModel vm)
        {
            vm.Sessions.CollectionChanged += (_, e) => Changes.Add("Sessions " + e.Action);
            vm.SessionGroups.CollectionChanged += (_, e) =>
            {
                Changes.Add("Groups " + e.Action);
                foreach (var g in e.NewItems?.OfType<SessionGroupViewModel>() ?? []) Watch(g);
            };
            foreach (var g in vm.SessionGroups) Watch(g);
        }

        private void Watch(SessionGroupViewModel g) =>
            g.Items.CollectionChanged += (_, e) => Changes.Add($"{g.Name} {e.Action} {string.Join(",", (e.NewItems ?? e.OldItems)?.OfType<SessionItemViewModel>().Select(i => i.Title) ?? [])}");
    }

    [AvaloniaFact]
    public async Task A_rescan_keeps_the_hovered_row_the_open_menu_and_the_scroll_position()
    {
        var (w, vm, _) = await OpenAsync(projects: 4, perProject: 6, height: 560);
        var list = w.FindControl<ListBox>("SessionList")!;
        var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height + 200, "the list scrolls");
        scroller.Offset = new Vector(0, 200);
        Frames();

        // Two rows well inside the viewport: one under the pointer, then another with its ⋯ menu open
        var listTop = list.TranslatePoint(default, w)!.Value.Y;
        var visible = Rows(w).Where(r => r.Value.TranslatePoint(default, w)!.Value.Y is var y && y > listTop + 40 && y < listTop + list.Bounds.Height - 80)
            .OrderBy(r => r.Value.TranslatePoint(default, w)!.Value.Y).ToList();
        Assert.True(visible.Count >= 3, "rows on screen");
        var (hovered, row) = visible[1];
        var (withMenu, menuRow) = visible[2];
        w.MouseMove(row.TranslatePoint(new Point(40, row.Bounds.Height / 2), w)!.Value);
        Frames();
        Assert.True(row.IsPointerOver);

        var newest = vm.Sessions.Single(x => x.Title == "Session 0");
        var log = new ChangeLog(vm);
        var rescans = 0;
        async Task RescanAsync()
        {
            // As during a run: the open session's newest message moves on, and the hovered one changes too (still
            // older than the row above it, so nothing reorders)
            rescans++;
            var hoveredAt = hovered.Model.LastMessageAt.AddMilliseconds(1);
            File.AppendAllText(hovered.Model.Path, Message(hoveredAt) + "\n");
            File.AppendAllText(newest.Model.Path, Message(Now.AddSeconds(rescans)) + "\n");
            vm.RequestCatalogRefresh();
            await Until(() => hovered.Model.LastMessageAt == hoveredAt && newest.Model.LastMessageAt == Now.AddSeconds(rescans), "rescan " + rescans);
            Assert.Same(row, Rows(w)[hovered]);
            Assert.Same(menuRow, Rows(w)[withMenu]);
            Assert.Equal(200, scroller.Offset.Y);
        }
        for (var i = 0; i < 5; i++)
        {
            await RescanAsync();
            Assert.True(row.IsPointerOver, "the hover stays");
            Assert.All(Actions(row), b => Assert.Equal(1, b.Opacity));
            // No other row shows its actions
            Assert.All(Rows(w).Where(r => r.Key != hovered).SelectMany(r => Actions(r.Value)), b => Assert.Equal(0, b.Opacity));
        }

        var more = Actions(menuRow).Single(b => b.Flyout is not null);
        more.Flyout!.ShowAt(more);
        Frames();
        Assert.True(more.Flyout.IsOpen);
        // The pointer is in the menu, off the row: its ⋯ still shows, and the age it is drawn over does not
        Assert.Equal(1, more.Opacity);
        Assert.Equal(0, Meta(menuRow).Opacity);
        for (var i = 0; i < 3; i++)
        {
            await RescanAsync();
            Assert.True(more.Flyout.IsOpen, "the row's menu stays open");
        }
        // The newest session (and its project) may move up past the open one; nothing is reset, added or removed
        Assert.All(log.Changes, c => Assert.Contains(" Move", c));
        more.Flyout.Hide();
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_new_renamed_pinned_or_deleted_session_changes_only_its_own_row()
    {
        var (w, vm, root) = await OpenAsync(projects: 2, perProject: 3, height: 900);
        var before = Rows(w);
        var log = new ChangeLog(vm);

        // A new session in project 0: one row added at the top of its group
        WriteSession(root, "/work/project-0", "Brand new", Now);
        vm.RequestCatalogRefresh();
        await Until(() => vm.Sessions.Any(x => x.Title == "Brand new"), "new listed");
        Assert.Equal(["Sessions Add", "project-0 Add Brand new"], log.Changes);
        Assert.All(before, r => Assert.Same(r.Value, Rows(w)[r.Key]));

        // Renamed (omp rewrites the title line): the same row, the new title
        log.Changes.Clear();
        var renamed = vm.Sessions.Single(x => x.Title == "Session 4");
        WriteSession(root, renamed.Cwd, "Renamed", renamed.Model.LastMessageAt, renamed.Model.Path);
        vm.RequestCatalogRefresh();
        await Until(() => renamed.Title == "Renamed", "renamed");
        Assert.Empty(log.Changes);
        Assert.Same(before[renamed], Rows(w)[renamed]);
        Assert.Equal("Renamed", Rows(w)[renamed].GetVisualDescendants().OfType<TextBlock>().First().Text);

        // Pinned: it leaves its project for Pinned (first); every other row stays as it was. The rescan the pin asks
        // for finds nothing more to change (anything it did would show in the next step's log).
        log.Changes.Clear();
        var pinned = vm.Sessions.Single(x => x.Title == "Session 1");
        await vm.TogglePinItemCommand.ExecuteAsync(pinned);
        await Until(() => vm.SessionGroups[0].IsPinnedGroup && Rows(w).ContainsKey(pinned), "pinned");
        // (Pinned fills its rows before it is listed, so the log only sees the group arrive)
        Assert.Equal(["Sessions Move", "project-0 Remove Session 1", "Groups Add"], log.Changes);
        Assert.Equal([pinned], vm.SessionGroups[0].Items);
        Assert.All(before.Where(r => r.Key != pinned), r => Assert.Same(r.Value, Rows(w)[r.Key]));
        // Its actions are not left showing on any row (nothing is hovered or focused from the keyboard)
        Assert.All(Rows(w).Values.SelectMany(Actions), b => Assert.Equal(0, b.Opacity));

        // A keyboard user sees the action they reach with Tab, and only while it has the focus
        var pin = Actions(Rows(w)[pinned])[0];
        pin.Focus(NavigationMethod.Tab);
        Frames();
        Assert.Equal(1, pin.Opacity);
        Assert.Equal(0, Meta(Rows(w)[pinned]).Opacity);
        w.FindControl<TextBox>("Composer")!.Focus(NavigationMethod.Tab);
        Frames();
        Assert.Equal(0, pin.Opacity);
        Assert.Equal(1, Meta(Rows(w)[pinned]).Opacity);

        // Deleted elsewhere (omp's picker, another window): one row goes
        log.Changes.Clear();
        var deleted = vm.Sessions.Single(x => x.Title == "Session 5");
        File.Delete(deleted.Model.Path);
        vm.RequestCatalogRefresh();
        await Until(() => !vm.Sessions.Contains(deleted), "deleted");
        Assert.Equal(["Sessions Remove", "project-1 Remove Session 5"], log.Changes);
        Assert.All(before.Where(r => r.Key != pinned && r.Key != deleted), r => Assert.Same(r.Value, Rows(w)[r.Key]));
        await vm.DisposeAsync();
        w.Close();
    }
}
