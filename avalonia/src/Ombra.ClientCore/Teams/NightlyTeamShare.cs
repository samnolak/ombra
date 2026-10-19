using System.Text.Json.Nodes;

namespace Ombra.ClientCore.Teams;

/// <summary>What the team link can see of the signed-in team account: the items API, who I am, the teams and their members.</summary>
/// <param name="Problem">Why sharing is not possible now (<see cref="NightlyTeamShare.SignedOut"/>, <see cref="NightlyTeamShare.NoTeam"/>,
/// <see cref="NightlyTeamShare.NotReady"/>); null when it is.</param>
public sealed record NightlyTeamState(ITeamItems? Items, Guid MemberId, IReadOnlyList<TeamView> Teams, string? Problem)
{
    public static NightlyTeamState Unavailable(string problem) => new(null, Guid.Empty, [], problem);
}

/// <summary>The signed-in team account as the Nightly link sees it; <see cref="Changed"/> on sign-in/out, refreshes and new items.</summary>
public interface INightlyTeamSource
{
    NightlyTeamState Current { get; }
    event Action? Changed;
}

/// <summary>
/// B1/B2 on the Nightly side (SERVER_DESIGN §7, decision 2026-10-18): Nightly has no team device of its own; it asks the
/// Ombra on the same Mac, over Ombra.App's NightlyTeamLink (Mach), to share a page, a tab group or a gallery screenshot through
/// <see cref="ITeamItems"/>, and to tell it what was shared with this member (toasts, open = accept). This class is the
/// request handler, engine- and transport-neutral (JSON in, JSON out), so it is unit tested with a fake source.
/// <para>Requests: <c>state</c>; <c>share {team, to: "all"|[memberHex], item: {kind: link|collection|screenshot, …}}</c>;
/// <c>inbox {after, skip, wait}</c> (long poll: answers at once when something not in <c>skip</c> (<c>author/id</c>) was
/// created at or after <c>after</c>, else on the next change or after <c>wait</c> ms); <c>open {team, author, id}</c> (the item, then <c>accepted</c>);
/// <c>decline {team, author, id}</c>. Every answer has <c>ok</c>; failures carry <c>error</c> (a code Nightly localizes).</para>
/// </summary>
public sealed class NightlyTeamShare
{
    public const string SignedOut = "signed-out", NoTeam = "no-team", NotReady = "not-ready";
    public const int MaxComment = SharedMarks.MaxStatusNote, MaxLinks = 100, MaxTitle = 300, MaxUrl = 8192;
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    private readonly INightlyTeamSource _source;
    private readonly Action<string, Exception>? _log;

    public NightlyTeamShare(INightlyTeamSource source, Action<string, Exception>? log = null)
    {
        _source = source;
        _log = log;
    }

