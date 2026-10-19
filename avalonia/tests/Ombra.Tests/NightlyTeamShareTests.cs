using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Ombra.App.Nightly;
using Ombra.ClientCore.Teams;

namespace Ombra.Tests;

/// <summary>
/// B1/B2 on the Nightly side through Ombra (SERVER_DESIGN §7): Nightly's requests (state, share a page / tab group /
/// screenshot to everyone or to members, the inbox long poll, open = accept, decline) against <see cref="ITeamItems"/>
/// fakes of three members, and the Mach channel itself (<see cref="NightlyTeamLink"/>) with a real round trip.
/// </summary>
public sealed class NightlyTeamShareTests
{
    private const string Team = SharedMarksTests.Team;
    private static readonly string A = TeamsHex.Of(SharedMarksTests.AliceId);
    private static readonly string B = TeamsHex.Of(SharedMarksTests.BobId);
    private static readonly string C = TeamsHex.Of(SharedMarksTests.CarolId);

    private sealed class Source(ITeamItems? items, Guid me, string? problem = null) : INightlyTeamSource
    {
        public string? Problem { get; set; } = problem;
        public event Action? Changed;
        public void Raise() => Changed?.Invoke();

        public NightlyTeamState Current => Problem is { } p ? NightlyTeamState.Unavailable(p) : new NightlyTeamState(items, me, [View(me)], null);

        private static TeamView View(Guid me)
        {
            TeamMember M(Guid id, string name, string role) => new(id, role, name, [], id == me, "fp");
            var roster = new TeamRoster(Team, 3, SharedMarksTests.AliceId,
                [M(SharedMarksTests.AliceId, "Alice", "owner"), M(SharedMarksTests.BobId, "Bob", "member"), M(SharedMarksTests.CarolId, "Carol", "member")], true);
            return new TeamView(Team, roster, false, me == SharedMarksTests.AliceId, null, [], null, null, []);
        }
    }

    private static (NightlyTeamShare Share, Source Source, FakeHub Hub) Member(FakeHub hub, Guid id)
    {
        var items = new FakeTeamItems(hub, TeamsHex.Of(id));
        var source = new Source(items, id);
        items.ItemsChanged += _ => source.Raise();
        return (new NightlyTeamShare(source), source, hub);
    }

    private static Task<JsonObject> Ask(NightlyTeamShare share, string json) => share.HandleAsync((JsonObject)JsonNode.Parse(json)!, CancellationToken.None);

    [Fact]
    public async Task State_names_the_other_members_or_says_why_sharing_is_off()
    {
        var hub = new FakeHub();
        var (alice, source, _) = Member(hub, SharedMarksTests.AliceId);

        var state = await Ask(alice, """{"op":"state"}""");
        Assert.True((bool)state["ready"]!);
        Assert.Equal(A, (string?)state["me"]);
        var members = state["teams"]![0]!["members"]!.AsArray();
        Assert.Equal(["Bob", "Carol"], members.Select(m => (string)m!["name"]!));
        Assert.Equal([B, C], members.Select(m => (string)m!["id"]!));

        foreach (var reason in new[] { NightlyTeamShare.SignedOut, NightlyTeamShare.NoTeam, NightlyTeamShare.NotReady })
        {
            source.Problem = reason;
            state = await Ask(alice, """{"op":"state"}""");
            Assert.False((bool)state["ready"]!);
            Assert.Equal(reason, (string?)state["reason"]);
            var share = await Ask(alice, $$$$"""{"op":"share","team":"{{{{Team}}}}","to":"all","item":{"kind":"link","url":"https://example.com/"}}""");
            Assert.False((bool)share["ok"]!);
            Assert.Equal(reason, (string?)share["error"]);
        }
        Assert.Empty(hub.Items);
    }

