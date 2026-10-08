using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.Services;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.App.Views.Panes;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// The Files pane's Changes (diff review) in real git repositories: a hunk reverts on its own (staged or not), the whole
/// file reverts only after the question, a new file is deleted the same way, and a comment on a line goes in the
/// message box as a chip and with the message to omp.
/// </summary>
public sealed class FilesReviewTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMPGUI_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static string Numbered(int lines) => string.Concat(Enumerable.Range(1, lines).Select(i => $"line {i}\n"));

    private static string GitOut(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30_000);
        return output;
    }

    private static async Task Settle(int ms = 200)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
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

    private static void ClickAt(Window w, Control c)
    {
        var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w)!.Value;
        w.MouseMove(p);
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    /// <summary>The Files pane alone in a window (as the side-pane host shows it).</summary>
    private static (Window w, FilesPane pane) Host(FilesViewModel f)
    {
        var pane = new FilesPane { DataContext = f };
        var w = new Window { Width = 560, Height = 820, Content = pane };
        w.Show();
        return (w, pane);
    }

    /// <summary>Opens <paramref name="relativePath"/> from the Changed list, as the user does (it starts on its changes).</summary>
    private static async Task<FileViewerViewModel> OpenChangesAsync(FilesViewModel f, string relativePath)
    {
        f.Mode = FilesMode.Changed;
        await FileTestRepo.Until(() => f.Results.Any(n => n.RelativePath == relativePath), relativePath + " listed as changed");
        await f.ActivateCommand.ExecuteAsync(f.Results.First(n => n.RelativePath == relativePath));
        var v = f.Viewer!;
        await FileTestRepo.Until(() => v.ShowChanges && v.DiffRows.Count > 0, "the changes of " + relativePath);
        return v;
    }

    [AvaloniaFact]
    public async Task Reverting_a_hunk_takes_back_only_that_part_staged_or_not_and_the_marks_follow()
    {
        if (!FileTestRepo.HasGit()) Assert.Skip("git is not installed");
        var root = TestProcesses.TempDir("review-hunks");
        var original = Numbered(40);
        FileTestRepo.Write(root, "calc.py", original);
        FileTestRepo.Git(root, "init", "-q");
        FileTestRepo.Git(root, "add", "-A");
        FileTestRepo.Git(root, "commit", "-q", "-m", "init");
        var file = Path.Combine(root, "calc.py");
        // Two edits far apart: two hunks
        File.WriteAllText(file, original.Replace("line 3\n", "line 3 changed by omp\n").Replace("line 35\n", "line 35 changed by omp\nline 35b added\n"));

        var vm = FileTestRepo.Vm(root);
        var f = await FileTestRepo.Shown(vm, git: true);
        var v = await OpenChangesAsync(f, "calc.py");
        Assert.Equal(GitMark.Modified, v.GitMark);
        Assert.Equal(2, v.DiffRows.Count(r => r.IsHunk));
        Assert.Equal("+3 −2", v.DiffStats);
        Assert.True(v.CanRevertHunks);
        var added = v.DiffRows.First(r => r.IsAdded);
        Assert.Equal(("line 3 changed by omp", (int?)3), (added.Text, added.Number));

        // The first hunk goes; the second stays
        await v.RevertHunkCommand.ExecuteAsync(v.DiffRows.First(r => r.IsHunk));
        var now = File.ReadAllText(file);
        Assert.Contains("\nline 3\n", now);
        Assert.DoesNotContain("line 3 changed", now);
        Assert.Contains("line 35 changed by omp\nline 35b added\n", now);
        await FileTestRepo.Until(() => v.DiffRows.Count(r => r.IsHunk) == 1, "one hunk left");
        Assert.Equal("+2 −1", v.DiffStats);
        Assert.Equal("", f.Notice);

        // Staged too: reverting it takes it out of the index as well, or the next commit would still have it
        FileTestRepo.Git(root, "add", "calc.py");
        await f.RefreshAsync();
        await v.RevertHunkCommand.ExecuteAsync(v.DiffRows.Single(r => r.IsHunk));
        Assert.Equal(original, File.ReadAllText(file));
        Assert.Equal("", GitOut(root, "status", "--porcelain", "--", "calc.py"));
        await FileTestRepo.Until(() => !v.HasChanges && !v.ShowChanges && f.ChangedCount == 0, "nothing left to review");
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Revert_file_asks_first_then_puts_the_file_back_and_a_new_file_is_deleted()
    {
        if (!FileTestRepo.HasGit()) Assert.Skip("git is not installed");
        var root = FileTestRepo.Create();
        var app = Path.Combine(root, "src", "app.py");
        var vm = FileTestRepo.Vm(root);
        var f = await FileTestRepo.Shown(vm, git: true);
        var (w, _) = Host(f);
        var v = await OpenChangesAsync(f, "src/app.py");
        await Settle();
        var revert = Named<Button>(w, "RevertFileButton");
        Assert.True(revert.IsEffectivelyVisible);
        Assert.Equal("Revert file…", revert.Content);
        Assert.True(Named<Border>(w, "ViewerReviewBar").IsEffectivelyVisible);
        Shot(w, "files-review-changes");

        // The question comes first; Cancel leaves the file as it is
        ClickAt(w, revert);
        await Settle(100);
        Assert.True(Named<Border>(w, "RevertConfirm").IsEffectivelyVisible);
        Assert.Contains("can't be undone", v.RevertQuestion);
        Shot(w, "files-review-revert-confirm");
        Assert.Contains("print('changed')", File.ReadAllText(app));
        ClickAt(w, Named<Border>(w, "RevertConfirm").GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Cancel"));
        Assert.False(v.IsConfirmingRevert);
        Assert.Contains("print('changed')", File.ReadAllText(app));

        // Confirmed: the file is the last commit's again, and the viewer shows it
        ClickAt(w, revert);
        await Settle(100);
        ClickAt(w, Named<Button>(w, "ConfirmRevertButton"));
        await FileTestRepo.Until(() => !File.ReadAllText(app).Contains("print('changed')") && !v.HasChanges && !v.ShowChanges, "reverted");
        Assert.Equal("", GitOut(root, "status", "--porcelain", "--", "src/app.py"));
        await FileTestRepo.Until(() => v.Text.Contains("def step_1"), "the file shown");
        Assert.False(Named<Border>(w, "ViewerReviewBar").IsEffectivelyVisible);

        // A file git does not have: one hunk (the whole file) with no Revert of its own; "Delete file…" asks too
        var added = Path.Combine(root, "src", "new_file.py");
        v = await OpenChangesAsync(f, "src/new_file.py");
        await Settle();
        Assert.Equal(GitMark.Untracked, v.GitMark);
        Assert.False(v.CanRevertHunks);
        Assert.Equal("Delete file…", Named<Button>(w, "RevertFileButton").Content);
        ClickAt(w, Named<Button>(w, "RevertFileButton"));
        await Settle(100);
        Assert.True(File.Exists(added));
        ClickAt(w, Named<Button>(w, "ConfirmRevertButton"));
        await FileTestRepo.Until(() => !File.Exists(added) && f.Viewer is null, "deleted and closed");
        w.Close();
        await vm.DisposeAsync();
    }

    /// <summary>
    /// A repository with <paramref name="files"/> committed, <paramref name="change"/> made to it (in its folder), and its
    /// Files pane shown with git on.
    /// </summary>
    private static async Task<(string root, MainViewModel vm, FilesViewModel f)> CommittedAsync(string name, Action<string> change,
        params (string Path, string Text)[] files)
    {
        var root = TestProcesses.TempDir(name);
        foreach (var (path, text) in files) FileTestRepo.Write(root, path, text);
        FileTestRepo.Git(root, "init", "-q");
        FileTestRepo.Git(root, "add", "-A");
        FileTestRepo.Git(root, "commit", "-q", "-m", "init");
        change(root);
        var vm = FileTestRepo.Vm(root);
        return (root, vm, await FileTestRepo.Shown(vm, git: true));
    }

    [AvaloniaFact]
    public async Task A_staged_new_file_or_rename_has_no_hunk_revert_and_reverting_a_rename_brings_the_old_name_back()
    {
        if (!FileTestRepo.HasGit()) Assert.Skip("git is not installed");
        var original = Numbered(20);
        // Added to git, never committed: its diff is the whole file, and taking that hunk out would delete it unasked
        var (root, vm, f) = await CommittedAsync("review-new", dir =>
        {
            File.WriteAllText(Path.Combine(dir, "added.py"), "print('new')\n");
            FileTestRepo.Git(dir, "add", "added.py");
        }, ("old_name.py", original));
        var added = Path.Combine(root, "added.py");
        var v = await OpenChangesAsync(f, "added.py");
        Assert.Equal(GitMark.Added, v.GitMark);
        Assert.False(v.CanRevertHunks);
        await v.RevertHunkCommand.ExecuteAsync(v.DiffRows.First(r => r.IsHunk));
        Assert.True(File.Exists(added));
        Assert.Equal("A  added.py\n", GitOut(root, "status", "--porcelain", "--", "added.py"));
        Assert.Contains("added since the last commit", v.RevertQuestion);

        // Renamed (git mv) and edited: Revert file puts the old name back as committed, the new one goes
        FileTestRepo.Git(root, "mv", "old_name.py", "new_name.py");
        File.AppendAllText(Path.Combine(root, "new_name.py"), "one more line\n");
        await f.RefreshAsync();
        v = await OpenChangesAsync(f, "new_name.py");
        Assert.Equal(GitMark.Renamed, v.GitMark);
        Assert.False(v.CanRevertHunks);
        Assert.Equal("old_name.py", v.RenamedFrom);
        Assert.Contains("goes back to old_name.py", v.RevertQuestion);
        await v.ConfirmRevertFileCommand.ExecuteAsync(null);
        Assert.Equal("", f.Notice);
        Assert.Equal(original, File.ReadAllText(Path.Combine(root, "old_name.py")));
        Assert.False(File.Exists(Path.Combine(root, "new_name.py")));
        Assert.Equal("", GitOut(root, "status", "--porcelain", "--", "old_name.py", "new_name.py"));
        Assert.Null(f.Viewer);
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task A_hunk_staged_and_then_edited_again_is_left_alone_with_a_reason()
    {
        if (!FileTestRepo.HasGit()) Assert.Skip("git is not installed");
        var edited = Numbered(20).Replace("line 3\n", "line 3 staged then edited\n");
        var (root, vm, f) = await CommittedAsync("review-partly-staged", dir =>
        {
            File.WriteAllText(Path.Combine(dir, "calc.py"), Numbered(20).Replace("line 3\n", "line 3 staged\n"));
            FileTestRepo.Git(dir, "add", "calc.py");
            File.WriteAllText(Path.Combine(dir, "calc.py"), edited);
        }, ("calc.py", Numbered(20)));
        var file = Path.Combine(root, "calc.py");
        var v = await OpenChangesAsync(f, "calc.py");
        Assert.True(v.CanRevertHunks);

        // Reverting only the file would hide the change here while the next commit still takes the staged part
        await v.RevertHunkCommand.ExecuteAsync(v.DiffRows.Single(r => r.IsHunk));
        Assert.Equal(edited, File.ReadAllText(file));
        Assert.Contains("line 3 staged\n", GitOut(root, "show", ":calc.py"));
        Assert.Contains("staged and was changed again", f.Notice);
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Long_changes_count_every_hunk_and_huge_ones_are_not_read_whole()
    {
        if (!FileTestRepo.HasGit()) Assert.Skip("git is not installed");
        var original = Numbered(5000);
        // 1600 lines changed (3200 rows, past the cap), then one more change far below: a second hunk
        var (root, vm, f) = await CommittedAsync("review-long", dir =>
        {
            var lines = original.Split('\n');
            for (var i = 0; i < 1600; i++) lines[i] += " changed";
            lines[3999] += " changed";
            File.WriteAllText(Path.Combine(dir, "data.txt"), string.Join('\n', lines));
        }, ("data.txt", original));
        var v = await OpenChangesAsync(f, "data.txt");
        Assert.Equal(FileViewerViewModel.MaxDiffRows, v.DiffRows.Count);
        Assert.Equal("+1601 −1601", v.DiffStats);
        Assert.Contains("Showing the first", v.DiffMessage);

        // A file git does not have, larger than what is read of a diff: a short message instead of its lines
        File.WriteAllText(Path.Combine(root, "big.log"), string.Concat(Enumerable.Repeat(new string('x', 99) + "\n", GitReview.MaxDiffChars / 100 + 1000)));
        await f.RefreshAsync();
        f.Mode = FilesMode.Changed;
        await FileTestRepo.Until(() => f.Results.Any(n => n.RelativePath == "big.log"), "big.log listed as changed");
        await f.ActivateCommand.ExecuteAsync(f.Results.First(n => n.RelativePath == "big.log"));
        v = f.Viewer!;
        await FileTestRepo.Until(() => v.ShowChanges && v.DiffMessage == "These changes are too large to show here.", "too large");
        Assert.Empty(v.DiffRows);
        Assert.False(v.CanRevertHunks);
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task A_comment_on_a_line_becomes_a_chip_and_goes_with_the_next_message()
    {
        if (!FileTestRepo.HasGit()) Assert.Skip("git is not installed");
        var root = FileTestRepo.Create();
        var s = new SessionController(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("review-sessions")), new LaunchRequest(root));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 };
        w.Show();
        vm.OnWindowOpened();
        await FileTestRepo.Until(() => vm.Phase == SessionPhase.Ready, "ready");
        var f = await FileTestRepo.Shown(vm, git: true);
        var v = await OpenChangesAsync(f, "src/app.py");
        await Settle();

        // "+" at the added line's start opens its comment box, with the keyboard in it
        var row = v.DiffRows.First(r => r.IsAdded);
        Assert.Equal(41, row.Number);
        var plus = w.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("diff-comment") && ReferenceEquals(b.DataContext, row));
        ClickAt(w, plus);
        await FileTestRepo.Until(() => row.IsCommenting && w.FocusManager?.GetFocusedElement() is TextBox { DataContext: DiffRowViewModel r } && ReferenceEquals(r, row),
            "the comment box focused");
        w.KeyTextInput("log this instead of printing");
        Shot(w, "files-review-comment-box");
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(row.IsCommenting);
        var chip = Assert.Single(vm.CodeComments);
        Assert.Equal("app.py:41", chip.Where);
        Assert.Equal("log this instead of printing", chip.Comment);
        await Settle();
        Assert.True(w.FindControl<ItemsControl>("CodeCommentsChip")!.IsEffectivelyVisible);
        Assert.True(vm.SendCommand.CanExecute(null)); // comments alone are a message
        Shot(w, "files-review-comment-chip");

        // Esc closes a box without adding anything (and without reaching the window)
        ClickAt(w, plus);
        await FileTestRepo.Until(() => row.IsCommenting && w.FocusManager?.GetFocusedElement() is TextBox, "the box again");
        w.KeyTextInput("never mind");
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(row.IsCommenting);
        Assert.Single(vm.CodeComments);

        // Sent alone: a first line says what to do, the comment follows with its file, line and the line's text; the
        // conversation shows it as a chip
        vm.SendCommand.Execute(null);
        await FileTestRepo.Until(() => vm.Rows.OfType<UserRowViewModel>().Any(), "sent");
        Assert.Empty(vm.CodeComments);
        var sent = vm.Rows.OfType<UserRowViewModel>().First();
        Assert.StartsWith(MainViewModel.CodeCommentsOnlyLine + "\n\n" + MainViewModel.CodeCommentsMarker, sent.Text);
        Assert.Contains("1. src/app.py, line 41: log this instead of printing\n   > print('changed')", sent.Text);
        Assert.True(sent.HasComments);
        Assert.Equal("1 comment on the code", sent.CommentsLabel);
        Assert.False(sent.HasOwnText);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_comment_being_written_stays_when_omp_changes_the_file_meanwhile()
    {
        if (!FileTestRepo.HasGit()) Assert.Skip("git is not installed");
        var root = TestProcesses.TempDir("review-comment");
        FileTestRepo.Write(root, "notes.md", Numbered(10));
        FileTestRepo.Git(root, "init", "-q");
        FileTestRepo.Git(root, "add", "-A");
        FileTestRepo.Git(root, "commit", "-q", "-m", "init");
        var file = Path.Combine(root, "notes.md");
        File.WriteAllText(file, Numbered(10).Replace("line 8\n", "line 8 by omp\n"));
        var vm = FileTestRepo.Vm(root);
        var f = await FileTestRepo.Shown(vm, git: true);
        var v = await OpenChangesAsync(f, "notes.md");
        v.DiffRows.First(r => r.IsAdded).CommentCommand.Execute(null);
        v.DiffRows.First(r => r.IsAdded).CommentText = "half a thought";

        // omp adds a line above it: the diff reads again, the box stays on that line with what was typed
        File.WriteAllText(file, "new first line\n" + Numbered(10).Replace("line 8\n", "line 8 by omp\n"));
        await f.RefreshAsync();
        await FileTestRepo.Until(() => v.DiffRows.Any(r => r.Text == "new first line"), "the new diff");
        var row = v.DiffRows.Single(r => r.IsCommenting);
        Assert.Equal(("line 8 by omp", (int?)9, "half a thought"), (row.Text, row.Number, row.CommentText));
        await vm.DisposeAsync();
    }

    [Fact]
    public void A_hunk_is_a_patch_of_its_own_with_the_file_header()
    {
        var diff = FileDiff.Parse(
            "diff --git a/x.txt b/x.txt\nindex 1..2 100644\n--- a/x.txt\n+++ b/x.txt\n" +
            "@@ -1,2 +1,2 @@\n-a\n+A\n b\n@@ -10,2 +10,3 @@ def f\n c\n+d\r\n e\n\\ No newline at end of file\n");
        Assert.False(diff.IsBinary);
        Assert.Equal(2, diff.Hunks.Count);
        Assert.Equal((10, 10), (diff.Hunks[1].OldStart, diff.Hunks[1].NewStart));
        Assert.Equal(
            "diff --git a/x.txt b/x.txt\nindex 1..2 100644\n--- a/x.txt\n+++ b/x.txt\n@@ -10,2 +10,3 @@ def f\n c\n+d\r\n e\n\\ No newline at end of file\n",
            diff.PatchFor(diff.Hunks[1]));
        Assert.True(FileDiff.Parse("diff --git a/i.png b/i.png\nBinary files a/i.png and b/i.png differ\n").IsBinary);
    }
}
