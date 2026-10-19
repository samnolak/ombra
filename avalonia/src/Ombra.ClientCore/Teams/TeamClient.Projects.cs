using System.Text.Json.Nodes;
using Ombra.Server.Contracts;

namespace Ombra.ClientCore.Teams;

/// <summary>A team project this device holds: its MLS project group (§3.1, descriptor type <c>project</c>).</summary>
public sealed record TeamProject(string TeamId, string ProjectId, string GroupId);

public sealed partial class TeamClient
{
    /// <summary>The open project groups this device is a leaf of, optionally of one team.</summary>
    public IReadOnlyList<TeamProject> ProjectGroups(string? teamId = null) =>
        [.. Device.Call("group.list")["groups"]!.AsArray()
            .Where(g => S(g!["type"]!) == "project" && !g["closed"]!.GetValue<bool>() && !g["evicted"]!.GetValue<bool>()
                && (teamId is null || S(g["teamId"]!) == teamId))
            .Select(g => new TeamProject(S(g!["teamId"]!), S(g["descriptor"]!["projectId"]!), S(g["groupId"]!)))];

    /// <summary>Admin: a new team project with access for every current member and its project group.</summary>
    public async Task<TeamProject> CreateProjectAsync(string teamId, CancellationToken ct = default)
    {
        if (!IsAdmin(teamId)) throw new InvalidOperationException("only owner/admin devices create team projects");
        var projectId = Hex(Guid.CreateVersion7().ToByteArray(bigEndian: true));
        await GrantProjectAccessAsync(teamId, projectId, ct);
        var gid = S(Device.Call("group.create", new()
        {
            ["descriptor"] = new JsonObject { ["type"] = "project", ["teamId"] = teamId, ["projectId"] = projectId, ["generation"] = 0 },
        })["groupId"]!);
        await Server.PostBytesAsync<GroupResponse>($"v1/teams/{teamId}/groups", B(Device.Call("group.public_info", new() { ["groupId"] = gid })["groupInfo"]!),
            Headers.MlsContentType, ct);
        var project = new TeamProject(teamId, projectId, gid);
        await AddProjectMembersAsync(project, ct);
        return project;
    }

    /// <summary>
    /// Admin: members who joined since the project was made get access, and every roster device missing from the
    /// project group (with a published key package) is added. Returns how many devices were added.
    /// </summary>
    public async Task<int> AddProjectMembersAsync(TeamProject project, CancellationToken ct = default)
    {
        if (!IsAdmin(project.TeamId)) return 0;
        await GrantProjectAccessAsync(project.TeamId, project.ProjectId, ct);
        var inTree = Device.Call("group.info", new() { ["groupId"] = project.GroupId })["members"]!.AsArray().Select(x => S(x!)).ToHashSet();
        var missing = Head(project.TeamId)["projection"]!["members"]!.AsArray().SelectMany(m => m!["devices"]!.AsArray())
            .Where(d => d!["revoked"] is null && !inTree.Contains(S(d["deviceId"]!))).Select(d => S(d!["deviceId"]!)).ToList();
        var kps = new List<JsonNode>();
        foreach (var d in missing)
        {
            try { kps.Add(Hex((await Server.GetAsync<KeyPackageResponse>($"v1/keypackages/{TeamsHex.ToGuid(d)}?team={project.TeamId}&group={project.GroupId}", ct)).KeyPackage)); }
            catch (TeamsServerException e) when (e.Status == 404) { } // that device has not published one yet: next round
        }
        if (kps.Count == 0) return 0;
        var add = Device.Call("group.add", new() { ["groupId"] = project.GroupId, ["keyPackages"] = new JsonArray(kps.ToArray()), ["now"] = Now });
        await CommitAsync(project.GroupId, add, ct);
        return kps.Count;
    }

    private async Task GrantProjectAccessAsync(string teamId, string projectId, CancellationToken ct)
    {
        var projection = Head(teamId)["projection"]!;
        var has = projection["access"]!.AsArray().Where(a => S(a!["projectId"]!) == projectId).Select(a => S(a!["memberId"]!)).ToHashSet();
        var grants = projection["members"]!.AsArray().Select(m => S(m!["memberId"]!)).Where(m => !has.Contains(m))
            .Select(m => (JsonNode)new JsonObject { ["projectId"] = projectId, ["memberId"] = m }).ToArray();
        if (grants.Length == 0) return;
        var u = Device.Call("roster.update", new() { ["teamId"] = teamId, ["now"] = Now, ["apply"] = true, ["grantAccess"] = new JsonArray(grants) });
        await PostRosterAsync(teamId, B(u["update"]!), ct);
    }
}