    [Fact]
    public async Task A_page_goes_to_everyone_else_with_its_comment_and_a_member_gets_only_theirs()
    {
        var hub = new FakeHub();
        var (alice, _, _) = Member(hub, SharedMarksTests.AliceId);

        var sent = await Ask(alice, $$$$"""{"op":"share","team":"{{{{Team}}}}","to":"all","item":{"kind":"link","url":"https://example.com/a?b=1","title":"Example","comment":"Look"}}""");
        Assert.True((bool)sent["ok"]!, sent.ToJsonString());
        Assert.Equal(2, (int)sent["sent"]!);
        Assert.Equal([B, C], hub.Items.Select(i => i.RecipientMemberId).Order());
        var item = hub.Items[0];
        Assert.Equal(TeamItemKinds.Link, item.Kind);
        Assert.Equal("https://example.com/a?b=1", TeamShare.Url(item));
        Assert.Equal("Example", TeamShare.Title(item));
        Assert.Equal("Look", TeamShare.Comment(item));

        hub.Items.Clear();
        sent = await Ask(alice, $$$$"""{"op":"share","team":"{{{{Team}}}}","to":["{{{{C}}}}"],"item":{"kind":"link","url":"https://example.com/"}}""");
        Assert.Equal(1, (int)sent["sent"]!);
        Assert.Equal(C, Assert.Single(hub.Items).RecipientMemberId);
    }

    [Theory]
    [InlineData("""{"kind":"link","url":"javascript:alert(1)"}""", "all")]
    [InlineData("""{"kind":"link","url":"https://user:pw@example.com/"}""", "all")]
    [InlineData("""{"kind":"collection","title":"Empty","links":[]}""", "all")]
    [InlineData("""{"kind":"skill","name":"x"}""", "all")]
    [InlineData("""{"kind":"link","url":"https://example.com/"}""", "[\"ffff\"]")]
    [InlineData("""{"kind":"link","url":"https://example.com/"}""", "[]")]
    public async Task Bad_items_and_strangers_are_refused_and_nothing_is_sent(string item, string to)
    {
        var hub = new FakeHub();
        var (alice, _, _) = Member(hub, SharedMarksTests.AliceId);
        var answer = await Ask(alice, $$$$"""{"op":"share","team":"{{{{Team}}}}","to":{{{{(to == "all" ? "\"all\"" : to)}}}},"item":{{{{item}}}}}""");
        Assert.False((bool)answer["ok"]!);
        Assert.Equal("bad-request", (string?)answer["error"]);
        Assert.Empty(hub.Items);
    }

    [Fact]
    public async Task A_tab_group_is_a_collection_and_a_screenshot_an_image_that_must_fit()
    {
        var hub = new FakeHub();
        var (alice, _, _) = Member(hub, SharedMarksTests.AliceId);

        var group = await Ask(alice, $$$$"""
            {"op":"share","team":"{{{{Team}}}}","to":["{{{{B}}}}"],"item":{"kind":"collection","title":"pinterest.com (4)",
             "links":[{"url":"https://pinterest.com/1","title":"1"},{"url":"https://pinterest.com/2","title":"2"}]}}
            """);
        Assert.True((bool)group["ok"]!, group.ToJsonString());
        var collection = Assert.Single(hub.Items, i => i.Kind == TeamItemKinds.Collection);
        Assert.Equal(["https://pinterest.com/1", "https://pinterest.com/2"], TeamShare.Links(collection, hub.Items).Select(l => l.Url));
        Assert.Equal(3, hub.Items.Count); // the two pages, then the collection naming them

        hub.Items.Clear();
        var jpeg = new byte[30_000];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        var shot = await Ask(alice, $$$$"""
            {"op":"share","team":"{{{{Team}}}}","to":["{{{{B}}}}"],"item":{"kind":"screenshot","title":"Board","url":"https://example.com/board",
             "jpeg":"{{{{Convert.ToBase64String(jpeg)}}}}"}}
            """);
        Assert.True((bool)shot["ok"]!, shot.ToJsonString());
        var snapshot = Assert.Single(hub.Items);
        Assert.Equal(TeamItemKinds.Snapshot, snapshot.Kind);
        Assert.Equal("https://example.com/board", TeamShare.SourceUrl(snapshot));
        Assert.Equal(30_000UL, TeamShare.Size(snapshot));
        Assert.NotNull(TeamBlob.KeyOf(snapshot, snapshot.Body!["blobRef"]!.GetValue<string>())); // the key travels with the item
    }

