using System.Text.Json.Nodes;
using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>
/// The ask form (<c>set_ask_dialog</c>: every question of one ask tool call comes as one <c>ask</c> request) and edits
/// to the plan (<c>set_todos</c>: the user's changes to omp's todo list), rpc-mode.ts.
/// </summary>
public sealed partial class SessionController
{
    /// <summary>
    /// Per connection, before anything can ask: omp then sends the ask tool's questions as one form. An omp without
    /// the command keeps asking one select (and one free-text input) per question, which the cards answer as before.
    /// </summary>
    private async Task EnableAskFormAsync(RpcConnection conn, CancellationToken ct)
    {
        try { await conn.SetAskDialogAsync(true, ct).ConfigureAwait(false); }
        catch (RpcCommandException e)
        {
            Mutate(s => s.Log(Now, "ask_form_unavailable", e.Message));
        }
    }

    /// <summary>
    /// Replaces omp's todo list with <paramref name="phases"/> (the Plan pane's edits) and shows omp's list after it.
    /// False when omp did not take it (a notice says why); the list shown stays omp's.
    /// </summary>
    public async Task<bool> SetTodosAsync(IReadOnlyList<TodoPhase> phases, CancellationToken ct = default)
    {
        var conn = RequireConnection();
        System.Text.Json.JsonElement applied;
        try { applied = await conn.SetTodosAsync(TodosJson(phases), ct).ConfigureAwait(false); }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException)
        {
            Mutate(s => s.AddNotice(NoticeLevel.Error, "The plan was not changed: " + e.Message));
            return false;
        }
        // A get_state answer already on its way may predate the change: the refresh requested first makes it obsolete
        // (it is dropped), and its own answer confirms the list from omp.
        if (ReferenceEquals(conn, Connection)) RequestStateRefresh(conn);
        Mutate(s => s.Todos = ConversationState.ParseTodos(applied));
        return true;
    }

    /// <summary>The wire form of todo phases (rpc-types.ts <c>TodoPhase</c>), each task's details and notes kept.</summary>
    internal static JsonArray TodosJson(IReadOnlyList<TodoPhase> phases)
    {
        var list = new JsonArray();
        foreach (var phase in phases)
        {
            var tasks = new JsonArray();
            foreach (var t in phase.Tasks)
            {
                var task = new JsonObject { ["content"] = t.Content, ["status"] = t.Status };
                if (t.Blocker is not null) task["blocker"] = t.Blocker;
                if (t.Details is not null) task["details"] = t.Details;
                if (t.Notes is { } notes) task["notes"] = new JsonArray([.. notes.Select(n => (JsonNode?)JsonValue.Create(n))]);
                tasks.Add(task);
            }
            list.Add(new JsonObject { ["name"] = phase.Name, ["tasks"] = tasks });
        }
        return list;
    }
}
