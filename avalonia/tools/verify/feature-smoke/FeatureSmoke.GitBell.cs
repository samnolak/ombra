#if OMBRA_FEATURE_SMOKE
using System.Diagnostics;
using Avalonia.VisualTree;
using Ombra.App.Services;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] GitBellSections() => [("git-bell", GitBell)];

        private static string SmokeGit(string cwd, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
            var who = cwd.EndsWith("bob", StringComparison.Ordinal) ? "Bob" : "Me";
            foreach (var a in (string[])["-c", "user.name=" + who, "-c", "user.email=smoke@example.com", "-c", "commit.gpgsign=false", .. args]) psi.ArgumentList.Add(a);
            psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return output.Trim();
        }

        /// <summary>A bare remote, my clone and Bob's; Bob pushes <paramref name="commits"/> commits to b.txt.</summary>
        private string SmokeRepos(string name, int commits)
        {
            var root = Path.Combine(dir, "git-bell", name);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            var remote = Path.Combine(root, "remote.git");
            Directory.CreateDirectory(remote);
            SmokeGit(remote, "init", "--bare", "--quiet", "--initial-branch=main");
            var seed = Path.Combine(root, "seed");
            Directory.CreateDirectory(seed);
            SmokeGit(seed, "init", "--quiet", "--initial-branch=main");
            File.WriteAllText(Path.Combine(seed, "a.txt"), "a\n");
            File.WriteAllText(Path.Combine(seed, "b.txt"), "b\n");
            SmokeGit(seed, "add", ".");
            SmokeGit(seed, "commit", "--quiet", "-m", "seed");
            SmokeGit(seed, "push", "--quiet", remote, "main");
            SmokeGit(root, "clone", "--quiet", remote, name);
            SmokeGit(root, "clone", "--quiet", remote, "bob");
            var bob = Path.Combine(root, "bob");
            for (var i = 0; i < commits; i++)
            {
                File.AppendAllText(Path.Combine(bob, "b.txt"), $"bob {i}\n");
                SmokeGit(bob, "commit", "--quiet", "-am", $"bob {i}");
            }
            SmokeGit(bob, "push", "--quiet", "origin", "main");
            return Path.Combine(root, name);
        }

        /// <summary>
        /// Four project chats whose remotes Bob pushed to, one per risk (clean, my changes elsewhere, overlap, diverged):
        /// the bell's badge and list in dark and light, the composer's "behind" hint, and the clean one's Update.
        /// </summary>
        private async Task GitBell()
        {
            var clean = SmokeRepos("clean", 3);
            var keep = SmokeRepos("keep", 1);
            File.AppendAllText(Path.Combine(keep, "a.txt"), "mine\n");
            var overlap = SmokeRepos("overlap", 2);
            File.AppendAllText(Path.Combine(overlap, "b.txt"), "mine\n");
            var diverged = SmokeRepos("diverged", 1);
            File.WriteAllText(Path.Combine(diverged, "c.txt"), "c\n");
            SmokeGit(diverged, "add", ".");
            SmokeGit(diverged, "commit", "--quiet", "-m", "mine");
            foreach (var folder in new[] { keep, overlap, diverged, clean })
            {
                // A chat with a message stays open next to the others (an empty one is reused for the next folder)
                await vm.OpenFolderInNewSessionAsync(folder);
                await Until(() => vm.Phase == SessionPhase.Ready, 60);
                await Prompt($"Hello from {Path.GetFileName(folder)}");
            }
            vm.WatchGitRemotes = true;
            // The folders are not trusted: the background check does not fetch them; Check now does
            await vm.Bell.CheckAsync();
            Check("bell-untrusted-not-fetched", vm.Bell.Repos.Count == 0, $"rows={vm.Bell.Repos.Count}");
            await vm.Bell.CheckNowCommand.ExecuteAsync(null);
            // (the smoke's work folder lies inside this source checkout: its row is not one of the four)
            var mine = vm.Bell.Repos.Where(r => r.Top.Contains(Path.Combine("git-bell", ""), StringComparison.Ordinal)).ToList();
            var risks = string.Join(",", vm.Bell.Repos.Select(r => $"{r.Name}:{r.Risk}"));
            Check("bell-four-risks", mine.Select(r => r.Risk).Order().SequenceEqual(
                [UpdateRisk.Clean, UpdateRisk.KeepChanges, UpdateRisk.Overlap, UpdateRisk.Diverged]), risks);
            Check("bell-badge", vm.Bell.HasBadge && vm.Bell.BadgeText == "4", $"badge={vm.Bell.BadgeText}");
            var button = window.GetVisualDescendants().OfType<GitBellButton>().FirstOrDefault();
            Check("bell-in-header", button?.IsEffectivelyVisible == true);
            Activate();
            await Task.Delay(400);
            Shot("50-bell-badge-dark");

            vm.Bell.IsOpen = true;
            await Task.Delay(600);
            ShotScreenRegion("51-bell-list-dark");
            vm.SettingsTheme = "light";
            await Task.Delay(600);
            ShotScreenRegion("51-bell-list-light");
            vm.Bell.IsOpen = false;

            // The shown chat (the clean repository) is behind: typing shows the hint, Send is not blocked
            vm.ComposerText = "Add a README section";
            await Task.Delay(300);
            Check("hint-behind", vm.UpdateFirstHint?.Contains('3') == true, vm.UpdateFirstHint ?? "null");
            Shot("52-update-first-hint-light");
            vm.SettingsTheme = "dark";
            await Task.Delay(400);
            Shot("52-update-first-hint-dark");
            vm.ComposerText = "";

            var row = vm.Bell.Repos.First(r => r.Risk == UpdateRisk.Clean);
            await row.UpdateCommand.ExecuteAsync(null);
            Check("bell-clean-updated", !vm.Bell.Repos.Any(r => r.Top == row.Top), $"message={row.Message}");
            var overlapRow = vm.Bell.Repos.First(r => r.Risk == UpdateRisk.Overlap);
            overlapRow.AskAgentCommand.Execute(null);
            Check("bell-ask-agent-prefills", vm.ComposerText.Contains("b.txt") && vm.ComposerText.Contains("rebase"), vm.ComposerText);
            vm.ComposerText = "";
            vm.Bell.IsOpen = true;
            await Task.Delay(600);
            ShotScreenRegion("53-bell-after-update-dark");
            vm.Bell.IsOpen = false;
        }
    }
}
#endif