    [Fact]
    public async Task The_inbox_wakes_on_a_share_open_accepts_and_decline_declines()
    {
        var hub = new FakeHub();
        var (alice, _, _) = Member(hub, SharedMarksTests.AliceId);
        var (bob, _, _) = Member(hub, SharedMarksTests.BobId);

        var empty = await Ask(bob, """{"op":"inbox","after":0,"wait":50}""");
        Assert.Empty(empty["items"]!.AsArray());

        var waiting = Ask(bob, """{"op":"inbox","after":0,"wait":10000}""");
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        await Ask(alice, $$$$"""{"op":"share","team":"{{{{Team}}}}","to":"all","item":{"kind":"link","url":"https://example.com/x","title":"X","comment":"for you"}}""");
        var inbox = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        var incoming = Assert.Single(inbox["items"]!.AsArray())!;
        Assert.Equal("Alice", (string?)incoming["authorName"]);
        Assert.Equal("link", (string?)incoming["kind"]);
        Assert.Equal("X", (string?)incoming["title"]);
        Assert.Equal("for you", (string?)incoming["comment"]);
        Assert.Equal("https://example.com/x", (string?)incoming["url"]);
        var key = $"{incoming["author"]}/{incoming["id"]}";

        // Announced already: skipped, so the long poll waits instead of answering at once.
        var skipped = await Ask(bob, $$$$"""{"op":"inbox","after":0,"skip":["{{{{key}}}}"],"wait":100}""");
        Assert.Empty(skipped["items"]!.AsArray());

        var open = await Ask(bob, $$$$"""{"op":"open","team":"{{{{Team}}}}","author":"{{{{incoming["author"]}}}}","id":"{{{{incoming["id"]}}}}"}""");
        Assert.True((bool)open["ok"]!, open.ToJsonString());
        Assert.Equal("https://example.com/x", (string?)open["url"]);
        var item = hub.Items.Single(i => i.RecipientMemberId == B);
        Assert.Equal(TeamItemStatuses.Accepted, TeamShare.MyStatus(item, B));
        Assert.Empty((await Ask(bob, """{"op":"inbox","after":0,"wait":0}"""))["items"]!.AsArray());

        // Carol declines hers; Bob cannot open Carol's copy.
        var (carol, _, _) = Member(hub, SharedMarksTests.CarolId);
        var carolsCopy = hub.Items.Single(i => i.RecipientMemberId == C);
        var notBobs = await Ask(bob, $$$$"""{"op":"open","team":"{{{{Team}}}}","author":"{{{{A}}}}","id":"{{{{carolsCopy.ItemId}}}}"}""");
        Assert.Equal("bad-request", (string?)notBobs["error"]);
        Assert.True((bool)(await Ask(carol, $$$$"""{"op":"decline","team":"{{{{Team}}}}","author":"{{{{A}}}}","id":"{{{{carolsCopy.ItemId}}}}"}"""))["ok"]!);
        Assert.Equal(TeamItemStatuses.Declined, TeamShare.MyStatus(hub.Items.Single(i => i.RecipientMemberId == C), C));
        Assert.Equal("declined", (string?)(await Ask(carol, $$$$"""{"op":"open","team":"{{{{Team}}}}","author":"{{{{A}}}}","id":"{{{{carolsCopy.ItemId}}}}"}"""))["error"]);
    }

