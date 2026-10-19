#if OMBRA_FEATURE_SMOKE
using System.Text.Json.Nodes;
using Ombra.ClientCore.Teams;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] NightlyShareSections() => [("nightly-share", NightlyShare)];

        /// <summary>Ivy's Ombra as the Nightly link sees it (her session in-process).</summary>
        private sealed class SessionSource : INightlyTeamSource
        {
            private readonly TeamSession _session;

            public SessionSource(TeamSession session)
            {
                _session = session;
                session.Items.ItemsChanged += _ => Changed?.Invoke();
            }

            public event Action? Changed;

            public NightlyTeamState Current => _session.Last is { } last
                ? new NightlyTeamState(_session.Items, last.MemberId, last.Teams, null)
                : NightlyTeamState.Unavailable(NightlyTeamShare.NotReady);
        }

        private static async Task<bool> UntilAsync(Func<Task<bool>> condition, double seconds)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (!await condition())
            {
                if (DateTime.UtcNow > end) return false;
                await Task.Delay(500);
            }
            return true;
        }

        /// <summary>Waits for <paramref name="name"/> in the hand-off folder (written by the Nightly driver).</summary>
        private static async Task<bool> Marker(string folder, string name, double seconds) =>
            await Until(() => File.Exists(Path.Combine(folder, name)), seconds);

        /// <summary>
        /// B2 end to end with a standalone dev Nightly (driven over Marionette by nightly-share-driver.py, which hands off
        /// through files in <c>&lt;shots&gt;/nightly-share/</c>): this Ombra signs into a team on the real server
        /// (OMBRA_FEATURE_SMOKE_TEAM) and Ivy joins in-process; Nightly shares a page with a comment (its <c>?token=</c>
        /// stripped by the redactor), a tab group and a gallery screenshot through this Ombra's Mach link; Ivy receives
        /// all three and opens them (the screenshot's blob downloads as a JPEG); Ivy shares a link and a group back,
        /// Nightly's toasts open them and Ivy sees them accepted.
        /// </summary>
        private async Task NightlyShare()
        {
            if (Environment.GetEnvironmentVariable("OMBRA_FEATURE_SMOKE_TEAM") is not { } spec || spec.Split('|') is not [var address, var pin, var code])
            {
                Check("nightly-share-server", false, "OMBRA_FEATURE_SMOKE_TEAM not set (team-server.sh up)");
                return;
            }
            var hand = Path.Combine(dir, "nightly-share");
            Directory.CreateDirectory(hand);
            var t = vm.Team;
            if (Environment.GetEnvironmentVariable("OMBRA_FEATURE_SMOKE_ONLY")?.Split(',').Contains("nightly-share") != true)
            {
                Log("nightly-share skipped: run it by name, with nightly-share-driver.py");
                return;
            }
            vm.OpenTeamPageCommand.Execute(null);
            t.ServerAddress = address;
            t.ServerPin = pin;
            t.BootstrapCode = code;
            await t.CreateTeamCommand.ExecuteAsync(null);
            t.DismissRecoveryKeyCommand.Execute(null);
            t.DisplayName = "Olga";
            await t.SaveNameCommand.ExecuteAsync(null);
            await t.CreateInviteCommand.ExecuteAsync(null);
            using var ivy = await Task.Run(() => TeamSession.JoinAsync(Path.Combine(dir, "team-others", "ivy"), null, t.Invite!, new MemoryKeys(), new Machine("ivy")));
            await Task.Run(() => ivy.SetNameAsync("Ivy"));
            await RefreshTeam();
            await t.JoinRows[0].ApproveCommand.ExecuteAsync(null);
            await Task.Run(async () => { await ivy.RefreshAsync(); await ivy.RefreshAsync(); });
            await RefreshTeam();
            var team = t.SelectedTeamId!;
            var olgaId = TeamsHex.Of(t.Session!.Last!.MemberId);
            var ivyId = TeamsHex.Of(ivy.Last!.MemberId);
            if (!Check("nightly-share-team", t.MemberRows.Count == 2, $"rows={t.MemberRows.Count} error={t.Error}")) return;
            Shot("80-nightly-share-team");

            var ivys = new NightlyTeamShare(new SessionSource(ivy));
            Task<JsonObject> Ask(JsonObject request) => Task.Run(() => ivys.HandleAsync(request, CancellationToken.None));
            File.WriteAllText(Path.Combine(hand, "ready"), team);
            if (!Check("nightly-sent", await Marker(hand, "sent", 600), "the driver did not finish sharing")) return;

            // Ivy receives the page (comment, no token), the group and the screenshot.
            JsonArray inbox = [];
            await UntilAsync(async () =>
            {
                await Task.Run(() => ivy.Items.DeliverAsync());
                inbox = (await Ask(new JsonObject { ["op"] = "inbox", ["after"] = 0, ["wait"] = 0 }))["items"]!.AsArray();
                return inbox.Count >= 3;
            }, 60);
            Log($"ivy inbox: {inbox.ToJsonString()}");
            var link = inbox.FirstOrDefault(i => (string?)i!["kind"] == "link");
            Check("ivy-got-page", link is not null && (string?)link["authorName"] == "Olga" && (string?)link["comment"] == "Read before standup"
                && ((string?)link["url"])?.Contains("token=") == false && ((string?)link["url"])?.Contains("keep=1") == true, link?.ToJsonString() ?? "none");
            var group = inbox.FirstOrDefault(i => (string?)i!["kind"] == "collection");
            Check("ivy-got-group", group?["links"]?.AsArray().Count >= 2 && ((string?)group["title"])?.StartsWith("example.com", StringComparison.Ordinal) == true,
                group?.ToJsonString() ?? "none");
            var shot = inbox.FirstOrDefault(i => (string?)i!["kind"] == "screenshot");
            Check("ivy-got-screenshot", shot is not null, shot?.ToJsonString() ?? "none");
            foreach (var item in new[] { link, group, shot }.OfType<JsonNode>())
            {
                var opened = await Ask(new JsonObject { ["op"] = "open", ["team"] = team, ["author"] = olgaId, ["id"] = (string?)item["id"] });
                Check($"ivy-opened-{item["kind"]}", (bool?)opened["ok"] == true, (string?)opened["error"] ?? "");
                if ((string?)item["kind"] == "screenshot")
                {
                    var jpeg = Convert.FromBase64String((string?)opened["data"] ?? "");
                    Check("ivy-screenshot-jpeg", jpeg.Length > 1000 && jpeg[0] == 0xFF && jpeg[1] == 0xD8, $"bytes={jpeg.Length}");
                    File.WriteAllBytes(Path.Combine(dir, "83-ivy-received-screenshot.jpg"), jpeg);
                }
            }
            await Task.Run(() => ivy.Items.DeliverAsync());
            Check("olga-sees-accepted", await UntilAsync(async () =>
            {
                await Task.Run(() => t.Items!.DeliverAsync());
                return TeamShare.Sent(t.Items!.List(team), olgaId) is { Count: 3 } s && s.All(x => x.Copies.All(c => c.Status == TeamItemStatuses.Accepted));
            }, 60));
            await RefreshTeam();
            Shot("81-nightly-share-sent-accepted");

            // Ivy shares back: Nightly's toasts open them.
            var back = await Ask(new JsonObject
            {
                ["op"] = "share", ["team"] = team, ["to"] = new JsonArray(olgaId),
                ["item"] = new JsonObject { ["kind"] = "link", ["url"] = "https://example.com/?from-ivy", ["title"] = "From Ivy", ["comment"] = "Look" },
            });
            Check("ivy-shared-link", (bool?)back["ok"] == true, back.ToJsonString());
            File.WriteAllText(Path.Combine(hand, "incoming-link"), "");
            if (!Check("nightly-opened-link", await Marker(hand, "opened-link", 300))) return;
            back = await Ask(new JsonObject
            {
                ["op"] = "share", ["team"] = team, ["to"] = new JsonArray(olgaId),
                ["item"] = new JsonObject
                {
                    ["kind"] = "collection", ["title"] = "Ivy refs",
                    ["links"] = new JsonArray(
                        new JsonObject { ["url"] = "https://example.com/?ivy-ref=1", ["title"] = "1" },
                        new JsonObject { ["url"] = "https://example.org/?ivy-ref=2", ["title"] = "2" }),
                },
            });
            Check("ivy-shared-group", (bool?)back["ok"] == true, back.ToJsonString());
            File.WriteAllText(Path.Combine(hand, "incoming-group"), "");
            if (!Check("nightly-opened-group", await Marker(hand, "opened-group", 300))) return;
            Check("ivy-sees-accepted", await UntilAsync(async () =>
            {
                await Task.Run(() => ivy.Items.DeliverAsync());
                return TeamShare.Sent(ivy.Items.List(team), ivyId) is { Count: 2 } s && s.All(x => x.Copies.All(c => c.Status == TeamItemStatuses.Accepted));
            }, 60));
            await RefreshTeam();
            Shot("82-nightly-share-done");
            File.WriteAllText(Path.Combine(hand, "done"), "");
        }
    }
}
#endif
