using System.Text.Json.Nodes;
using Ombra.ClientCore.Teams;
using Ombra.Teams.Tests.Infra;

namespace Ombra.Teams.Tests;

/// <summary>
/// B1/B2 from Nightly through Ombra (SERVER_DESIGN §7) over the real server and ombra-core: Nightly's requests to each
/// member's Ombra (<see cref="NightlyTeamShare"/> on that member's <see cref="TeamSession"/>) share a page and a tab group,
/// the recipient's inbox long poll wakes with them, open accepts and the sender sees the status.
/// </summary>
public sealed class NightlyShareServerTests(ServerStack pg)
{
    /// <summary>One member's Ombra as the Nightly link sees it.</summary>
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

    private static string Root(TeamServerHost host, string name) => Path.Combine(host.Root, "accounts", name);

    private static Task<JsonObject> Ask(NightlyTeamShare share, JsonObject request) => share.HandleAsync(request, CancellationToken.None);

    /// <summary>Delivers until <paramref name="done"/> (as the session's background loop would).</summary>
    private static async Task Until(ITeamItems items, Func<bool> done)
    {
        for (var i = 0; i < 50 && !done(); i++)
        {
            await items.DeliverAsync();
            if (done()) return;
            await Task.Delay(200);
        }
        Assert.True(done(), "the items did not arrive");
    }

    [Fact]
    public async Task A_page_and_a_tab_group_from_Nightly_reach_the_member_and_open_accepts_them()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        using var owner = await TeamSession.ConnectAsync(Root(host, "owner"), host.Address, host.Pin, new MemoryKeyStore(), new FixedMachine("owner"));
        var created = await owner.CreateTeamAsync(await host.BootstrapCodeAsync());
        await owner.SetNameAsync("Olga");
        using var ivy = await TeamSession.JoinAsync(Root(host, "ivy"), null, await owner.InviteAsync(created.TeamId, "member"), new MemoryKeyStore(), new FixedMachine("ivy"));
        await ivy.SetNameAsync("Ivy");
        await owner.ApproveAsync(Assert.Single((await owner.RefreshAsync()).Teams[0].PendingJoins));
        await ivy.RefreshAsync();
        await owner.RefreshAsync();
        var team = created.TeamId;
        var olgaId = TeamsHex.Of(owner.Last!.MemberId);
        var ivyId = TeamsHex.Of(ivy.Last!.MemberId);

        // Olga's Nightly asks her Ombra who can receive.
        var olga = new NightlyTeamShare(new SessionSource(owner));
        var ivys = new NightlyTeamShare(new SessionSource(ivy));
        var state = await Ask(olga, new JsonObject { ["op"] = "state" });
        Assert.True((bool)state["ready"]!, state.ToJsonString());
        var member = Assert.Single(state["teams"]![0]!["members"]!.AsArray())!;
        Assert.Equal("Ivy", (string?)member["name"]);
        Assert.Equal(ivyId, (string?)member["id"]);

        // Ivy's Nightly waits for something shared with her.
        var waiting = Ask(ivys, new JsonObject { ["op"] = "inbox", ["after"] = 0, ["wait"] = 20000 });

        var page = await Ask(olga, new JsonObject
        {
            ["op"] = "share", ["team"] = team, ["to"] = "all",
            ["item"] = new JsonObject { ["kind"] = "link", ["url"] = "https://example.com/spec", ["title"] = "Spec", ["comment"] = "Read before standup" },
        });
        Assert.True((bool)page["ok"]!, page.ToJsonString());
        var group = await Ask(olga, new JsonObject
        {
            ["op"] = "share", ["team"] = team, ["to"] = new JsonArray(ivyId),
            ["item"] = new JsonObject
            {
                ["kind"] = "collection", ["title"] = "pinterest.com (2)",
                ["links"] = new JsonArray(
                    new JsonObject { ["url"] = "https://pinterest.com/pin/1", ["title"] = "Pin 1" },
                    new JsonObject { ["url"] = "https://pinterest.com/pin/2", ["title"] = "Pin 2" }),
            },
        });
        Assert.True((bool)group["ok"]!, group.ToJsonString());

        await Until(ivy.Items, () => TeamShare.Incoming(ivy.Items.List(team), ivyId).Count >= 2);
        var first = await waiting.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotEmpty(first["items"]!.AsArray());
        var inbox = (await Ask(ivys, new JsonObject { ["op"] = "inbox", ["after"] = 0, ["wait"] = 0 }))["items"]!.AsArray();
        var link = inbox.Single(i => (string?)i!["kind"] == "link")!;
        Assert.Equal("Olga", (string?)link["authorName"]);
        Assert.Equal("https://example.com/spec", (string?)link["url"]);
        Assert.Equal("Read before standup", (string?)link["comment"]);
        var collection = inbox.Single(i => (string?)i!["kind"] == "collection")!;
        Assert.Equal("pinterest.com (2)", (string?)collection["title"]); // the group's name travels in the collection

        var opened = await Ask(ivys, new JsonObject { ["op"] = "open", ["team"] = team, ["author"] = olgaId, ["id"] = (string?)link["id"] });
        Assert.True((bool)opened["ok"]!, opened.ToJsonString());
        Assert.Equal("https://example.com/spec", (string?)opened["url"]);
        opened = await Ask(ivys, new JsonObject { ["op"] = "open", ["team"] = team, ["author"] = olgaId, ["id"] = (string?)collection["id"] });
        Assert.True((bool)opened["ok"]!, opened.ToJsonString());
        Assert.Equal(["https://pinterest.com/pin/1", "https://pinterest.com/pin/2"], opened["links"]!.AsArray().Select(l => (string)l!["url"]!));
        Assert.Empty((await Ask(ivys, new JsonObject { ["op"] = "inbox", ["after"] = 0, ["wait"] = 0 }))["items"]!.AsArray());

        // Olga sees Ivy accepted both.
        await ivy.Items.DeliverAsync();
        await Until(owner.Items, () => TeamShare.Sent(owner.Items.List(team), olgaId) is { Count: 2 } s
            && s.All(x => x.Copies.All(c => c.Status == TeamItemStatuses.Accepted)));
    }
}