    [Fact]
    public async Task Opening_a_shared_group_or_screenshot_returns_its_tabs_or_image()
    {
        var hub = new FakeHub();
        var (alice, _, _) = Member(hub, SharedMarksTests.AliceId);
        var (bob, _, _) = Member(hub, SharedMarksTests.BobId);
        await Ask(alice, $$$$"""{"op":"share","team":"{{{{Team}}}}","to":["{{{{B}}}}"],"item":{"kind":"collection","title":"Refs","links":[{"url":"https://a.example/","title":"A"},{"url":"https://b.example/","title":"B"}]}}""");
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
        await Ask(alice, $$$$"""{"op":"share","team":"{{{{Team}}}}","to":["{{{{B}}}}"],"item":{"kind":"screenshot","title":"Shot","jpeg":"{{{{Convert.ToBase64String(jpeg)}}}}"}}""");

        var inbox = (await Ask(bob, """{"op":"inbox","after":0,"wait":0}"""))["items"]!.AsArray();
        Assert.Equal(["collection", "screenshot"], inbox.Select(i => (string)i!["kind"]!).Order());
        Assert.All(inbox, i => Assert.Null(i!["data"])); // the image only comes with open

        var group = inbox.Single(i => (string?)i!["kind"] == "collection")!;
        var opened = await Ask(bob, $$$$"""{"op":"open","team":"{{{{Team}}}}","author":"{{{{A}}}}","id":"{{{{group["id"]}}}}"}""");
        Assert.Equal(["https://a.example/", "https://b.example/"], opened["links"]!.AsArray().Select(l => (string)l!["url"]!));

        var shot = inbox.Single(i => (string?)i!["kind"] == "screenshot")!;
        opened = await Ask(bob, $$$$"""{"op":"open","team":"{{{{Team}}}}","author":"{{{{A}}}}","id":"{{{{shot["id"]}}}}"}""");
        Assert.Equal("image/jpeg", (string?)opened["mime"]);
        Assert.Equal(jpeg, Convert.FromBase64String((string)opened["data"]!));
    }

