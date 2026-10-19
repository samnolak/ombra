#if OMBRA_FEATURE_SMOKE
using Avalonia.Controls;
using Avalonia.VisualTree;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore.Teams;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] ShareSections() => [("team-share", ShareFlow)];

        /// <summary>
        /// P5 resources in the real window over an in-memory team (Alice = this window, Bob and Carol): Share a file to
        /// Bob found by name → his Accept shows on Alice's Sent; Bob shares a skill, a link and a patch → toast, rail
        /// badge, Inbox with previews; Accept installs the skill into omp's user skills and opens the link in the Browser
        /// page; Decline leaves the patch unapplied.
        /// </summary>
        private async Task ShareFlow()
        {
            Activate();
            var hub = new SmokeHub();
            var alice = new SmokeTeam(hub, SmokeHub.Alice);
            var bob = new SmokeItems(hub, TeamsHex.Of(SmokeHub.Bob));
            var a = TeamsHex.Of(SmokeHub.Alice);
            vm.Share.Attach(alice);
            await Until(() => vm.Share.Refreshed.IsCompleted, 5);
            Check("share-attach", vm.Share.CanShare);

            // Alice shares a file with Bob, picked by name
            var root = vm.ProjectRoot ?? Environment.CurrentDirectory;
            var file = Path.Combine(root, "share-demo.md");
            File.WriteAllText(file, "# Release notes\n\n- faster start\n");
            vm.ShareFile(file);
            Check("share-dialog-open", await Until(() => vm.Share.IsDialogOpen
                && window.GetVisualDescendants().OfType<Control>().Any(c => c.Name == "ShareDialog" && c.IsVisible), 5));
            vm.Share.Recipients.IsSpecific = true;
            vm.Share.Recipients.Filter = "bo";
            await Task.Delay(200);
            if (vm.Share.Recipients.Members.FirstOrDefault() is { } row) row.IsSelected = true;
            vm.Share.Comment = "For the release";
            await Task.Delay(500);
            Shot("70-share-dialog");
            var send = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "ShareSend" && b.IsEffectivelyVisible);
            Check("share-send-visible", send is not null);
            if (send is null) return;
            Click(send);
            Check("share-sent-to-bob", await Until(() => hub.Items.Count == 1 && hub.Items[0].RecipientMemberId == bob.Me && !vm.Share.IsDialogOpen, 5),
                $"items={hub.Items.Count}");

            // Bob accepts: Alice sees it on the Team page
            await bob.SetStatusAsync(SmokeHub.Team, a, hub.Items[0].ItemId, TeamItemStatuses.Accepted);
            Check("share-sender-status", await Until(() => vm.Share.Sent.FirstOrDefault()?.Recipients.FirstOrDefault()?.StatusText == Loc.T("Accepted"), 5));
            vm.OpenTeamPageCommand.Execute(null);
            await Task.Delay(800);
            window.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "TeamSentCard")?.BringIntoView();
            await Task.Delay(400);
            Shot("71-share-sent-status");
            vm.IsTeamPageOpen = false;
            await Task.Delay(300);

            // Bob shares a skill, a link and a patch with Alice
            using var site = SmokeSite.Start();
            var skillDir = Path.Combine(Path.GetTempPath(), "ombra-smoke-skill-" + Guid.NewGuid().ToString("N")[..6], "release-check");
            Directory.CreateDirectory(Path.Combine(skillDir, "scripts"));
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: release-check\ndescription: Checks a release\n---\nRun scripts/check.sh\n");
            File.WriteAllText(Path.Combine(skillDir, "scripts", "check.sh"), "#!/bin/sh\necho ok\n");
            var skillsRoot = await vm.UserSkillsFolderAsync();
            var installed = Path.Combine(skillsRoot, "release-check");
            if (Directory.Exists(installed)) Directory.Delete(installed, true);
            await TeamShare.SkillFrom(skillDir).SendAsync(bob, SmokeHub.Team, [a], null);
            var toast = Loc.F("{0} shared {1} “{2}”", "Bob", Loc.T("a skill"), "release-check");
            Check("share-toast", await Until(() => vm.SessionCard?.Title == toast, 5), $"card='{vm.SessionCard?.Title}'");
            Check("share-skill-not-installed-before-accept", !Directory.Exists(installed));
            await Task.Delay(400);
            Shot("72-share-toast");
            await TeamShare.Link(site.Url, "Shop").SendAsync(bob, SmokeHub.Team, [a], null);
            await TeamShare.Edit("Changes in demo", "diff --git a/share-demo.md b/share-demo.md\n--- a/share-demo.md\n+++ b/share-demo.md\n@@ -1 +1 @@\n-# Release notes\n+# Release notes (v2)\n",
                    await RepoIdentity.OfAsync(root) ?? new RepoIdentity(new string('0', 32), new string('0', 40)))
                .SendAsync(bob, SmokeHub.Team, [a], null);
            Check("share-badge", await Until(() => vm.Share.InboxCount == 3, 5)
                && window.GetVisualDescendants().OfType<Border>().Any(b => b.Name == "RailTeamBadge" && b.IsEffectivelyVisible), $"count={vm.Share.InboxCount}");
            ShotScreenRegion("73-share-rail-badge");

            // Open from the toast: the Team page's Inbox
            vm.SessionCard?.Primary?.Command.Execute(null);
            Check("share-inbox-open", await Until(() => vm.IsTeamPageOpen, 5));
            await Task.Delay(800);
            window.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "TeamInboxCard")?.BringIntoView();
            await Task.Delay(400);
            Shot("74-share-inbox");
            Check("share-seen-reaches-bob", await Until(() => hub.Items.Where(i => i.RecipientMemberId == a).All(i => i.Statuses.Any(s => s.Seen)), 5));

            // Accept the skill → installed in omp's user skills; Decline the patch → nothing changes
            var skillRow = vm.Share.Inbox.First(r => r.Item.Kind == TeamItemKinds.Skill);
            await skillRow.AcceptCommand.ExecuteAsync(null);
            Check("share-skill-installed", File.Exists(Path.Combine(installed, "SKILL.md")) && File.Exists(Path.Combine(installed, "scripts", "check.sh")), installed);
            var patchRow = vm.Share.Inbox.First(r => r.Item.Kind == TeamItemKinds.Edit);
            await patchRow.DeclineCommand.ExecuteAsync(null);
            Check("share-patch-declined", File.ReadAllText(file).StartsWith("# Release notes\n", StringComparison.Ordinal));
            await Task.Delay(500);
            Shot("75-share-inbox-answered");
            var bobSees = bob.List(SmokeHub.Team).Where(i => i.AuthorMemberId == bob.Me).Select(i => (i.Kind, TeamShare.MyStatus(i, a))).ToList();
            Check("share-statuses-reach-bob", bobSees.Contains((TeamItemKinds.Skill, TeamItemStatuses.Accepted)) && bobSees.Contains((TeamItemKinds.Edit, TeamItemStatuses.Declined)),
                string.Join(" ", bobSees));

            // Accept the link → it opens in the Browser page
            var linkRow = vm.Share.Inbox.First(r => r.Item.Kind == TeamItemKinds.Link);
            await linkRow.AcceptCommand.ExecuteAsync(null);
            Check("share-link-opened", await Until(() => vm.IsBrowserPageOpen && vm.Browser.ActiveTab.Url?.AbsoluteUri.StartsWith(site.Url, StringComparison.Ordinal) == true, 15),
                $"url={vm.Browser.ActiveTab.Url}");
            await Task.Delay(1500);
            Shot("76-share-link-browser");
            vm.IsBrowserPageOpen = false;
            if (Directory.Exists(installed)) Directory.Delete(installed, true);
            File.Delete(file);
        }
    }
}
#endif
