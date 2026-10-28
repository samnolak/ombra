using System.Collections.Specialized;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

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

    [AvaloniaFact]
    public async Task Returning_to_the_window_reconciles_external_changes_without_replacing_surviving_rows()
    {
        var (w, vm, root) = await OpenAsync(1, 2, 700);
        await vm.CatalogRefreshTask;
        var renamed = vm.Sessions.Single(r => r.Title == "Session 0");
        var deleted = vm.Sessions.Single(r => r.Title == "Session 1");
        vm.SetWindowActive(false);
        WriteSession(root, renamed.Cwd, "Changed while away", Now, renamed.Model.Path);
        File.Delete(deleted.Model.Path);
        var added = WriteSession(root, renamed.Cwd, "Added while away", Now);
        vm.SetWindowActive(true);
        await vm.CatalogRefreshTask;
        Assert.False(vm.HasCatalogNotice);
        Assert.Contains(renamed, vm.Sessions);
        Assert.Equal("Changed while away", renamed.Title);
        Assert.DoesNotContain(deleted, vm.Sessions);
        Assert.Contains(vm.Sessions, row => row.Model.Path == added);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Unavailable_root_keeps_visible_rows_and_automatically_recovers_then_observes_deletion()
    {
        var (w, vm, root) = await OpenAsync(1, 2, 700);
        await vm.CatalogRefreshTask;
        var rows = vm.Sessions.ToArray();
        var moved = root + "-unavailable";
        Directory.Move(root, moved);
        try
        {
            vm.RequestCatalogRefresh();
            await Until(() => vm.HasCatalogNotice, "stale catalog notice");
            Assert.Equal(rows, vm.Sessions);
            Assert.Contains(Loc.F("Sessions may be out of date. Keeping previously listed sessions. {0}", "\0").Split('\0')[0], w.FindControl<TextBlock>("CatalogNoticeText")!.Text);
        }
        finally { Directory.Move(moved, root); }
        var renamed = rows.First(r => r.Title == "Session 0");
        WriteSession(root, renamed.Cwd, "Recovered title", Now, renamed.Model.Path);
        // No further request: the existing worker's bounded retry must recover.
        await Until(() => !vm.HasCatalogNotice && renamed.Title == "Recovered title", "automatic catalog recovery");
        await vm.CatalogRefreshTask;
        Assert.Contains(renamed, vm.Sessions);
        File.Delete(renamed.Model.Path);
        vm.RequestCatalogRefresh();
        await Until(() => !vm.Sessions.Contains(renamed), "authoritative deletion after recovery");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Partial_reads_merge_valid_updates_but_retain_unreadable_and_missing_rows_until_complete()
    {
        var (w, vm, root) = await OpenAsync(1, 3, 700);
        await vm.CatalogRefreshTask;
        var unreadable = vm.Sessions.Single(r => r.Title == "Session 0");
        var changed = vm.Sessions.Single(r => r.Title == "Session 1");
        var deleted = vm.Sessions.Single(r => r.Title == "Session 2");
        var original = File.ReadAllText(unreadable.Model.Path);
        File.WriteAllText(unreadable.Model.Path, "{\"type\":\"session\"");
        File.Delete(deleted.Model.Path);
        WriteSession(root, changed.Cwd, "Valid update", Now, changed.Model.Path);
        var added = WriteSession(root, changed.Cwd, "Valid new session", Now);
        vm.RequestCatalogRefresh();
        await Until(() => vm.HasCatalogNotice && changed.Title == "Valid update"
            && vm.Sessions.Any(r => r.Model.Path == added), "partial updates merged");
        Assert.Contains(unreadable, vm.Sessions);
        Assert.Contains(deleted, vm.Sessions);
        File.WriteAllText(unreadable.Model.Path, original);
        vm.RetryCatalogCommand.Execute(null);
        await Until(() => !vm.HasCatalogNotice && !vm.Sessions.Contains(deleted), "complete scan removes deleted row");
        Assert.Contains(unreadable, vm.Sessions);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Capped_scan_preserves_older_known_rows_and_Show_older_reads_past_the_cap()
    {
        var (w, vm, root) = await OpenAsync(1, 1, 700);
        await vm.CatalogRefreshTask;
        var old = vm.Sessions.Single(r => r.Title == "Session 0");
        File.SetLastWriteTimeUtc(old.Model.Path, DateTime.UtcNow.AddDays(-10));
        for (var i = 0; i < 501; i++) WriteSession(root, old.Cwd, "Capped " + i, Now);
        vm.RequestCatalogRefresh();
        await Until(() => vm.CanShowOlder && vm.Sessions.Any(r => r.Title.StartsWith("Capped ", StringComparison.Ordinal)), "capped scan");
        await vm.CatalogRefreshTask;
        Assert.False(vm.HasCatalogNotice); // a cap is not a failure: Show older is offered instead
        Assert.Contains(old, vm.Sessions);
        Frames();
        Assert.True(w.FindControl<Button>("ShowOlderButton")!.IsEffectivelyVisible);
        vm.ShowOlderCommand.Execute(null);
        Assert.Equal(2 * MainViewModel.CatalogPage, vm.CatalogLimit);
        await Until(() => !vm.CanShowOlder, "every file read");
        await vm.CatalogRefreshTask;
        Assert.Equal(502, vm.Sessions.Count(r => r.Title.StartsWith("Capped ", StringComparison.Ordinal) || r.Title == "Session 0"));
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Persistent_failure_has_three_retries_and_manual_retry_starts_a_new_bounded_attempt()
    {
        var (w, vm, root) = await OpenAsync(1, 1, 700);
        await vm.CatalogRefreshTask;
        var previous = vm.Sessions.ToArray();
        var calls = 0;
        vm.ScanCatalogAsync = (path, ct) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new SessionCatalogScanResult(path, [], false, false, [new(path, "Injected directory failure")]));
        };
        vm.RequestCatalogRefresh();
        await vm.CatalogRefreshTask;
        Assert.Equal(4, calls);
        Assert.True(vm.HasCatalogNotice);
        Assert.Equal(previous, vm.Sessions);
        vm.ScanCatalogAsync = (path, ct) => SessionCatalog.ScanAsync(path, ct: ct);
        vm.RetryCatalogCommand.Execute(null);
        await vm.CatalogRefreshTask;
        Assert.False(vm.HasCatalogNotice);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Cancellation_does_not_publish_an_inflight_result()
    {
        var (w, vm, root) = await OpenAsync(1, 1, 700);
        await vm.CatalogRefreshTask;
        var previous = vm.Sessions.ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SessionCatalogScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.ScanCatalogAsync = (path, ct) => { entered.TrySetResult(); return release.Task; };
        vm.RequestCatalogRefresh();
        await entered.Task;
        await vm.DisposeAsync();
        release.SetResult(new(root, [], true, false, []));
        await vm.CatalogRefreshTask;
        Assert.Equal(previous, vm.Sessions);
        Assert.False(vm.HasCatalogNotice);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Superseded_scan_cannot_publish_over_a_newer_request()
    {
        var (w, vm, root) = await OpenAsync(1, 1, 700);
        await vm.CatalogRefreshTask;
        var previous = vm.Sessions.ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SessionCatalogScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextRelease = new TaskCompletionSource<SessionCatalogScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        vm.ScanCatalogAsync = (path, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); return release.Task; }
            nextEntered.TrySetResult();
            return nextRelease.Task;
        };
        vm.RequestCatalogRefresh();
        await entered.Task;
        vm.RequestCatalogRefresh();
        release.SetResult(new(root, [], true, false, []));
        await nextEntered.Task;
        Assert.Equal(previous, vm.Sessions);
        nextRelease.SetResult(new(root, [], true, false, []));
        await vm.CatalogRefreshTask;
        Assert.DoesNotContain(vm.Sessions, r => r.Title == "Session 0");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData("root")]
    [InlineData("project")]
    [InlineData("metadata")]
    public async Task Io_failure_at_each_scan_boundary_retains_rows_and_merges_readable_updates(string failure)
    {
        var (w, vm, root) = await OpenAsync(2, 1, 700);
        await vm.CatalogRefreshTask;
        var changed = vm.Sessions.Single(r => r.Title == "Session 0");
        var retained = vm.Sessions.Single(r => r.Title == "Session 1");
        WriteSession(root, changed.Cwd, "Readable update", Now, changed.Model.Path);
        var goodProject = new DirectoryInfo(Path.GetDirectoryName(changed.Model.Path)!);
        var badProject = new DirectoryInfo(Path.GetDirectoryName(retained.Model.Path)!);
        IEnumerable<DirectoryInfo> Projects(DirectoryInfo _)
        {
            yield return goodProject;
            if (failure == "root") throw new IOException("Injected root enumeration failure");
            yield return badProject;
        }
        IEnumerable<FileInfo> Files(DirectoryInfo project)
        {
            if (failure == "project" && project.FullName == badProject.FullName)
                throw new UnauthorizedAccessException("Injected project access failure");
            return project.EnumerateFiles("*.jsonl");
        }
        DateTime Metadata(FileInfo file)
        {
            if (failure == "metadata" && file.FullName == retained.Model.Path)
                throw new IOException("Injected metadata failure");
            return file.LastWriteTimeUtc;
        }
        vm.ScanCatalogAsync = (path, ct) => SessionCatalog.ScanAsync(path, 500, ct, Projects, Files, Metadata);
        vm.RequestCatalogRefresh();
        await Until(() => vm.HasCatalogNotice && changed.Title == "Readable update", "failed boundary reported and readable update merged");
        Assert.Contains(retained, vm.Sessions);
        vm.ScanCatalogAsync = (path, ct) => SessionCatalog.ScanAsync(path, ct: ct);
        vm.RetryCatalogCommand.Execute(null);
        await Until(() => !vm.HasCatalogNotice, "failure boundary recovered");
        Assert.Contains(retained, vm.Sessions);
        await vm.DisposeAsync();
        w.Close();
    }

    [Fact]
    public async Task Scan_distinguishes_missing_root_empty_root_cap_and_cancellation()
    {
        var root = TestProcesses.TempDir("catalog-coverage");
        var empty = await SessionCatalog.ScanAsync(root);
        Assert.True(empty.IsAuthoritative);
        Assert.Empty(empty.Sessions);
        var path = WriteSession(root, "/work/coverage", "Known", Now);
        var complete = await SessionCatalog.ScanAsync(root);
        Assert.True(complete.IsAuthoritative);
        Assert.Single(complete.Sessions);
        WriteSession(root, "/work/coverage", "Other", Now);
        var capped = await SessionCatalog.ScanAsync(root, limit: 1);
        Assert.True(capped.IsCapped);
        Assert.False(capped.IsAuthoritative);
        var missing = await SessionCatalog.ScanAsync(root + "-missing");
        Assert.False(missing.EnumerationComplete);
        Assert.NotEmpty(missing.Errors);
        Assert.Empty(missing.Merge(root, complete.Sessions)); // Never merge another account's cached rows.
        var failed = new SessionCatalogScanResult(root, [], false, false, [new(root, "directory failed")]);
        Assert.Equal(complete.Sessions, failed.Merge(root, complete.Sessions));
        File.Delete(path);
        var gone = await SessionCatalog.ReadSummaryAsync(path);
        Assert.Null(gone.Summary);
        Assert.NotNull(gone.Error);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SessionCatalog.ScanAsync(root, ct: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SessionCatalog.ReadSummaryAsync(path, cancelled.Token));
        foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)) File.Delete(file);
        var deleted = await SessionCatalog.ScanAsync(root);
        Assert.True(deleted.IsAuthoritative);
        Assert.Empty(deleted.Merge(root, complete.Sessions));
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
        const double Scrolled = 150; // down past the first project (rows are 30 px)
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height + Scrolled, $"the list scrolls (extent {scroller.Extent.Height:0}, viewport {scroller.Viewport.Height:0})");
        scroller.Offset = new Vector(0, Scrolled);
        Frames();

        // Two rows well inside the viewport: one under the pointer, then another with its ⋯ menu open
        var listTop = list.TranslatePoint(default, w)!.Value.Y;
        var visible = Rows(w).Where(r => r.Value.TranslatePoint(default, w)!.Value.Y is var y && y > listTop + 40 && y < listTop + list.Bounds.Height - 80)
            .OrderBy(r => r.Value.TranslatePoint(default, w)!.Value.Y).ToList();
        Assert.True(visible.Count >= 3, "rows on screen");
        var (hovered, row) = visible[1];
        var (withMenu, menuRow) = visible[2];
        w.MouseMove(row.TranslatePoint(new Point(40, row.Bounds.Height / 2), w)!.Value);
        // Pointer entry now has a 120ms fade. Settle it before testing that rescans do not restart it.
        await Until(() => Actions(row).All(b => b.Opacity == 1)
            && Rows(w).Where(r => r.Key != hovered).SelectMany(r => Actions(r.Value)).All(b => b.Opacity == 0), "hover actions settled");
        Assert.True(row.IsPointerOver);
        // What the reader sees stays put: the rows keep their place on screen (the list may make up for a group that
        // moved from above the view to below it by changing its offset)
        var rowY = row.TranslatePoint(default, w)!.Value.Y;

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
            Assert.Equal(rowY, row.TranslatePoint(default, w)!.Value.Y, 0.5);
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
        await Until(() => more.Opacity == 1, "open-menu action fade");
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

        // Pinned: it moves to the top of its own project; every other row stays as it was. The rescan the pin asks for
        // finds nothing more to change (anything it did would show in the next step's log).
        log.Changes.Clear();
        var pinned = vm.Sessions.Single(x => x.Title == "Session 1");
        await vm.TogglePinItemCommand.ExecuteAsync(pinned);
        await Until(() => vm.SessionGroups.Single(g => g.Items.Contains(pinned)).Items[0] == pinned, "pinned");
        Assert.Equal(["Sessions Move", "project-0 Move Session 1"], log.Changes);
        Assert.All(before.Where(r => r.Key != pinned), r => Assert.Same(r.Value, Rows(w)[r.Key]));
        // Its actions are not left showing on any row (nothing is hovered or focused from the keyboard)
        Assert.All(Rows(w).Values.SelectMany(Actions), b => Assert.Equal(0, b.Opacity));

        // A keyboard user sees the action they reach with Tab, and only while it has the focus
        var pin = Actions(Rows(w)[pinned])[0];
        pin.Focus(NavigationMethod.Tab);
        await Until(() => pin.Opacity == 1, "keyboard focus action fade");
        Assert.Equal(1, pin.Opacity);
        // A pinned row's button unpins: it shows the struck-through pin, not the pin
        var shown = pin.GetVisualDescendants().OfType<Ombra.App.Controls.Icon>().Single(i => i.IsVisible);
        Assert.Same(w.FindResource("IconPinOff"), shown.Data);
        Assert.Equal(0, Meta(Rows(w)[pinned]).Opacity);
        w.FindControl<TextBox>("Composer")!.Focus(NavigationMethod.Tab);
        await Until(() => pin.Opacity == 0, "blurred action fade");
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

    [AvaloniaFact]
    public async Task A_pinned_project_goes_first_and_a_renamed_one_keeps_its_folder()
    {
        var (w, vm, _) = await OpenAsync(projects: 3, perProject: 1, height: 800);
        Assert.Equal(["project-0", "project-1", "project-2"], vm.SessionGroups.Select(g => g.Name).Where(n => !n.StartsWith("sidebar-", StringComparison.Ordinal)));
        var oldest = vm.SessionGroups.Single(g => g.Name == "project-2");

        oldest.TogglePinCommand!.Execute(oldest);
        Assert.Equal(["project-2", "project-0", "project-1"], vm.SessionGroups.Select(g => g.Name).Where(n => !n.StartsWith("sidebar-", StringComparison.Ordinal)));
        Assert.True(oldest.IsPinned);
        Assert.Equal(Loc.T("Unpin project"), oldest.PinLabel);
        // A newer message elsewhere does not move it down
        vm.RequestCatalogRefresh();
        await Until(() => vm.SessionGroups[0] == oldest, "still first");

        oldest.StartRenameCommand.Execute(null);
        Assert.Equal("project-2", oldest.RenameText);
        oldest.RenameText = "  Backend  ";
        oldest.CommitRenameCommand.Execute(null);
        Assert.False(oldest.IsRenaming);
        Assert.Equal("Backend", oldest.Name);
        Assert.Equal("/work/project-2", oldest.Cwd);
        // Esc keeps the name
        oldest.StartRenameCommand.Execute(null);
        oldest.RenameText = "Other";
        oldest.CancelRenameCommand.Execute(null);
        Assert.Equal("Backend", oldest.Name);
        // Empty: the folder's name again
        oldest.StartRenameCommand.Execute(null);
        oldest.RenameText = "";
        oldest.CommitRenameCommand.Execute(null);
        Assert.Equal("project-2", oldest.Name);

        oldest.TogglePinCommand!.Execute(oldest);
        Assert.Equal(["project-0", "project-1", "project-2"], vm.SessionGroups.Select(g => g.Name).Where(n => !n.StartsWith("sidebar-", StringComparison.Ordinal)));
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_project_folds_to_its_header_stays_folded_through_refreshes_and_a_search_still_finds_its_sessions()
    {
        var (w, vm, _) = await OpenAsync(projects: 2, perProject: 2, height: 800);
        await vm.CatalogRefreshTask;
        var group = vm.SessionGroups.Single(g => g.Name == "project-0");
        bool RowShown(SessionItemViewModel r) =>
            w.GetVisualDescendants().OfType<Button>().Any(b => b.DataContext == r && b.Classes.Contains("session") && b.IsEffectivelyVisible);
        Assert.True(group.IsExpanded);
        Assert.Equal(Loc.F("Fold {0}", "project-0"), group.FoldLabel);

        // A click on the folder's name folds it: its header stays, its rows go, the other project keeps its own
        var header = w.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("group-fold") && b.DataContext == group && b.IsEffectivelyVisible);
        header.Command!.Execute(header.CommandParameter);
        Frames();
        Assert.False(group.IsExpanded);
        Assert.Equal(Loc.F("Unfold {0}", "project-0"), group.FoldLabel);
        Assert.True(header.IsEffectivelyVisible);
        Assert.DoesNotContain(group.Items, RowShown);
        Assert.All(vm.SessionGroups.Single(g => g.Name == "project-1").Items, r => Assert.True(RowShown(r)));

        // A refresh of the list keeps the fold
        vm.RequestCatalogRefresh();
        await vm.CatalogRefreshTask;
        Frames();
        Assert.False(vm.SessionGroups.Single(g => g.Name == "project-0").IsExpanded);

        // A search shows its matches in the folded project; cleared, the project is folded again
        vm.SessionFilter = "Session";
        await Until(() => vm.SessionGroups.Single(g => g.Name == "project-0").IsExpanded, "search unfolds for its matches");
        vm.SessionFilter = "";
        await Until(() => !vm.SessionGroups.Single(g => g.Name == "project-0").IsExpanded, "folded again");

        header = w.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("group-fold") && b.DataContext == group && b.IsEffectivelyVisible);
        header.Command!.Execute(header.CommandParameter);
        Frames();
        Assert.True(group.IsExpanded);
        Assert.All(group.Items, r => Assert.True(RowShown(r)));
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Archive_moves_a_session_under_a_folded_Archived_group_and_back()
    {
        var (w, vm, _) = await OpenAsync(2, 2, 900);
        await vm.CatalogRefreshTask;
        var row = vm.Sessions.Single(r => r.Title == "Session 1");
        var before = vm.SessionGroups.SelectMany(g => g.Items).Count();

        // The hover action and the row menu archive at once (nothing on disk changes), with Undo on the note
        row.ToggleArchiveCommand!.Execute(row);
        Assert.True(row.IsArchived);
        Assert.Equal(Loc.T("Unarchive"), row.ArchiveLabel);
        Assert.True(File.Exists(row.Model.Path));
        var archive = vm.SessionGroups.Last();
        Assert.True(archive.IsArchiveGroup);
        Assert.False(archive.IsExpanded);
        Assert.Equal([row], archive.Items);
        Assert.Equal(Loc.F("Archived {0}", 1), archive.ArchiveLabel);
        Assert.DoesNotContain(vm.SessionGroups.Where(g => g.IsProject), g => g.Items.Contains(row));
        Frames();
        Assert.Single(w.GetVisualDescendants().OfType<Grid>(), g => g.Name == "ArchiveHeader" && g.IsEffectivelyVisible);
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<Button>(), b => b.DataContext == row && b.IsEffectivelyVisible);
        // ⌘1–9 and Ctrl+Tab skip a folded archive
        Assert.False(vm.OpenNthSession(before));

        Assert.Equal("brief", vm.SessionCard?.Kind);
        vm.SessionCard!.Primary!.Command.Execute(null); // Undo
        Assert.False(row.IsArchived);
        Assert.DoesNotContain(vm.SessionGroups, g => g.IsArchiveGroup);

        foreach (var r in vm.Sessions.Where(r => r.Title is "Session 0" or "Session 2").ToList()) r.ToggleArchiveCommand!.Execute(r);
        archive = vm.SessionGroups.Last();
        archive.ToggleExpandedCommand.Execute(null);
        Frames();
        Assert.Single(w.GetVisualDescendants().OfType<Grid>(), g => g.Name == "ArchiveHeader" && g.IsEffectivelyVisible);
        Assert.Equal(2, archive.Items.Count(i => w.GetVisualDescendants().OfType<Button>().Any(b => b.DataContext == i && b.IsEffectivelyVisible)));
        archive.UnarchiveAllCommand!.Execute(null);
        Assert.DoesNotContain(vm.Sessions, r => r.IsArchived);
        Assert.Equal(before, vm.SessionGroups.SelectMany(g => g.Items).Count());
        await vm.DisposeAsync();
        w.Close();
    }
}
