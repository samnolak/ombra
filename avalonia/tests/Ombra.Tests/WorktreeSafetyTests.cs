using Avalonia.Headless.XUnit;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class WorktreeSafetyTests
{
    private static string OpenChatProblem => Loc.T("An open conversation works in it. Close that conversation or open another folder in it first.");

    [AvaloniaFact]
    public async Task Background_chat_in_nested_symlinked_folder_blocks_removal()
    {
        if (!WorkspaceFixture.HasGit) { Assert.Skip("git is not installed"); return; }
        var f = new WorkspaceFixture();
        var (repo, worktree) = f.MakeRepo();
        var nested = Path.Combine(worktree, "nested");
        Directory.CreateDirectory(nested);
        var alias = Path.Combine(f.Dir, "worktree-alias");
        Directory.CreateSymbolicLink(alias, worktree);
        var vm = await f.StartAsync(Path.Combine(alias, "nested"));
        try
        {
            vm.ComposerText = "Keep this background chat open";
            await vm.OpenFolderInNewSessionAsync(repo);
            Assert.Equal(2, vm.OpenChats.Count);
            Assert.True(SessionCatalog.SamePath(repo, vm.ProjectFolder));
            await vm.GitSettings.LoadAsync();
            var row = vm.GitSettings.Worktrees.Single(r => SessionCatalog.SamePath(r.Path, worktree));
            Assert.False(row.IsCurrent);
            await row.AskRemoveCommand.ExecuteAsync(null);
            Assert.False(row.IsConfirming);
            Assert.Contains(OpenChatProblem, row.Problem);
            await row.ConfirmRemoveCommand.ExecuteAsync(null); // direct dispatch must be protected too
            Assert.Contains(OpenChatProblem, row.Problem);
            Assert.True(Directory.Exists(nested));
            Assert.DoesNotContain(f.ToolLog, IsRemoval);
        }
        finally { await vm.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task Chat_opened_after_confirmation_was_shown_blocks_removal()
    {
        if (!WorkspaceFixture.HasGit) { Assert.Skip("git is not installed"); return; }
        var f = new WorkspaceFixture();
        var (repo, worktree) = f.MakeRepo();
        var vm = await f.StartAsync(repo);
        try
        {
            await vm.GitSettings.LoadAsync();
            var row = vm.GitSettings.Worktrees.Single(r => SessionCatalog.SamePath(r.Path, worktree));
            await row.AskRemoveCommand.ExecuteAsync(null);
            Assert.True(row.IsConfirming);
            vm.ComposerText = "Keep repository chat";
            await vm.OpenFolderInNewSessionAsync(worktree);
            vm.ComposerText = "Keep worktree chat";
            await vm.OpenFolderInNewSessionAsync(repo);
            Assert.True(SessionCatalog.SamePath(repo, vm.ProjectFolder));
            await row.ConfirmRemoveCommand.ExecuteAsync(null);
            Assert.False(row.IsConfirming);
            Assert.Contains(OpenChatProblem, row.Problem);
            Assert.True(Directory.Exists(worktree));
            Assert.DoesNotContain(f.ToolLog, IsRemoval);
        }
        finally { await vm.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task Chat_opened_during_final_status_probe_blocks_dispatch()
    {
        if (!WorkspaceFixture.HasGit) { Assert.Skip("git is not installed"); return; }
        var f = new WorkspaceFixture();
        var (repo, worktree) = f.MakeRepo();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = false;
        var vm = new MainViewModel(f.Session(repo), new Ombra.App.AppArgs())
        {
            OmpCliLaunch = TestProcesses.FakeCli(f.CliFile, f.CliLog),
            RunTool = async (command, ct) =>
            {
                var result = await f.RunTool(command, ct);
                if (armed && command.Args.SequenceEqual(new[] { "status", "--porcelain" })
                    && SessionCatalog.SamePath(command.WorkingDirectory, worktree))
                {
                    entered.TrySetResult();
                    await release.Task;
                }
                return result;
            },
        };
        try
        {
            vm.OnWindowOpened();
            await WorkspaceFixture.Until(() => vm.Phase == SessionPhase.Ready && vm.ProjectFolder is not null, "omp ready");
            await vm.GitSettings.LoadAsync();
            var row = vm.GitSettings.Worktrees.Single(r => SessionCatalog.SamePath(r.Path, worktree));
            await row.AskRemoveCommand.ExecuteAsync(null);
            Assert.True(row.IsConfirming);
            armed = true;
            var removal = row.ConfirmRemoveCommand.ExecuteAsync(null);
            await WorkspaceFixture.Until(() => entered.Task.IsCompleted, "final worktree status probe");
            vm.ComposerText = "Keep repository chat";
            await vm.OpenFolderInNewSessionAsync(worktree);
            release.TrySetResult();
            await removal;
            Assert.Contains(OpenChatProblem, row.Problem);
            Assert.True(Directory.Exists(worktree));
            Assert.DoesNotContain(f.ToolLog, IsRemoval);
        }
        finally
        {
            release.TrySetResult();
            await vm.DisposeAsync();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removal_reservation_refuses_chat_and_raw_input_until_git_finishes(bool failRemoval)
    {
        if (!WorkspaceFixture.HasGit) { Assert.Skip("git is not installed"); return; }
        var f = new WorkspaceFixture();
        var (repo, worktree) = f.MakeRepo();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new MainViewModel(f.Session(repo), new Ombra.App.AppArgs())
        {
            OmpCliLaunch = TestProcesses.FakeCli(f.CliFile, f.CliLog),
            RunTool = async (command, ct) =>
            {
                if (IsRemoval(command.ToString()))
                {
                    entered.TrySetResult();
                    await release.Task; // dispatched, but git has not started yet
                    if (failRemoval) return new ToolResult(1, "", "simulated removal failure");
                }
                return await f.RunTool(command, ct);
            },
        };
        try
        {
            vm.OnWindowOpened();
            await WorkspaceFixture.Until(() => vm.Phase == SessionPhase.Ready && vm.ProjectFolder is not null, "omp ready");
            await vm.GitSettings.LoadAsync();
            var row = vm.GitSettings.Worktrees.Single(r => SessionCatalog.SamePath(r.Path, worktree));
            await row.AskRemoveCommand.ExecuteAsync(null);
            Assert.True(row.IsConfirming);
            var removal = row.ConfirmRemoveCommand.ExecuteAsync(null);
            await WorkspaceFixture.Until(() => entered.Task.IsCompleted, "removal dispatch");
            vm.ComposerText = "/move " + worktree;
            await vm.SendCommand.ExecuteAsync(null);
            Assert.Equal("/move " + worktree, vm.ComposerText); // refused without consuming the draft
            await vm.OpenFolderInNewSessionAsync(worktree);
            Assert.Single(vm.OpenChats);
            Assert.True(SessionCatalog.SamePath(repo, vm.ProjectFolder));
            Assert.Equal(Loc.T("Worktree removal in progress"), vm.SessionCard?.Title);
            Assert.False((await vm.MoveSessionToNewWorktreeAsync("blocked")).Ok);
            release.TrySetResult();
            await removal;
            Assert.Equal(failRemoval ? "simulated removal failure" : "", row.Problem);
            Assert.Equal(failRemoval, Directory.Exists(worktree));
            Assert.All(vm.OpenChats, chat => Assert.True(Directory.Exists(chat.CurrentLaunch.WorkingDirectory)));
            // Both success and failure release the scope; a failed removal can be opened again.
            await vm.OpenFolderInNewSessionAsync(failRemoval ? worktree : repo);
            Assert.Equal(2, vm.OpenChats.Count);
        }
        finally
        {
            release.TrySetResult();
            await vm.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task Starting_chat_is_protected_but_a_sibling_prefix_is_not()
    {
        var f = new WorkspaceFixture();
        var worktree = Path.Combine(f.Dir, "tree");
        var nested = Path.Combine(worktree, "nested");
        Directory.CreateDirectory(nested);
        var vm = f.Vm(f.Session(nested)); // no snapshot cwd yet: the launch folder still counts
        try
        {
            Assert.True(vm.GitSettings.IsWorktreeInUse(worktree));
            Assert.True(vm.GitSettings.IsWorktreeInUse(nested));
            Assert.False(vm.GitSettings.IsWorktreeInUse(Path.Combine(f.Dir, "tre")));
            Assert.False(vm.GitSettings.IsWorktreeInUse(Path.Combine(f.Dir, "tree-other")));
        }
        finally { await vm.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task Missing_worktree_cleanup_only_removes_selected_registration()
    {
        if (!WorkspaceFixture.HasGit) { Assert.Skip("git is not installed"); return; }
        var f = new WorkspaceFixture();
        var (repo, worktree) = f.MakeRepo();
        var other = Path.Combine(f.Dir, "other-worktree");
        WorkspaceFixture.Git(repo, "worktree", "add", "-b", "other", other);
        var vm = await f.StartAsync(other);
        try
        {
            vm.ComposerText = "Keep missing worktree chat";
            await vm.OpenFolderInNewSessionAsync(repo);
            // Only these fixture-owned disposable worktrees are removed to simulate missing folders.
            Directory.Delete(worktree, recursive: true);
            Directory.Delete(other, recursive: true);
            await vm.GitSettings.LoadAsync();
            var protectedRow = vm.GitSettings.Worktrees.Single(r => SessionCatalog.SamePath(r.Path, other));
            Assert.True(protectedRow.Info.Prunable);
            await protectedRow.AskRemoveCommand.ExecuteAsync(null);
            Assert.Contains(OpenChatProblem, protectedRow.Problem);
            var row = vm.GitSettings.Worktrees.Single(r => SessionCatalog.SamePath(r.Path, worktree));
            Assert.True(row.Info.Prunable);
            await row.AskRemoveCommand.ExecuteAsync(null);
            Assert.True(row.IsConfirming);
            await row.ConfirmRemoveCommand.ExecuteAsync(null);
            Assert.Equal("", row.Problem);
            var listed = WorkspaceFixture.Git(repo, "worktree", "list", "--porcelain");
            Assert.Contains(SessionCatalog.ResolveLinks(other), listed);
            Assert.DoesNotContain(SessionCatalog.ResolveLinks(worktree), listed);
            Assert.DoesNotContain(f.ToolLog, c => c.StartsWith("git worktree prune", StringComparison.Ordinal));
        }
        finally { await vm.DisposeAsync(); }
    }

    private static bool IsRemoval(string command) => command.StartsWith("git worktree remove", StringComparison.Ordinal)
        || command.StartsWith("git worktree prune", StringComparison.Ordinal);
}