    [SupportedOSPlatform("macos")]
    [Fact]
    public void Messages_round_trip_and_bad_ones_are_rejected()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var m = NightlyTeamLink.Message(NightlyTeamLink.RequestId, 0, """{"op":"state","t":"ü"}""");
        Assert.Equal(0, m.Length % 4);
        Assert.Equal("ü", (string?)NightlyTeamLink.Parse(m)!["t"]);
        Assert.Null(NightlyTeamLink.Parse(NightlyTeamLink.Message(NightlyTeamLink.ReplyId, 0, "{}"))); // not a request
        var bad = NightlyTeamLink.Message(NightlyTeamLink.RequestId, 0, "not json");
        Assert.Null(NightlyTeamLink.Parse(bad));
        var lying = NightlyTeamLink.Message(NightlyTeamLink.RequestId, 0, "{}");
        BitConverter.GetBytes(10_000).CopyTo(lying, 28); // length past the message
        Assert.Null(NightlyTeamLink.Parse(lying));
    }

    [SupportedOSPlatform("macos")]
    [Fact]
    public async Task The_mach_channel_answers_a_trusted_sender_and_drops_others()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var hub = new FakeHub();
        var items = new FakeTeamItems(hub, A);
        var source = new Source(items, SharedMarksTests.AliceId);
        var trust = true;
        var service = $"com.ombra.nightly-teams.test.{Environment.ProcessId}.{Guid.NewGuid():N}";
        using var link = new NightlyTeamLink(new NightlyTeamShare(source), service, (pid, token) =>
            trust && pid == Environment.ProcessId && token.Length == NightlyMach.AuditTokenSize);

        var (id, json) = await Task.Run(() => Request(service, """{"op":"state"}"""));
        Assert.Equal(NightlyTeamLink.ReplyId, id);
        Assert.Equal(["Bob", "Carol"], JsonNode.Parse(json!)!["teams"]![0]!["members"]!.AsArray().Select(m => (string)m!["name"]!));

        var big = new string('x', 200_000); // larger than one page: still inline
        (id, json) = await Task.Run(() => Request(service, $$$$"""{"op":"share","team":"{{{{Team}}}}","to":["{{{{B}}}}"],"item":{"kind":"link","url":"https://example.com/","title":"t","comment":"{{{{big}}}}"}}"""));
        Assert.True((bool)JsonNode.Parse(json!)!["ok"]!, json);
        Assert.Equal(280, TeamShare.Comment(Assert.Single(hub.Items))!.Length);

        trust = false;
        (id, json) = await Task.Run(() => Request(service, """{"op":"state"}"""));
        Assert.NotEqual(NightlyTeamLink.ReplyId, id); // the send-once notification: refused, no answer
        Assert.Null(json);
    }

    /// <summary>
    /// Release trust without a Developer ID: the sender is accepted only when its running code has the pinned cdhash.
    /// The sender here is this test host (a signed binary); its cdhash comes from codesign, independent of Ombra's code.
    /// </summary>
    [SupportedOSPlatform("macos")]
    [Fact]
    public async Task The_mach_channel_answers_only_the_pinned_cdhash()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var codesign = Process.Start(new ProcessStartInfo("/usr/bin/codesign", ["-dvvv", Environment.ProcessPath!]) { RedirectStandardError = true })!;
        var info = await codesign.StandardError.ReadToEndAsync();
        await codesign.WaitForExitAsync();
        var own = Regex.Match(info, "^CDHash=([0-9a-f]{40})$", RegexOptions.Multiline).Groups[1].Value;
        Assert.True(NightlyRuntimePackage.IsCdhash(own), info);
        var other = own[..39] + (own[39] == '0' ? '1' : '0');

        var hub = new FakeHub();
        var source = new Source(new FakeTeamItems(hub, A), SharedMarksTests.AliceId);
        var pinned = own;
        byte[]? seen = null;
        var service = $"com.ombra.nightly-teams.test.{Environment.ProcessId}.{Guid.NewGuid():N}";
        using var link = new NightlyTeamLink(new NightlyTeamShare(source), service, (_, token) =>
        {
            seen = token;
            return NightlyMach.HasCdhash(token, pinned);
        });

        var (id, json) = await Task.Run(() => Request(service, """{"op":"state"}"""));
        Assert.Equal(NightlyTeamLink.ReplyId, id);
        Assert.NotNull(json);
        Assert.False(NightlyMach.HasCdhash(seen!, other));
        Assert.False(NightlyMach.HasCdhash(seen!, "not a cdhash"));

        pinned = other; // a different binary than the pin: refused, no answer
        (id, json) = await Task.Run(() => Request(service, """{"op":"state"}"""));
        Assert.NotEqual(NightlyTeamLink.ReplyId, id);
        Assert.Null(json);
    }

    /// <summary>What Nightly's ZenTeamLinkWorker.js does: look the service up, send with a send-once reply right, wait.</summary>
    [SupportedOSPlatform("macos")]
    private static (int Id, string? Json) Request(string service, string json)
    {
        var port = NightlyMach.LookUp(service);
        Assert.NotEqual(0u, port);
        var reply = NightlyMach.NewReceivePort();
        Assert.NotEqual(0u, reply);
        try
        {
            var m = NightlyTeamLink.Message(NightlyTeamLink.RequestId, 19 | (21 << 8), json); // copy-send | make-send-once
            Assert.Equal(0, NightlyMach.SendRequest(port, reply, m, 2000));
            var buffer = new byte[NightlyTeamLink.MaxJson + 1024];
            Assert.Equal(0, NightlyMach.Receive(reply, buffer));
            var id = BitConverter.ToInt32(buffer, 20);
            if (id != NightlyTeamLink.ReplyId) return (id, null);
            return (id, Encoding.UTF8.GetString(buffer, 32, BitConverter.ToInt32(buffer, 28)));
        }
        finally
        {
            NightlyMach.DestroyReceiveRight(reply);
            NightlyMach.Deallocate(port);
        }
    }
}
