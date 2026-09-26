using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using OmpGui.App.Controls;
using OmpGui.App.ViewModels;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// The Computer use, Git and worktrees and SSH hosts pages in several states, and the header's branch chip and its menu,
/// measured by <see cref="LayoutAudit"/> in both themes, wide and narrow. With OMPGUI_REVIEW_DIR set the screens are
/// saved under workspace/.
/// </summary>
public sealed class WorkspaceLayoutTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("OMPGUI_REVIEW_DIR");

    private sealed class Audit(string tag)
    {
        public readonly StringBuilder Report = new();
        public int Count;

        public void Take(Window w, string screen)
        {
            screen = $"{tag}-{screen}";
            using (var frame = w.CaptureRenderedFrame())
            {
                if (Dir is not null)
                {
                    Directory.CreateDirectory(Path.Combine(Dir, "workspace"));
                    using var file = File.Create(Path.Combine(Dir, "workspace", screen + ".png"));
                    frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
            }
            var found = LayoutAudit.Run(w);
            Count += found.Count;
            Report.Append(LayoutAudit.Report(screen, found));
        }

        /// <summary>The part of the page around the named control (a list in the middle of a long page).</summary>
        public async Task TakeAround(Window w, string name, string screen)
        {
            w.GetVisualDescendants().OfType<Control>().First(c => c.Name == name).BringIntoView();
            await WorkspaceFixture.Settle(200);
            Take(w, screen);
        }

        /// <summary>The settings page at its top and, when it scrolls, at its end.</summary>
        public async Task TakePage(Window w, string screen)
        {
            var scroll = w.FindControl<ScrollViewer>("SettingsScroll")!;
            scroll.ScrollToHome();
            await WorkspaceFixture.Settle(200);
            Take(w, screen);
            if (scroll.Extent.Height > scroll.Viewport.Height + 1)
            {
                scroll.ScrollToEnd();
                await WorkspaceFixture.Settle(200);
                Take(w, screen + "-end");
                scroll.ScrollToHome();
            }
        }
    }

    private static Task Until(Func<bool> c, string what) => WorkspaceFixture.Until(c, what);

    [AvaloniaTheory]
    [MemberData(nameof(DesignMatrix.Standard), MemberType = typeof(DesignMatrix))]
    public async Task Workspace_pages_and_branch_chip_are_aligned(string theme, double width, double height)
    {
        MainViewModel.ApplyTheme(theme);
        var a = new Audit($"{theme}-{width}");
        try
        {
            var f = new WorkspaceFixture();
            f.Commands["/computer status"] = "Computer use: enabled · prelude: active · configured: display=all, maxWidth=3840, maxHeight=2400";
            f.Commands["/ssh list"] = "build-box | build.example.com | ci | 2222 [user]\nlab | 10.1.1.20 | - | 22 [project]";
            f.SetConfig(new() { ["computer.enabled"] = true, ["tools.approval"] = new Dictionary<string, object> { ["computer"] = "prompt" }, ["github.enabled"] = true,
                ["task.isolation.enabled"] = true });
            f.Tools["gh auth status"] = new ToolResult(0, "github.com\n  ✓ Logged in to github.com account octocat (keyring)\n  - Active account: true\n", "");
            f.Tools["xdpyinfo"] = new ToolResult(0, "number of extensions:    2\n    RANDR\n    XTEST\nscreen #0:\n", "");
            f.Tools["ssh -n -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=8 -p 2222 ci@build.example.com exit"] = new ToolResult(0, "", "");
            f.Tools["ssh -n -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=8 10.1.1.20 exit"] = new ToolResult(255, "", "ssh: connect to host 10.1.1.20 port 22: Connection timed out\n");
            var git = WorkspaceFixture.HasGit;
            var (repo, worktree) = git ? f.MakeRepo() : (TestProcesses.TempDir("ws-repo"), TestProcesses.TempDir("ws-worktree"));
            if (git) File.WriteAllText(Path.Combine(worktree, "wip.txt"), "wip\n");

            // In a worktree: the chip with its worktree mark, and its menu
            var (w, vm) = await f.OpenAsync(worktree, width, height);
            PinPlatform(vm);
            if (git) await Until(() => vm.BranchChip.IsVisible && vm.BranchChip.IsWorktree, "branch chip");
            await WorkspaceFixture.Settle();
            a.Take(w, "header");
            var chip = w.GetVisualDescendants().OfType<BranchChip>().Single();
            if (git)
            {
                chip.OpenMenu();
                await WorkspaceFixture.Settle();
                a.Take(w, "branch-menu");
                chip.Chip.Flyout!.Hide();
                await WorkspaceFixture.Settle(100);
            }

            // Computer use: on in the session, "Ask" for actions, the requirements
            await vm.OpenSettingsAtAsync("computer");
            await Until(() => !vm.ComputerSettings.IsLoading && vm.ComputerSettings.Requirements.Count > 1, "computer page");
            await a.TakePage(w, "computer");
            vm.ComputerSettings.MaxWidth = "1920";
            await vm.ComputerSettings.SetPolicyCommand.ExecuteAsync("allow"); // not answered by the fake CLI: an error line
            await a.TakePage(w, "computer-edited");

            // Git and worktrees in the worktree: the list, the new-worktree form, a refused removal
            vm.SettingsCategory = "git";
            await Until(() => !vm.GitSettings.IsLoading && vm.GitSettings.GhSignedIn, "git page");
            await a.TakePage(w, "git");
            if (git)
            {
                vm.GitSettings.StartNewWorktree();
                vm.GitSettings.NewBranch = "feature/login";
                await WorkspaceFixture.Settle(400);
                a.Take(w, "git-new"); // the form scrolled into view by itself
                vm.GitSettings.CancelCreateCommand.Execute(null);
                // A third, clean worktree to remove (this session works in the second one)
                WorkspaceFixture.Git(repo, "worktree", "add", "-q", "-b", "review", Path.Combine(f.Dir, "git", "app-review"));
                await vm.GitSettings.LoadAsync();
                await vm.GitSettings.Worktrees.First(r => r.Title == "review").AskRemoveCommand.ExecuteAsync(null);
                await WorkspaceFixture.Settle();
                await a.TakeAround(w, "WorktreeList", "git-remove");
            }

            // SSH hosts: the list, one test that worked and one that did not, the add form with a problem
            vm.SettingsCategory = "ssh";
            await Until(() => !vm.SshSettings.IsLoading && vm.SshSettings.Hosts.Count == 2, "ssh page");
            await vm.SshSettings.Hosts[0].TestCommand.ExecuteAsync(null);
            await vm.SshSettings.Hosts[1].TestCommand.ExecuteAsync(null);
            await a.TakePage(w, "ssh");
            vm.SshSettings.BeginAddCommand.Execute(null);
            vm.SshSettings.NewName = "new box";
            await vm.SshSettings.AddCommand.ExecuteAsync(null);
            await WorkspaceFixture.Settle(400);
            a.Take(w, "ssh-add");

            // A long branch name trims in the header instead of pushing into the buttons
            if (git)
            {
                vm.CloseSettingsCommand.Execute(null);
                WorkspaceFixture.Git(worktree, "checkout", "-q", "-b", "feature/a-rather-long-branch-name-for-the-login-rework");
                vm.BranchChip.Refresh();
                await Until(() => vm.BranchChip.Branch?.StartsWith("feature/a-rather", StringComparison.Ordinal) == true, "long branch");
                await WorkspaceFixture.Settle();
                a.Take(w, "header-long");
                var label = chip.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "BranchLabel");
                var buttons = w.FindControl<Button>("PreviewButton")!;
                var right = label.TranslatePoint(new Point(label.Bounds.Width, 0), w)!.Value.X;
                Assert.True(right <= buttons.TranslatePoint(default, w)!.Value.X, $"the branch ends at {right}, over the header buttons");
            }
            await vm.DisposeAsync();
            w.Close();

            // omp not running, a folder outside git, gh not installed, no hosts, computer use off with eval off
            var g = new WorkspaceFixture();
            g.SetConfig(new() { ["eval.js"] = false, ["eval.py"] = false });
            var plain = TestProcesses.TempDir("ws-plain");
            (w, vm) = await g.OpenAsync(plain, width, height, start: false);
            PinPlatform(vm);
            a.Take(w, "header-no-git");
            await vm.OpenSettingsAtAsync("computer");
            await Until(() => !vm.ComputerSettings.IsLoading && vm.ComputerSettings.Requirements.Count > 0, "computer page (stopped)");
            await a.TakePage(w, "computer-stopped");
            vm.SettingsCategory = "git";
            await Until(() => !vm.GitSettings.IsLoading && !vm.GitSettings.GhChecking, "git page (plain)");
            await a.TakePage(w, "git-plain");
            vm.SettingsCategory = "ssh";
            await Until(() => !vm.SshSettings.IsLoading, "ssh page (stopped)");
            await a.TakePage(w, "ssh-stopped");
            await vm.DisposeAsync();
            w.Close();
        }
        finally
        {
            MainViewModel.ApplyTheme("light");
        }
        if (Dir is not null) File.AppendAllText(Path.Combine(Dir, "workspace-audit.tsv"), a.Report.ToString());
        Assert.True(a.Count == 0, $"{a.Count} layout findings:\n{a.Report}");
    }

    /// <summary>The same requirement rows on every machine the test runs on: an X11 desktop.</summary>
    private static void PinPlatform(MainViewModel vm)
    {
        vm.ComputerSettings.CheckPlatform = "linux";
        var env = new Dictionary<string, string?> { ["DISPLAY"] = ":0", ["XDG_SESSION_TYPE"] = "x11", ["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/run/user/1000/bus" };
        vm.ComputerSettings.Environment = k => env.GetValueOrDefault(k);
    }
}
