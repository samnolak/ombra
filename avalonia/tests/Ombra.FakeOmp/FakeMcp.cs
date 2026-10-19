using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ombra.FakeOmp;

/// <summary>
/// MCP servers over RPC like the owned engine (<c>get_mcp_servers</c>, <c>mcp_login</c>, <c>mcp_login_cancel</c>,
/// <c>mcp_reconnect</c>), in every scenario. FAKE_OMP_MCP names a JSON file <c>{"servers":[…]}</c>; a server's
/// <c>holdLogin</c>, <c>authUrl</c> and <c>loginTools</c> steer its sign-in and are never sent. Each command is
/// appended to FAKE_OMP_COMMAND_LOG; <c>test_mcp_control</c> replaces the servers or emits a frame.
/// </summary>
internal sealed class FakeMcp(Action<object> emit, Action<string?, string, object?, string?, string?> respond) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _logins = new(StringComparer.Ordinal);
    private JsonArray _servers = Load();

    private static JsonArray Load() =>
        Environment.GetEnvironmentVariable("FAKE_OMP_MCP") is { Length: > 0 } path && File.Exists(path)
        && JsonNode.Parse(File.ReadAllText(path))?["servers"] is JsonArray servers
            ? (JsonArray)servers.DeepClone() : [];

    private static void Log(string line)
    {
        if (Environment.GetEnvironmentVariable("FAKE_OMP_COMMAND_LOG") is { Length: > 0 } log) FakeEngineHost.AppendLog(log, line + "\n");
    }

    private static JsonObject Public(JsonObject server)
    {
        var copy = (JsonObject)server.DeepClone();
        copy.Remove("holdLogin");
        copy.Remove("authUrl");
        copy.Remove("loginTools");
        return copy;
    }

    private JsonArray PublicServers()
    {
        lock (_gate) return [.. _servers.OfType<JsonObject>().Select(s => (JsonNode)Public(s))];
    }

    private JsonObject? Find(string name)
    {
        lock (_gate) return _servers.OfType<JsonObject>().FirstOrDefault(s => s["name"]?.GetValue<string>() == name);
    }

    /// <summary>Answers an MCP command; false when <paramref name="type"/> is not one.</summary>
    public bool Handle(string? id, string type, JsonNode? cmd)
    {
        switch (type)
        {
            case "get_mcp_servers":
                Log("get_mcp_servers");
                respond(id, type, new { servers = PublicServers() }, null, null);
                return true;
            case "mcp_login":
            {
                var name = cmd?["serverName"]?.GetValue<string>() ?? "";
                Log("mcp_login " + name);
                if (Find(name) is not { } entry) { respond(id, type, null, $"MCP server \"{name}\" not found", null); return true; }
                var cts = new CancellationTokenSource();
                bool hold;
                lock (_gate)
                {
                    if (_logins.Remove(name, out var previous)) previous.Cancel();
                    _logins[name] = cts;
                    hold = entry["holdLogin"]?.GetValue<bool>() == true;
                }
                emit(new
                {
                    type = "extension_ui_request", id = "mcp-open-" + name, method = "open_url",
                    url = entry["authUrl"]?.GetValue<string>() ?? "https://auth.example.invalid/authorize?client_id=fake&state=s1",
                    mcpServer = name,
                });
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(hold ? Timeout.Infinite : 300, cts.Token); }
                    catch (OperationCanceledException)
                    {
                        respond(id, type, null, "MCP sign-in cancelled", "mcp_login_cancelled");
                        return;
                    }
                    JsonObject done;
                    lock (_gate)
                    {
                        if (_logins.TryGetValue(name, out var current) && current == cts) _logins.Remove(name);
                        entry["auth"] = "signed_in";
                        entry["status"] = "connected";
                        entry["toolCount"] = entry["loginTools"]?.GetValue<int>() ?? 2;
                        entry.Remove("error");
                        done = Public(entry);
                    }
                    respond(id, type, done, null, null);
                });
                return true;
            }
            case "mcp_login_cancel":
            {
                var name = cmd?["serverName"]?.GetValue<string>() ?? "";
                Log("mcp_login_cancel " + name);
                CancellationTokenSource? pending;
                lock (_gate) _logins.Remove(name, out pending);
                pending?.Cancel();
                respond(id, type, new { cancelled = pending is not null }, null, null);
                return true;
            }
            case "mcp_reconnect":
            {
                var name = cmd?["serverName"]?.GetValue<string>();
                Log("mcp_reconnect " + (name ?? "*"));
                lock (_gate)
                    foreach (var server in _servers.OfType<JsonObject>())
                        if ((name is null || server["name"]?.GetValue<string>() == name)
                            && server["auth"]?.GetValue<string>() is "none" or "signed_in")
                            server["status"] = "connected";
                respond(id, type, new { servers = PublicServers() }, null, null);
                return true;
            }
            case "test_mcp_control":
                if (cmd?["servers"] is JsonArray servers) lock (_gate) _servers = (JsonArray)servers.DeepClone();
                if (cmd?["emit"] is JsonObject frame) emit(frame.DeepClone());
                respond(id, type, new { }, null, null);
                return true;
            default:
                return false;
        }
    }

    /// <summary>An <c>extension_ui_response</c> to an MCP elicitation is logged, without its type, on one line.</summary>
    public static void OnUiResponse(JsonNode? cmd)
    {
        if (cmd is not JsonObject response || response["id"]?.GetValue<string>() is not { } id || !id.StartsWith("elicit-", StringComparison.Ordinal)) return;
        var copy = (JsonObject)response.DeepClone();
        copy.Remove("type");
        Log("elicit " + copy.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var login in _logins.Values) login.Cancel();
            _logins.Clear();
        }
    }
}