    public async Task<JsonObject> HandleAsync(JsonObject request, CancellationToken ct)
    {
        try
        {
            return (string?)request["op"] switch
            {
                "state" => State(),
                "share" => await ShareAsync(request, ct),
                "inbox" => await InboxAsync(request, ct),
                "open" => await OpenAsync(request, ct),
                "decline" => await DeclineAsync(request, ct),
                _ => Fail("bad-request"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (UnavailableException e) { return Fail(e.Message); }
        catch (TeamShareTooLargeException e) { return Fail("too-large", e.Message); }
        catch (Exception e) when (e is ArgumentException or FormatException or KeyNotFoundException)
        {
            return Fail("bad-request", e.Message);
        }
        catch (Exception e)
        {
            _log?.Invoke((string?)request["op"] ?? "?", e);
            return Fail("failed", e.Message);
        }
    }

    /// <summary>Sharing is not possible now: the message is the reason code (<see cref="SignedOut"/>, …).</summary>
    private sealed class UnavailableException(string reason) : Exception(reason);

    private static JsonObject Fail(string code, string? detail = null)
    {
        var o = new JsonObject { ["ok"] = false, ["error"] = code };
        if (detail is not null) o["detail"] = detail;
        return o;
    }

    private JsonObject State()
    {
        var s = _source.Current;
        if (s.Problem is { } problem) return new JsonObject { ["ok"] = true, ["ready"] = false, ["reason"] = problem };
        var teams = new JsonArray();
        foreach (var t in s.Teams)
        {
            var members = new JsonArray();
            foreach (var m in t.Roster.Members.Where(m => !m.IsMe))
                members.Add(new JsonObject { ["id"] = TeamsHex.Of(m.MemberId), ["name"] = m.Name ?? TeamsHex.Of(m.MemberId)[..8] });
            teams.Add(new JsonObject { ["id"] = t.TeamId, ["title"] = $"Team {t.TeamId[..Math.Min(8, t.TeamId.Length)]}", ["members"] = members });
        }
        return new JsonObject { ["ok"] = true, ["ready"] = true, ["me"] = TeamsHex.Of(s.MemberId), ["teams"] = teams };
    }

    private (ITeamItems Items, NightlyTeamState State, TeamView Team) Team(JsonObject request)
    {
        var s = _source.Current;
        if (s.Problem is not null || s.Items is null) throw new UnavailableException(s.Problem ?? NotReady);
        var id = (string?)request["team"] ?? throw new ArgumentException("team is required");
        var team = s.Teams.FirstOrDefault(t => t.TeamId == id) ?? throw new ArgumentException("unknown team");
        return (s.Items, s, team);
    }

    private async Task<JsonObject> ShareAsync(JsonObject request, CancellationToken ct)
    {
        var (items, _, team) = Team(request);
        var others = team.Roster.Members.Where(m => !m.IsMe).Select(m => TeamsHex.Of(m.MemberId)).ToList();
        IReadOnlyList<string> recipients = request["to"] switch
        {
            JsonValue v when (string?)v == "all" => others,
            JsonArray a => a.Select(n => (string?)n ?? "").Distinct().ToList(),
            _ => throw new ArgumentException("to must be \"all\" or a list of member ids"),
        };
        if (recipients.Count == 0) throw new ArgumentException("no recipients");
        if (recipients.FirstOrDefault(r => !others.Contains(r)) is { } stranger) throw new ArgumentException($"{stranger} is not another member of the team");
        var content = Draft(request["item"] as JsonObject ?? throw new ArgumentException("item is required"), out var comment);
        var sent = await content.SendAsync(items, team.TeamId, recipients, comment, ct);
        _ = DeliverAsync(items);
        return new JsonObject { ["ok"] = true, ["sent"] = sent };
    }

    private async Task DeliverAsync(ITeamItems items)
    {
        try { await items.DeliverAsync(); }
        catch (Exception e) { _log?.Invoke("deliver", e); } // the outbox keeps it; the session's loop retries
    }

    /// <summary>What Nightly shares, built only by <see cref="TeamShare"/> (the core's item schemas are closed, so Nightly
    /// adds no fields of its own): a page, a tab group, a screenshot (a blob, C3). The comment goes to the item's context.</summary>
    public static ShareContent Draft(JsonObject item, out string? comment)
    {
        comment = Text(item["comment"], MaxComment);
        return (string?)item["kind"] switch
        {
            "link" => TeamShare.Link(Url(item["url"]), Text(item["title"], MaxTitle)),
            "collection" => TeamShare.Collection(Text(item["title"], MaxTitle) ?? "",
                (item["links"] as JsonArray ?? throw new ArgumentException("links are required"))
                    .Select(l => l as JsonObject ?? throw new ArgumentException("bad link"))
                    .Select(l => new SharedLink(Url(l["url"]), Text(l["title"], MaxTitle)))
                    .ToList() is { Count: > 0 and <= MaxLinks } links ? links : throw new ArgumentException($"a tab group has 1–{MaxLinks} tabs")),
            "screenshot" => TeamShare.Snapshot(Text(item["title"], MaxTitle) is { Length: > 0 } t ? t : "Screenshot",
                Convert.FromBase64String((string?)item["jpeg"] ?? throw new ArgumentException("jpeg is required")), item["url"] is { } u ? Url(u) : null),
            _ => throw new ArgumentException("kind must be link, collection or screenshot"),
        };
    }

    private static string? Text(JsonNode? node, int max)
    {
        var s = ((string?)node)?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length <= max ? s : s[..max];
    }

    private static string Url(JsonNode? node)
    {
        var s = (string?)node ?? throw new ArgumentException("url is required");
        if (s.Length > MaxUrl || !Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.UserInfo.Length > 0)
            throw new ArgumentException("only http(s) addresses without credentials can be shared");
        return u.AbsoluteUri;
    }

    private async Task<JsonObject> InboxAsync(JsonObject request, CancellationToken ct)
    {
        var after = request["after"] is JsonValue a && a.TryGetValue<ulong>(out var v) ? v : 0UL;
        var skip = (request["skip"] as JsonArray)?.Select(n => (string?)n ?? "").ToHashSet(StringComparer.Ordinal) ?? [];
        var wait = TimeSpan.FromMilliseconds(Math.Clamp(request["wait"] is JsonValue w && w.TryGetValue<int>(out var ms) ? ms : 0, 0, (int)MaxWait.TotalMilliseconds));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged() => changed.TrySetResult();
        _source.Changed += OnChanged;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(wait);
            while (true)
            {
                var (state, found) = Incoming(after, skip);
                if (found.Count > 0 || timeout.IsCancellationRequested || state.Problem is not null)
                    return Inbox(state, found);
                try { await changed.Task.WaitAsync(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        finally
        {
            _source.Changed -= OnChanged;
        }
    }

    /// <summary>Resources shared with me by others (link, tab group, image) at or after <paramref name="after"/> and not in
    /// <paramref name="skip"/>, not accepted or declined yet.</summary>
    private (NightlyTeamState State, List<(TeamView Team, TeamItem Item, IReadOnlyList<TeamItem> All)> Found) Incoming(ulong after, HashSet<string> skip)
    {
        var s = _source.Current;
        var found = new List<(TeamView, TeamItem, IReadOnlyList<TeamItem>)>();
        if (s.Problem is not null || s.Items is null) return (s, found);
        var me = TeamsHex.Of(s.MemberId);
        foreach (var team in s.Teams)
        {
            var all = s.Items.List(team.TeamId);
            foreach (var item in TeamShare.Incoming(all, me))
            {
                if (item.CreatedAt < after || !Openable(item) || !TeamShare.IsPending(item, me)) continue;
                if (skip.Contains(item.AuthorMemberId + "/" + item.ItemId)) continue;
                found.Add((team, item, all));
            }
        }
        return (s, found);
    }

    private static bool Openable(TeamItem item) => item.Kind is TeamItemKinds.Link or TeamItemKinds.Collection or TeamItemKinds.Snapshot;

    private static JsonObject Inbox(NightlyTeamState state, List<(TeamView Team, TeamItem Item, IReadOnlyList<TeamItem> All)> found)
    {
        var list = new JsonArray();
        var now = 0UL;
        foreach (var (team, item, all) in found.OrderBy(f => f.Item.CreatedAt))
        {
            now = Math.Max(now, item.CreatedAt);
            list.Add(Describe(team, item, all, data: null));
        }
        var o = new JsonObject { ["ok"] = true, ["items"] = list, ["now"] = now };
        if (state.Problem is { } problem) o["reason"] = problem;
        return o;
    }

    private static JsonObject Describe(TeamView team, TeamItem item, IReadOnlyList<TeamItem> all, byte[]? data)
    {
        var author = team.Roster.Members.FirstOrDefault(m => TeamsHex.Of(m.MemberId) == item.AuthorMemberId);
        var o = new JsonObject
        {
            ["team"] = team.TeamId,
            ["author"] = item.AuthorMemberId,
            ["authorName"] = author?.Name ?? item.AuthorMemberId[..Math.Min(8, item.AuthorMemberId.Length)],
            ["id"] = item.ItemId,
            ["kind"] = item.Kind == TeamItemKinds.Snapshot ? "screenshot" : item.Kind,
            ["createdAt"] = item.CreatedAt,
            ["title"] = TeamShare.Title(item),
            ["comment"] = TeamShare.Comment(item),
        };
        if ((TeamShare.Url(item) ?? TeamShare.SourceUrl(item)) is { } url) o["url"] = url;
        if (item.Kind == TeamItemKinds.Collection)
            o["links"] = new JsonArray(TeamShare.Links(item, all).Select(l => (JsonNode)new JsonObject { ["url"] = l.Url, ["title"] = l.Title }).ToArray());
        if (data is not null)
        {
            o["mime"] = "image/jpeg";
            o["data"] = Convert.ToBase64String(data);
        }
        return o;
    }

    private (ITeamItems Items, TeamView Team, TeamItem Item) Find(JsonObject request)
    {
        var (items, state, team) = Team(request);
        var author = (string?)request["author"] ?? throw new ArgumentException("author is required");
        var id = (string?)request["id"] ?? throw new ArgumentException("id is required");
        var item = items.Get(team.TeamId, author, id);
        if (item is null || item.Deleted || TeamShare.Incoming([item], TeamsHex.Of(state.MemberId)).Count == 0 || !Openable(item)) throw new KeyNotFoundException("no such item");
        return (items, team, item);
    }

    private async Task<JsonObject> OpenAsync(JsonObject request, CancellationToken ct)
    {
        var (items, team, item) = Find(request);
        var mine = TeamShare.MyStatus(item, TeamsHex.Of(_source.Current.MemberId));
        if (mine == TeamItemStatuses.Declined) return Fail("declined");
        if (mine != TeamItemStatuses.Accepted)
        {
            await items.SetStatusAsync(team.TeamId, item.AuthorMemberId, item.ItemId, TeamItemStatuses.Accepted, ct: ct);
            _ = DeliverAsync(items);
            item = items.Get(team.TeamId, item.AuthorMemberId, item.ItemId) ?? item;
        }
        // C3: a screenshot's blob is fetched only now, after Accept
        var data = item.Kind == TeamItemKinds.Snapshot ? await items.DownloadAsync(item, ct) : null;
        var o = Describe(team, item, items.List(team.TeamId), data);
        o["ok"] = true;
        return o;
    }

    private async Task<JsonObject> DeclineAsync(JsonObject request, CancellationToken ct)
    {
        var (items, team, item) = Find(request);
        await items.SetStatusAsync(team.TeamId, item.AuthorMemberId, item.ItemId, TeamItemStatuses.Declined, ct: ct);
        _ = DeliverAsync(items);
        return new JsonObject { ["ok"] = true };
    }
}
