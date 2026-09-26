using System.Text.Json.Nodes;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.ClientCore;
using OmpGui.Rpc;

namespace OmpGui.Tests;

/// <summary>
/// What omp 18.2.0 prints for /mcp and its plugin CLI, as recorded from real runs (a stdio MCP server of our own, a
/// broken command, an SSE address; a linked npm plugin and a local marketplace; paths shortened), and how the
/// Connectors and Plugins and skills pages read it.
/// </summary>
public class ConnectorsParsingTests
{
    // Real `/mcp list` (omp 18.2.0 over RPC, slash-commands/helpers/mcp.ts handleListCommand).
    internal const string RealList =
        "echo | stdio | enabled | /opt/bun/bin/bun [user]\n" +
        "broken | stdio | enabled | /nonexistent/cmd [project]\n" +
        "web | sse | enabled | https://example.invalid/mcp [project]\n" +
        "web2 | http | enabled | https://example.com/mcp [project]";

    // Real `/tools` with the echo server connected: its tools are listed and mounted under xd://.
    internal const string RealTools = "* read\n* bash\n- ast_edit\n* eval\n- mcp__echo_add\n- mcp__echo_echo\n~ xd://ast_edit\n~ xd://mcp__echo_add\n~ xd://mcp__echo_echo";

    [Fact]
    public void Mcp_list_reads_name_transport_state_location_and_scope()
    {
        var list = OmpMcp.ParseList(RealList)!;
        Assert.Equal(4, list.Count);
        Assert.Equal(new McpServerEntry("echo", "stdio", true, "/opt/bun/bin/bun", "user"), list[0]);
        Assert.Equal(new McpServerEntry("web", "sse", true, "https://example.invalid/mcp", "project"), list[2]);
        Assert.Equal("http", list[3].Transport);
        Assert.False(OmpMcp.ParseList("echo | stdio | disabled | /usr/bin/node [user]")![0].Enabled);
        Assert.Empty(OmpMcp.ParseList("No MCP servers configured.")!);
        // Anything else is omp's message (an error): the page shows it as it is.
        Assert.Null(OmpMcp.ParseList("Failed to list MCP servers: EACCES: permission denied"));
        Assert.Null(OmpMcp.ParseList(""));
    }

    [Fact]
    public void Mcp_test_reads_the_tools_or_the_reason()
    {
        var ok = OmpMcp.ParseTest("Server \"echo\" connected (2 tools).\n  - echo\n  - add");
        Assert.True(ok.Connected);
        Assert.Equal(2, ok.ToolCount);
        Assert.Equal(["echo", "add"], ok.Tools);

        var failed = OmpMcp.ParseTest("Connection to \"broken\" failed: ENOENT: no such file or directory, posix_spawn '/nonexistent/cmd'");
        Assert.False(failed.Connected);
        Assert.Equal("ENOENT: no such file or directory, posix_spawn '/nonexistent/cmd'", failed.Message);

        var refused = OmpMcp.ParseTest("Connection to \"web\" failed: HTTP 403: request blocked: no rule or allowlist entry allows host \"example.invalid\"");
        Assert.StartsWith("HTTP 403", refused.Message);

        // omp's /mcp test skips disabled servers: it says "not found"
        var disabled = OmpMcp.ParseTest("Server \"echo\" not found. Run /mcp list to see configured servers.");
        Assert.False(disabled.Connected);
        Assert.Contains("not found", disabled.Message);
        Assert.Equal(1, OmpMcp.ParseTest("Server \"one\" connected (1 tool).\n  - t").ToolCount);
    }

    [Fact]
    public void Resources_and_prompts_are_split_by_server()
    {
        var resources = OmpMcp.ParseItems("echo/file:///readme.md\necho/memo://today", out var none);
        Assert.Null(none);
        Assert.Equal([new McpItem("echo", "file:///readme.md", null), new McpItem("echo", "memo://today", null)], resources);
        var prompts = OmpMcp.ParseItems("echo/summarize — Summarize a text\necho/greet", out _);
        Assert.Equal(new McpItem("echo", "summarize", "Summarize a text"), prompts[0]);
        Assert.Equal(new McpItem("echo", "greet", null), prompts[1]);
        Assert.Empty(OmpMcp.ParseItems("No resources available on connected servers.", out none));
        Assert.Equal("No resources available on connected servers.", none);
        Assert.Empty(OmpMcp.ParseItems("No MCP servers configured.", out none));
        Assert.Equal(OmpMcp.NoServers, none);
    }

    [Fact]
    public void Smithery_results_and_its_sign_in_message()
    {
        // Built from omp's formatting (helpers/mcp.ts handleSmitherySearchCommand): "Display (qualified) — description".
        var r = OmpMcp.ParseSmithery("GitHub (@smithery-ai/github) — Access the GitHub API (repos, issues)\nMemory (@modelcontextprotocol/memory)")!;
        Assert.Equal(new SmitheryResult("GitHub", "@smithery-ai/github", "Access the GitHub API (repos, issues)"), r[0]);
        Assert.Equal(new SmitheryResult("Memory", "@modelcontextprotocol/memory", null), r[1]);
        // Real, without a Smithery API key:
        const string auth = "Smithery authentication required. Run /mcp smithery-login in the TUI client or add an API key to ~/.omp/agent/smithery.json.";
        Assert.Null(OmpMcp.ParseSmithery(auth));
        Assert.True(OmpMcp.IsSmitherySignInNeeded(auth));
        Assert.Null(OmpMcp.ParseSmithery("No Smithery results found for \"zzz\"."));
    }

    [Fact]
    public void Session_tools_are_attributed_to_their_server()
    {
        var tools = OmpMcp.McpToolsIn(RealTools);
        Assert.Equal(["mcp__echo_add", "mcp__echo_echo"], tools);
        // createMCPToolName: lowercase, anything but a-z and _ becomes _, the longest server prefix wins
        Assert.Equal("mcp__my_server_", OmpMcp.ToolPrefix("My-Server2"));
        var (by, other) = OmpMcp.AttributeTools(["echo", "echo-two", "web"], [.. tools, "mcp__echo_two_ping", "mcp__claude_thing_run"]);
        Assert.Equal(["mcp__echo_add", "mcp__echo_echo"], by["echo"]);
        Assert.Equal(["mcp__echo_two_ping"], by["echo-two"]);
        Assert.Empty(by["web"]);
        Assert.Equal(["mcp__claude_thing_run"], other);
    }

    /// <summary>omp's tokenizer for slash-command arguments (utils/command-args.ts parseCommandArgs), ported to check our quoting.</summary>
    private static List<string> OmpTokens(string s)
    {
        var args = new List<string>();
        var current = "";
        char? quote = null;
        foreach (var c in s)
        {
            if (quote is { } q)
            {
                if (c == q) quote = null;
                else current += c;
            }
            else if (c is '"' or '\'') quote = c;
            else if (c is ' ' or '\t')
            {
                if (current.Length > 0) args.Add(current);
                current = "";
            }
            else current += c;
        }
        if (current.Length > 0) args.Add(current);
        return args;
    }

    [Fact]
    public void Add_command_follows_omps_grammar_and_survives_its_tokenizer()
    {
        var stdio = OmpMcp.AddCommand("github", "user", "stdio", "npx", ["-y", "@modelcontextprotocol/server-github", "a b", "say \"hi\"", "it's"], null, null);
        Assert.StartsWith("/mcp add github --scope user -- npx -y @modelcontextprotocol/server-github ", stdio);
        Assert.Equal(["github", "--scope", "user", "--", "npx", "-y", "@modelcontextprotocol/server-github", "a b", "say \"hi\"", "it's"],
            OmpTokens(stdio["/mcp add ".Length..]));

        var http = OmpMcp.AddCommand("linear", "project", "sse", null, [], "https://mcp.linear.app/sse", "tok en");
        Assert.Equal(["linear", "--scope", "project", "--url", "https://mcp.linear.app/sse", "--transport", "sse", "--token", "tok en"],
            OmpTokens(http["/mcp add ".Length..]));
        Assert.Equal("/mcp add x --scope user --url https://a --transport http", OmpMcp.AddCommand("x", "user", "http", null, [], "https://a", ""));
        Assert.Equal("/mcp remove github --scope project", OmpMcp.RemoveCommand("github", "project"));
    }

    [Theory]
    [InlineData("github", null)]
    [InlineData("cloudflare:api_v2.1", null)]
    [InlineData("", "Give the connector a name.")]
    [InlineData("my server", "Use letters, numbers, dash, underscore, dot or colon (no spaces).")]
    [InlineData("a/b", "Use letters, numbers, dash, underscore, dot or colon (no spaces).")]
    public void Names_are_checked_as_omp_checks_them(string name, string? error) => Assert.Equal(error, OmpMcp.ValidateName(name));

    [Fact]
    public void Editing_a_server_keeps_every_other_field_and_writes_as_omp_does()
    {
        var dir = TestProcesses.TempDir("mcpfile");
        var path = Path.Combine(dir, "mcp.json");
        // A file as omp writes it, plus fields this app never touches.
        File.WriteAllText(path, """
            {
              "mcpServers": {
                "echo": { "type": "stdio", "command": "bun", "args": ["echo.js"], "enabled": true, "timeout": 30000, "futureField": { "x": 1 } },
                "web": { "type": "sse", "url": "https://example.invalid/mcp", "auth": { "type": "oauth", "credentialId": "c1" } }
              },
              "disabledServers": ["other"]
            }
            """);
        McpConfigFile.UpdateServer(path, "echo", e => McpConfigFile.SetMap(e, "env", [new("API_KEY", "s3cret"), new("REGION", "eu ü")]));
        var text = File.ReadAllText(path);
        Assert.StartsWith("{\n  \"$schema\": \"" + McpConfigFile.SchemaUrl + "\",\n  \"mcpServers\"", text.Replace("\r", ""));
        Assert.Contains("eu ü", text); // not escaped, as JSON.stringify writes it
        var root = JsonNode.Parse(text)!.AsObject();
        var echo = root["mcpServers"]!["echo"]!.AsObject();
        Assert.Equal(30000, echo["timeout"]!.GetValue<int>());
        Assert.Equal(1, echo["futureField"]!["x"]!.GetValue<int>());
        Assert.Equal("s3cret", echo["env"]!["API_KEY"]!.GetValue<string>());
        Assert.Equal("c1", root["mcpServers"]!["web"]!["auth"]!["credentialId"]!.GetValue<string>());
        Assert.Equal("other", root["disabledServers"]![0]!.GetValue<string>());
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

        Assert.True(McpConfigFile.UsesOAuth(McpConfigFile.ReadServer(path, "web")));
        Assert.Equal(["echo.js"], McpConfigFile.GetArgs(McpConfigFile.ReadServer(path, "echo")));
        Assert.Null(McpConfigFile.ReadServer(path, "nope"));
        Assert.Throws<InvalidDataException>(() => McpConfigFile.UpdateServer(path, "nope", _ => { }));
    }

    // Real `omp plugin list --json` with a linked npm plugin and a marketplace plugin (disabled).
    internal const string RealPluginList = """
        {
          "npm": [
            {
              "name": "omp-hello",
              "version": "1.2.0",
              "path": "/home/u/.omp/profiles/harness/plugins/node_modules/omp-hello",
              "manifest": { "version": "1.2.0", "description": "Greets you from a plugin", "commands": ["commands/hello.md"] },
              "enabledFeatures": null,
              "enabled": true
            }
          ],
          "marketplace": [
            {
              "id": "hello-skill@local-mkt",
              "scope": "user",
              "entries": [
                {
                  "scope": "user",
                  "installPath": "/home/u/.omp/profiles/harness/plugins/cache/plugins/local-mkt___hello-skill___0.1.0",
                  "version": "0.1.0",
                  "installedAt": "2026-09-25T20:18:01.489Z",
                  "lastUpdated": "2026-09-25T20:18:01.489Z",
                  "enabled": false
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Plugin_list_json_reads_npm_and_marketplace_plugins()
    {
        var list = OmpPlugins.ParseList(RealPluginList)!;
        Assert.Equal(new InstalledPluginInfo("omp-hello", "omp-hello", null, "1.2.0", "Greets you from a plugin", true, null, false), list[0]);
        Assert.Equal(new InstalledPluginInfo("hello-skill@local-mkt", "hello-skill", "local-mkt", "0.1.0", null, false, "user", false), list[1]);
        Assert.Empty(OmpPlugins.ParseList("{\n  \"npm\": [],\n  \"marketplace\": []\n}")!);
        Assert.Null(OmpPlugins.ParseList("error: something"));
        var shadowed = OmpPlugins.ParseList("""{"npm":[],"marketplace":[{"id":"a@m","scope":"user","entries":[{"version":"1"}],"shadowedBy":"project"}]}""")!;
        Assert.True(shadowed[0].Shadowed);
        Assert.True(shadowed[0].Enabled); // no "enabled" means enabled
    }

    [Fact]
    public void Marketplace_list_and_catalog_from_the_cli()
    {
        // Real `omp plugin marketplace list` and `omp plugin discover local-mkt`
        var markets = OmpPlugins.ParseMarketplaces("Configured Marketplaces:\n\n  local-mkt  /home/u/fixtures/local-mkt\n")!;
        Assert.Equal([new MarketplaceInfo("local-mkt", "/home/u/fixtures/local-mkt")], markets);
        Assert.Empty(OmpPlugins.ParseMarketplaces("No marketplaces configured\n\nAdd one with: omp plugin marketplace add <source>")!);
        Assert.Null(OmpPlugins.ParseMarketplaces("✘ Failed to list marketplaces: boom"));

        var cli = OmpPlugins.ParseDiscover("Available Plugins (local-mkt):\n\n  hello-skill@0.1.0\n    Adds a skill that says hello\n  lint-kit@2.0.1\n    Lint commands for any project\n")!;
        Assert.Equal([new MarketplacePluginInfo("hello-skill", "0.1.0", "Adds a skill that says hello"), new MarketplacePluginInfo("lint-kit", "2.0.1", "Lint commands for any project")], cli);
        // The same catalog as /marketplace discover prints it over RPC
        var rpc = OmpPlugins.ParseDiscover("Available plugins:\n  - hello-skill@0.1.0\n      Adds a skill that says hello\n  - bare")!;
        Assert.Equal([new MarketplacePluginInfo("hello-skill", "0.1.0", "Adds a skill that says hello"), new MarketplacePluginInfo("bare", null, null)], rpc);
        Assert.Empty(OmpPlugins.ParseDiscover("No plugins found in local-mkt")!);
    }

    [Fact]
    public void Config_values_and_disabled_skills()
    {
        var config = OmpPlugins.ParseConfigList("""
            { "disabledExtensions": { "value": ["skill:tdd", "mcp:x", "skill:"], "type": "array", "description": "" },
              "skills.enableClaudeUser": { "value": true, "type": "boolean", "description": "" },
              "marketplace.autoUpdate": { "value": "auto", "type": "enum", "description": "Check for plugin updates on startup" },
              "some.secret": { "redacted": true, "type": "string", "description": "" } }
            """)!;
        Assert.True(OmpPlugins.Bool(config, "skills.enableClaudeUser", false));
        Assert.True(OmpPlugins.Bool(config, "skills.enablePiUser", true));
        Assert.Equal("auto", OmpPlugins.String(config, "marketplace.autoUpdate"));
        Assert.Equal(["tdd"], OmpPlugins.DisabledSkills(OmpPlugins.Strings(config["disabledExtensions"])));
        Assert.Null(config["some.secret"]);

        var get = OmpPlugins.ParseConfigGet("{\n  \"key\": \"disabledExtensions\",\n  \"value\": [],\n  \"type\": \"array\",\n  \"description\": \"\"\n}", out var ok);
        Assert.True(ok);
        Assert.Empty(OmpPlugins.Strings(get));
        Assert.Equal("[\"skill:a\",\"mcp:b\"]", OmpPlugins.ArrayValue(["skill:a", "mcp:b"]));
        Assert.Equal("Failed to install x: Error: bun install failed: 404", OmpPlugins.Clean("✘ Failed to install x: Error: bun install failed: 404\n"));
        Assert.True(PluginsViewModel.IsNewer("2.1.0", "2.0.1"));
        Assert.False(PluginsViewModel.IsNewer("2.0.1", "2.0.1"));
    }
}

/// <summary>The Connectors page against the fake omp: the commands it sends (logged) and what it shows.</summary>
public class ConnectorsFlowTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    internal sealed record Setup(MainViewModel Vm, SessionController Session, string Project, string UserDir, string CommandLog, string CliLog)
    {
        public string[] Sent => File.Exists(CommandLog) ? File.ReadAllLines(CommandLog) : [];
        public string[] Cli => File.Exists(CliLog) ? File.ReadAllLines(CliLog) : [];
    }

    /// <summary>A fake omp answering /mcp and friends, a fake CLI, a user mcp.json and a project .omp/mcp.json.</summary>
    internal static async Task<Setup> StartAsync(Dictionary<string, string>? commands = null, Dictionary<string, object>? cli = null, bool start = true,
        bool projectFiles = true)
    {
        var dir = TestProcesses.TempDir("connectors");
        var project = Path.Combine(dir, "project");
        var userDir = Path.Combine(dir, "agent");
        Directory.CreateDirectory(Path.Combine(project, ".omp"));
        Directory.CreateDirectory(userDir);
        File.WriteAllText(Path.Combine(userDir, "mcp.json"), """
            { "mcpServers": { "echo": { "type": "stdio", "command": "/opt/bun/bin/bun", "args": ["echo.js"], "env": { "ECHO_KEY": "k" }, "enabled": true } },
              "disabledServers": ["docs"] }
            """);
        // The project folder's own files (Claude Code's .mcp.json, a plain mcp.json): omp loads them, /mcp list leaves them out.
        if (projectFiles) File.WriteAllText(Path.Combine(project, ".mcp.json"), """
            { "mcpServers": { "shared": { "command": "npx", "args": ["-y", "shared-mcp"] }, "echo": { "command": "other" } } }
            """);
        if (projectFiles) File.WriteAllText(Path.Combine(project, "mcp.json"), """
            { "mcpServers": { "docs": { "url": "https://docs.example.com/mcp?key=secret" } } }
            """);
        File.WriteAllText(Path.Combine(project, ".omp", "mcp.json"), """
            { "mcpServers": { "broken": { "type": "stdio", "command": "/nonexistent/cmd", "args": ["--flag"] },
                              "web": { "type": "sse", "url": "https://example.invalid/mcp", "headers": { "Authorization": "Bearer abc" } } } }
            """);
        var answers = new Dictionary<string, string>
        {
            ["/mcp list"] = "echo | stdio | enabled | /opt/bun/bin/bun [user]\nbroken | stdio | enabled | /nonexistent/cmd [project]\nweb | sse | enabled | https://example.invalid/mcp [project]",
            ["/tools"] = ConnectorsParsingTests.RealTools + "\n- mcp__claude_thing_run",
            ["/mcp test echo"] = "Server \"echo\" connected (2 tools).\n  - echo\n  - add",
            ["/mcp test broken"] = "Connection to \"broken\" failed: ENOENT: no such file or directory, posix_spawn '/nonexistent/cmd'",
            ["/mcp disable"] = "Server \"echo\" disabled (user config).",
            ["/mcp disable shared"] = "Server \"shared\" disabled.",
            ["/mcp enable"] = "Server \"echo\" enabled (user config).",
            ["/mcp remove"] = "Removed server \"broken\" from project config.",
            ["/mcp add"] = "Added MCP server \"github\" (user).",
            ["/mcp resources"] = "echo/file:///readme.md\necho/memo://today",
            ["/mcp prompts"] = "echo/summarize — Summarize a text\necho/greet",
            ["/mcp smithery-search"] = "Smithery authentication required. Run /mcp smithery-login in the TUI client or add an API key to ~/.omp/agent/smithery.json.",
            ["/reload-plugins"] = "Plugins reloaded.",
        };
        foreach (var (k, v) in commands ?? []) answers[k] = v;
        var cliAnswers = new Dictionary<string, object>
        {
            ["config path"] = new { stdout = userDir + "\n" },
            ["config get mcp.enableProjectConfig --json"] = new { stdout = "{\n  \"key\": \"mcp.enableProjectConfig\",\n  \"value\": true,\n  \"type\": \"boolean\",\n  \"description\": \"Load .mcp.json/mcp.json from project root\"\n}\n" },
            ["config set mcp.enableProjectConfig"] = new { stdout = "{\"key\":\"mcp.enableProjectConfig\",\"value\":false}\n" },
        };
        foreach (var (k, v) in cli ?? []) cliAnswers[k] = v;
        var commandsFile = Path.Combine(dir, "commands.json");
        var cliFile = Path.Combine(dir, "cli.json");
        File.WriteAllText(commandsFile, System.Text.Json.JsonSerializer.Serialize(answers));
        File.WriteAllText(cliFile, System.Text.Json.JsonSerializer.Serialize(cliAnswers));
        var log = Path.Combine(dir, "sent.log");
        var cliLog = Path.Combine(dir, "cli.log");
        var sessions = Path.Combine(dir, "sessions");
        var s = new SessionController(req =>
        {
            var spec = TestProcesses.FakeWithCommands("normal", commandsFile, log);
            return spec with
            {
                WorkingDirectory = req.WorkingDirectory,
                Environment = new Dictionary<string, string?>(spec.Environment) { ["FAKE_SESSION_DIR"] = sessions },
            };
        }, new LaunchRequest(project));
        var vm = new MainViewModel(s, new AppArgs()) { OmpCliLaunch = TestProcesses.FakeCli(cliFile, cliLog) };
        if (start) await s.StartAsync();
        return new(vm, s, project, userDir, log, cliLog);
    }

    internal static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("never reached: " + what);
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task The_page_lists_omps_servers_with_details_and_what_this_session_has()
    {
        var t = await StartAsync();
        await using var _ = t.Session;
        var c = t.Vm.Connectors;
        await c.RefreshAsync();
        await Until(() => c.Servers.FirstOrDefault()?.HasExtras == true, "user file details");

        Assert.True(c.ShowList);
        Assert.False(c.ShowEmpty);
        Assert.Equal(["echo", "broken", "web", "docs", "shared"], c.Servers.Select(s => s.Name));
        var echo = c.Servers[0];
        Assert.Equal(("All projects", "stdio"), (echo.ScopeLabel, echo.TransportLabel));
        Assert.Equal("/opt/bun/bin/bun echo.js", echo.Detail);
        Assert.Equal("Environment: ECHO_KEY", echo.Extras); // names only, never values
        Assert.Equal(ConnectorState.Connected, echo.State);
        Assert.Equal("Connected · 2 tools", echo.StateText);
        var broken = c.Servers[1];
        Assert.Equal(("This project", "/nonexistent/cmd --flag"), (broken.ScopeLabel, broken.Detail));
        Assert.Equal(ConnectorState.NotConnected, broken.State);
        var web = c.Servers[2];
        Assert.True(web.IsRemote);
        Assert.Equal("Headers: Authorization", web.Extras);
        Assert.Equal("SSE", web.TransportLabel);
        Assert.Equal("mcp__claude_thing_run", c.OtherTools);
        Assert.Equal(["/mcp list", "/tools"], t.Sent);

        // The project folder's own mcp.json / .mcp.json: listed after omp's, a name omp's files use is omp's.
        var docs = c.Servers.Single(s => s.Name == "docs");
        Assert.Equal(("mcp.json", "http", "https://docs.example.com/mcp"), (docs.ScopeLabel, docs.Transport, docs.Detail));
        Assert.False(docs.IsEnabled); // omp's disabledServers
        var shared = c.Servers.Single(s => s.Name == "shared");
        Assert.Equal((".mcp.json", "npx -y shared-mcp", true), (shared.ScopeLabel, shared.Detail, shared.IsEnabled));
        Assert.True(shared.IsFromProjectFile);
        Assert.False(shared.HasMenu || shared.CanTest || shared.CanEdit);
        shared.IsEnabled = false; // the switch still works: omp's /mcp disable puts it in disabledServers
        await Until(() => c.NeedsRestart, "restart asked");
        Assert.Equal("/mcp disable shared", t.Sent.Last());
        Assert.Contains("config path", t.Cli);
        await Until(() => c.HasOptions, "the option read through omp config");
        Assert.True(c.ProjectServers);
    }

    [Fact]
    public async Task The_switch_sends_mcp_disable_and_asks_for_a_restart()
    {
        var t = await StartAsync();
        await using var _ = t.Session;
        var c = t.Vm.Connectors;
        await c.RefreshAsync();
        var echo = c.Servers.Single(s => s.Name == "echo");

        echo.IsEnabled = false; // the switch
        await Until(() => c.NeedsRestart, "restart asked");
        Assert.Equal("/mcp disable echo", t.Sent.Last());
        Assert.False(echo.IsEnabled);
        Assert.Equal(ConnectorState.Off, echo.State);
        Assert.Equal("Turned off “echo”.", c.Feedback.Notice);
        Assert.True(c.Feedback.ShowRestart);
        Assert.Contains("Restart it", c.Feedback.RestartText);

        // omp refuses: the switch goes back and omp's words are shown
        var refused = await StartAsync(new() { ["/mcp enable"] = "Server \"nope\" not found in user or project config." });
        await using var __ = refused.Session;
        await refused.Vm.Connectors.RefreshAsync();
        var row = refused.Vm.Connectors.Servers[0];
        row.SetEnabledQuietly(false);
        row.IsEnabled = true;
        await Until(() => refused.Vm.Connectors.Feedback.HasError, "refusal shown");
        Assert.False(row.IsEnabled);
        Assert.Equal("Server \"nope\" not found in user or project config.", refused.Vm.Connectors.Feedback.Error);
        Assert.False(refused.Vm.Connectors.NeedsRestart);
    }

    [Fact]
    public async Task Remove_waits_for_the_confirmation_and_names_the_scope()
    {
        var t = await StartAsync();
        await using var _ = t.Session;
        var c = t.Vm.Connectors;
        await c.RefreshAsync();
        var broken = c.Servers.Single(s => s.Name == "broken");

        broken.AskRemoveCommand.Execute(null);
        Assert.True(broken.IsConfirmingRemove);
        Assert.DoesNotContain(t.Sent, x => x.StartsWith("/mcp remove", StringComparison.Ordinal));
        broken.CancelRemoveCommand.Execute(null);
        Assert.False(broken.IsConfirmingRemove);

        broken.AskRemoveCommand.Execute(null);
        await broken.RemoveCommand.ExecuteAsync(null);
        Assert.Equal("/mcp remove broken --scope project", t.Sent.Last());
        Assert.DoesNotContain(broken, c.Servers);
        Assert.Equal("Removed “broken”.", c.Feedback.Notice);
        Assert.True(c.NeedsRestart);
    }

    [Fact]
    public async Task Test_shows_the_tools_or_omps_reason()
    {
        var t = await StartAsync();
        await using var _ = t.Session;
        var c = t.Vm.Connectors;
        await c.RefreshAsync();
        var echo = c.Servers[0];
        await echo.TestCommand.ExecuteAsync(null);
        Assert.Equal("/mcp test echo", t.Sent.Last());
        Assert.Equal(("Test passed · 2 tools", "echo, add"), (echo.StateText, echo.Tools));

        var broken = c.Servers[1];
        await broken.TestCommand.ExecuteAsync(null);
        Assert.Equal(ConnectorState.Failed, broken.State);
        Assert.StartsWith("ENOENT", broken.Failure);
        Assert.False(broken.SuggestSignIn);

        // A test result stays through a refresh of the list
        await c.RefreshAsync();
        Assert.Equal(ConnectorState.Failed, c.Servers[1].State);
        // A remote server refused with 401 offers omp's terminal sign-in
        var web = c.Servers[2];
        web.Failure = "HTTP 401: Unauthorized";
        Assert.True(web.SuggestSignIn);
        Assert.Equal("/mcp reauth web", web.ReauthCommand);
    }

    [Fact]
    public async Task The_add_form_checks_what_omp_would_refuse_before_sending()
    {
        var t = await StartAsync();
        await using var _ = t.Session;
        var c = t.Vm.Connectors;
        await c.RefreshAsync();
        c.OpenAddFormCommand.Execute(null);
        Assert.True(c.IsFormOpen);
        Assert.Equal(("stdio", "user"), (c.FormTransport, c.FormScope));

        async Task<string> Submit()
        {
            await c.SubmitFormCommand.ExecuteAsync(null);
            return c.FormError;
        }

        Assert.Equal("Give the connector a name.", await Submit());
        c.FormName = "my server";
        Assert.Contains("no spaces", await Submit());
        c.FormName = "echo";
        Assert.Contains("already a connector named “echo”", await Submit());
        c.FormName = "github";
        Assert.Contains("Enter the command", await Submit());
        c.FormCommand = "npx";
        c.FormEnv = "GITHUB_TOKEN";
        Assert.Equal("“GITHUB_TOKEN” is not NAME=value.", await Submit());
        c.SetTransportCommand.Execute("http");
        Assert.Equal("Enter the server's URL.", await Submit());
        c.FormUrl = "https://x";
        c.FormHeaders = "no colon";
        Assert.Equal("“no colon” is not Name: value.", await Submit());
        Assert.DoesNotContain(t.Sent, x => x.StartsWith("/mcp add", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Adding_goes_through_mcp_add_and_puts_the_environment_in_omps_entry()
    {
        var t = await StartAsync();
        await using var _ = t.Session;
        var c = t.Vm.Connectors;
        await c.RefreshAsync();
        c.OpenAddFormCommand.Execute(null);
        c.FormName = "github";
        c.FormCommand = "npx";
        c.FormArgs = "-y\n@modelcontextprotocol/server-github\n";
        c.FormEnv = "GITHUB_TOKEN=ghp_x=y\n";
        // The fake answers but does not write the file: put omp's entry where /mcp add would.
        File.WriteAllText(Path.Combine(t.UserDir, "mcp.json"), """
            { "mcpServers": { "github": { "type": "stdio", "command": "npx", "args": ["-y", "@modelcontextprotocol/server-github"] } } }
            """);
        await c.SubmitFormCommand.ExecuteAsync(null);

        Assert.Contains("/mcp add github --scope user -- npx -y @modelcontextprotocol/server-github", t.Sent);
        Assert.Equal("", c.FormError);
        Assert.False(c.IsFormOpen);
        var entry = McpConfigFile.ReadServer(Path.Combine(t.UserDir, "mcp.json"), "github")!;
        Assert.Equal("ghp_x=y", entry["env"]!["GITHUB_TOKEN"]!.GetValue<string>());
        Assert.Equal("npx", entry["command"]!.GetValue<string>());
        Assert.Equal("Added “github”.", c.Feedback.Notice);
        Assert.True(c.Feedback.ShowRestart);

        // omp's own refusal is shown in the form, which stays open
        var refused = await StartAsync(new() { ["/mcp add"] = "Failed to add server: Server \"github\" already exists in /x/mcp.json" });
        await using var __ = refused.Session;
        var r = refused.Vm.Connectors;
        await r.RefreshAsync();
        r.OpenAddFormCommand.Execute(null);
        r.FormName = "github";
        r.SetTransportCommand.Execute("sse");
        r.SetScopeCommand.Execute("project");
        r.FormUrl = "mcp.example.com/sse";
        r.FormToken = "abc";
        await r.SubmitFormCommand.ExecuteAsync(null);
        Assert.Equal("/mcp add github --scope project --url mcp.example.com/sse --transport sse --token abc", refused.Sent.Last());
        Assert.StartsWith("Failed to add server", r.FormError);
        Assert.True(r.IsFormOpen);
    }

    [Fact]
    public async Task Editing_changes_only_the_form_fields_in_the_file()
    {
        var t = await StartAsync();
        await using var _ = t.Session;
        var c = t.Vm.Connectors;
        await c.RefreshAsync();
        await Until(() => c.Servers[0].CanEdit, "user entry read");
        var web = c.Servers.Single(s => s.Name == "web");
        web.EditCommand.Execute(null);
        Assert.True(c.IsEditing);
        Assert.Equal(("sse", "https://example.invalid/mcp", "abc", ""), (c.FormTransport, c.FormUrl, c.FormToken, c.FormHeaders));
        c.FormUrl = "https://example.org/mcp";
        c.FormHeaders = "X-Team: core";
        await c.SubmitFormCommand.ExecuteAsync(null);
        Assert.Equal("", c.FormError);
        var entry = McpConfigFile.ReadServer(Path.Combine(t.Project, ".omp", "mcp.json"), "web")!;
        Assert.Equal("https://example.org/mcp", entry["url"]!.GetValue<string>());
        Assert.Equal("Bearer abc", entry["headers"]!["Authorization"]!.GetValue<string>());
        Assert.Equal("core", entry["headers"]!["X-Team"]!.GetValue<string>());
        Assert.Equal("Saved “web”.", c.Feedback.Notice);
        Assert.DoesNotContain(t.Sent, x => x.StartsWith("/mcp add", StringComparison.Ordinal) || x.StartsWith("/mcp remove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resources_prompts_and_smithery_sign_in()
    {
        var t = await StartAsync();
        await using var _ = t.Session;
        var c = t.Vm.Connectors;
        await c.LoadItemsCommand.ExecuteAsync(null);
        Assert.Equal(["file:///readme.md", "memo://today"], c.Resources.Select(r => r.Name));
        Assert.Equal(["/echo:summarize", "/echo:greet"], c.Prompts.Select(p => p.Command));
        Assert.Equal("Summarize a text", c.Prompts[0].Description);

        c.SearchQuery = "git hub --scope user";
        await c.SearchCommand.ExecuteAsync(null);
        Assert.Equal("/mcp smithery-search git hub --limit 20", t.Sent.Last());
        Assert.True(c.SearchNeedsSignIn);
        Assert.StartsWith("Smithery authentication required", c.SearchMessage);
        Assert.Equal("/mcp smithery-login", c.SmitheryLoginCommand);
    }

    [Fact]
    public async Task Nothing_is_sent_while_omp_is_not_running()
    {
        var t = await StartAsync(start: false);
        await using var _ = t.Session;
        var c = t.Vm.Connectors;
        await c.RefreshAsync();
        Assert.True(c.ShowNotRunning);
        Assert.False(c.ShowList);
        Assert.False(c.CanChange);
        c.OpenAddFormCommand.Execute(null);
        c.FormName = "x";
        c.FormCommand = "y";
        await c.SubmitFormCommand.ExecuteAsync(null);
        Assert.Contains("omp isn't running", c.FormError);
        Assert.Empty(t.Sent);
    }

    [Fact]
    public async Task The_project_servers_option_goes_through_omp_config()
    {
        var t = await StartAsync();
        await using var _ = t.Session;
        var c = t.Vm.Connectors;
        await c.RefreshAsync();
        await Until(() => c.HasOptions, "option read");
        c.ProjectServers = false;
        await Until(() => c.NeedsRestart, "restart asked");
        Assert.Contains("config set mcp.enableProjectConfig false --json", t.Cli);
        Assert.True(c.OptionsFeedback.ShowRestart);
        Assert.False(c.Feedback.ShowRestart);
    }
}

/// <summary>The Plugins and skills page against a fake CLI (logged) and the fake omp.</summary>
public class PluginsFlowTests
{
    private static readonly string Markets = "Configured Marketplaces:\n\n  local-mkt  /home/u/fixtures/local-mkt\n";
    private static readonly string Catalog = "Available Plugins (local-mkt):\n\n  hello-skill@0.2.0\n    Adds a skill that says hello\n  lint-kit@2.0.1\n    Lint commands for any project\n";

    internal static Dictionary<string, object> Cli(string? list = null) => new()
    {
        ["plugin list --json"] = new { stdout = list ?? ConnectorsParsingTests.RealPluginList },
        ["plugin marketplace list"] = new { stdout = Markets },
        ["plugin discover local-mkt"] = new { stdout = Catalog },
        ["config list --json"] = new
        {
            stdout = """
                { "disabledExtensions": { "value": ["skill:tdd", "rule:x"], "type": "array", "description": "" },
                  "skills.enabled": { "value": true, "type": "boolean", "description": "" },
                  "skills.enableClaudeUser": { "value": false, "type": "boolean", "description": "" },
                  "skills.customDirectories": { "value": ["~/.omp/profiles/harness/skills"], "type": "array", "description": "" },
                  "marketplace.autoUpdate": { "value": "notify", "type": "enum", "description": "" } }
                """,
        },
        ["config get disabledExtensions --json"] = new { stdout = "{ \"key\": \"disabledExtensions\", \"value\": [\"skill:tdd\", \"rule:x\"], \"type\": \"array\", \"description\": \"\" }" },
        ["config set"] = new { stdout = "{}" },
        ["plugin disable omp-hello --json"] = new { stdout = "{\"disabled\":\"omp-hello\"}\n" },
        ["plugin enable hello-skill@local-mkt --json --scope user"] = new { stdout = "{\"enabled\":\"hello-skill@local-mkt\"}\n" },
        ["plugin uninstall omp-hello"] = new { stdout = "✔ Uninstalled omp-hello\n" },
        ["plugin upgrade hello-skill@local-mkt --scope user"] = new { stdout = "Upgraded hello-skill@local-mkt (user) to 0.2.0\n" },
        ["plugin install left-pad-nope"] = new { stderr = "✘ Failed to install left-pad-nope: Error: bun install failed: Resolving dependencies\nerror: GET https://registry.npmjs.org/left-pad-nope - 404\n", exit = 1 },
        ["plugin install lint-kit@local-mkt"] = new { stdout = "✔ Installed lint-kit from local-mkt (2.0.1)\n" },
        ["plugin marketplace add"] = new { stdout = "✔ Added marketplace: owner/repo\n" },
        ["plugin marketplace remove local-mkt"] = new { stdout = "✔ Removed marketplace: local-mkt\n" },
    };

    [Fact]
    public async Task The_page_lists_plugins_marketplaces_skills_and_sources()
    {
        var t = await ConnectorsFlowTests.StartAsync(cli: Cli());
        await using var _ = t.Session;
        var p = t.Vm.Plugins;
        await p.RefreshAsync();

        Assert.Equal(["omp-hello", "hello-skill"], p.Plugins.Select(x => x.Name));
        var npm = p.Plugins[0];
        Assert.Equal(("v1.2.0", "npm", "Greets you from a plugin", true), (npm.Version, npm.SourceLabel, npm.Description, npm.IsEnabled));
        var market = p.Plugins[1];
        Assert.Equal(("local-mkt", false), (market.SourceLabel, market.IsEnabled));
        Assert.Equal("Adds a skill that says hello", market.Description); // from the marketplace's catalog
        Assert.Equal("0.2.0", market.UpdateVersion);
        Assert.Equal(["local-mkt"], p.Marketplaces.Select(m => m.Name));
        Assert.Equal("notify", p.AutoUpdate);
        Assert.False(p.SkillSources.Single(s => s.Key == "skills.enableClaudeUser").IsOn);
        Assert.True(p.SkillSources.Single(s => s.Key == "skills.enablePiUser").IsOn);
        Assert.Equal(["~/.omp/profiles/harness/skills"], p.Directories.Select(d => d.Path));
        // The fake's catalog has one skill; "tdd" is off in disabledExtensions and still listed, with its switch off
        Assert.Equal(["systematic-debugging", "tdd"], p.Skills.Select(s => s.Name));
        Assert.True(p.Skills[0].IsEnabled);
        Assert.False(p.Skills[1].IsEnabled);
        Assert.False(p.Skills[1].CanUse);
        Assert.Equal(new[] { "plugin list --json", "plugin marketplace list", "config list --json", "plugin discover local-mkt" }.Order(), t.Cli.Order());
    }

    [Fact]
    public async Task Switching_a_plugin_runs_omps_cli_then_reloads_plugins()
    {
        var t = await ConnectorsFlowTests.StartAsync(cli: Cli());
        await using var _ = t.Session;
        var p = t.Vm.Plugins;
        await p.RefreshAsync();
        p.Plugins[0].IsEnabled = false;
        await ConnectorsFlowTests.Until(() => p.NeedsRestart, "restart asked");
        Assert.Contains("plugin disable omp-hello --json", t.Cli);
        Assert.Equal("/reload-plugins", t.Sent.Last());
        Assert.Equal("Turned off “omp-hello”. Skills and commands are updated.", p.PluginsFeedback.Notice);
        Assert.Contains("tools, hooks and MCP servers", p.PluginsFeedback.RestartText);

        p.Plugins[1].IsEnabled = true;
        await ConnectorsFlowTests.Until(() => t.Cli.Contains("plugin enable hello-skill@local-mkt --json --scope user"), "marketplace plugin enabled with its scope");
    }

    [Fact]
    public async Task Uninstall_asks_first_and_errors_are_omps_words()
    {
        var t = await ConnectorsFlowTests.StartAsync(cli: Cli());
        await using var _ = t.Session;
        var p = t.Vm.Plugins;
        await p.RefreshAsync();
        var npm = p.Plugins[0];
        npm.AskUninstallCommand.Execute(null);
        Assert.True(npm.IsConfirmingUninstall);
        Assert.DoesNotContain("plugin uninstall omp-hello", t.Cli);
        await npm.UninstallCommand.ExecuteAsync(null);
        Assert.Contains("plugin uninstall omp-hello", t.Cli);
        Assert.DoesNotContain(npm, p.Plugins);
        Assert.StartsWith("Uninstalled “omp-hello”.", p.PluginsFeedback.Notice);

        p.InstallSpec = "left-pad-nope";
        await p.InstallCommand.ExecuteAsync(null);
        Assert.True(p.InstallFailed);
        Assert.StartsWith("Failed to install left-pad-nope: Error: bun install failed", p.InstallOutput);
        Assert.Contains("404", p.InstallOutput);
        Assert.Equal("left-pad-nope", p.InstallSpec); // kept to fix and retry

        await p.Plugins[0].UpgradeCommand.ExecuteAsync(null);
        Assert.Contains("plugin upgrade hello-skill@local-mkt --scope user", t.Cli);
        Assert.StartsWith("Upgraded hello-skill@local-mkt (user) to 0.2.0.", p.PluginsFeedback.Notice);
    }

    [Fact]
    public async Task Marketplaces_add_browse_install_and_remove()
    {
        var t = await ConnectorsFlowTests.StartAsync(cli: Cli());
        await using var _ = t.Session;
        var p = t.Vm.Plugins;
        await p.RefreshAsync();
        var m = p.Marketplaces[0];
        await m.BrowseCommand.ExecuteAsync(null);
        Assert.True(m.IsOpen);
        Assert.Equal(["hello-skill", "lint-kit"], m.Plugins.Select(x => x.Name));
        Assert.True(m.Plugins[0].IsInstalled);
        Assert.False(m.Plugins[0].CanInstall);
        await m.Plugins[1].InstallCommand.ExecuteAsync(null);
        Assert.Contains("plugin install lint-kit@local-mkt", t.Cli);
        Assert.Equal("/reload-plugins", t.Sent.Last());
        Assert.StartsWith("Installed “lint-kit”.", p.MarketplacesFeedback.Notice);

        p.MarketplaceSource = "owner/repo";
        await p.AddMarketplaceCommand.ExecuteAsync(null);
        Assert.Contains("plugin marketplace add owner/repo", t.Cli);
        Assert.Equal("", p.MarketplaceSource);

        m = p.Marketplaces[0];
        m.AskRemoveCommand.Execute(null);
        Assert.True(m.IsConfirmingRemove);
        await m.RemoveCommand.ExecuteAsync(null);
        Assert.Contains("plugin marketplace remove local-mkt", t.Cli);
        Assert.Empty(p.Marketplaces);
    }

    [Fact]
    public async Task Skills_switch_through_disabled_extensions_and_use_goes_to_the_composer()
    {
        var t = await ConnectorsFlowTests.StartAsync(cli: Cli());
        await using var _ = t.Session;
        var p = t.Vm.Plugins;
        await p.RefreshAsync();
        var debugging = p.Skills.Single(s => s.Name == "systematic-debugging");
        debugging.IsEnabled = false;
        await ConnectorsFlowTests.Until(() => p.NeedsRestart, "restart asked");
        // omp's current list is read first, so other ids stay
        Assert.Contains("config set disabledExtensions [\"skill:tdd\",\"rule:x\",\"skill:systematic-debugging\"] --json", t.Cli);
        Assert.True(p.SkillsFeedback.ShowRestart);

        var tdd = p.Skills.Single(s => s.Name == "tdd");
        tdd.IsEnabled = true;
        await ConnectorsFlowTests.Until(() => t.Cli.Contains("config set disabledExtensions [\"rule:x\"] --json"), "tdd back on");

        t.Vm.ComposerText = "the flaky test";
        debugging.SetEnabledQuietly(true);
        debugging.UseCommand.Execute(null);
        Assert.Equal("/skill:systematic-debugging the flaky test", t.Vm.ComposerText);
        Assert.False(t.Vm.IsSettingsOpen);

        p.SkillSources.Single(s => s.Key == "skills.enableClaudeUser").IsOn = true;
        await ConnectorsFlowTests.Until(() => t.Cli.Contains("config set skills.enableClaudeUser true --json"), "source on");
        p.NewDirectory = "~/my-skills";
        await p.AddDirectoryCommand.ExecuteAsync(null);
        Assert.Contains("config set skills.customDirectories [\"~/.omp/profiles/harness/skills\",\"~/my-skills\"] --json", t.Cli);
        Assert.Equal(2, p.Directories.Count);
        p.SetAutoUpdateCommand.Execute("off");
        await ConnectorsFlowTests.Until(() => t.Cli.Contains("config set marketplace.autoUpdate off --json"), "auto update off");
    }

    [Fact]
    public async Task Without_omps_cli_the_page_says_so()
    {
        var t = await ConnectorsFlowTests.StartAsync(cli: new() { ["plugin list --json"] = new { stderr = "✘ boom\n", exit = 1 } });
        await using var _ = t.Session;
        var p = t.Vm.Plugins;
        await p.RefreshAsync();
        Assert.Equal("boom", p.PluginsError);
        Assert.False(p.ShowPluginsEmpty);
        Assert.True(p.HasPluginsError);
    }
}

/// <summary>Against real omp (OMPGUI_TEST_CONFIG) in a throwaway project: /mcp add, list, test, resources, prompts, disable, remove.</summary>
public class RealConnectorsTests(ITestOutputHelper log)
{
    private const string EchoServer = """
        const readline = require("node:readline");
        const rl = readline.createInterface({ input: process.stdin });
        const send = o => process.stdout.write(JSON.stringify(o) + "\n");
        rl.on("line", line => {
          let m; try { m = JSON.parse(line); } catch { return; }
          if (m.id === undefined) return;
          const ok = result => send({ jsonrpc: "2.0", id: m.id, result });
          switch (m.method) {
            case "initialize": return ok({ protocolVersion: m.params?.protocolVersion ?? "2025-03-26", capabilities: { tools: {}, resources: {}, prompts: {} }, serverInfo: { name: "probe", version: "1.0.0" } });
            case "tools/list": return ok({ tools: [{ name: "echo", description: "Echo", inputSchema: { type: "object" } }, { name: "add", description: "Add", inputSchema: { type: "object" } }] });
            case "resources/list": return ok({ resources: [{ uri: "memo://today", name: "Today" }] });
            case "resources/templates/list": return ok({ resourceTemplates: [] });
            case "prompts/list": return ok({ prompts: [{ name: "summarize", description: "Summarize a text" }] });
            default: return send({ jsonrpc: "2.0", id: m.id, error: { code: -32601, message: "Method not found" } });
          }
        });
        """;

    [Fact]
    public async Task Real_omp_manages_a_project_connector_the_way_the_page_reads_it()
    {
        if (TestProcesses.RealOmp() is not { } options)
        {
            Assert.Skip("OMPGUI_TEST_CONFIG is not set");
            return;
        }
        var project = TestProcesses.TempDir("real-mcp");
        var script = Path.Combine(project, "probe.js");
        File.WriteAllText(script, EchoServer);
        await using var s = new SessionController(options.ToLaunchSpec, new LaunchRequest(project), TimeSpan.FromSeconds(90));
        await s.StartAsync();

        async Task<string> Run(string command)
        {
            var r = await s.RunSlashCommandAsync(command, TimeSpan.FromMinutes(2));
            log.WriteLine($"=== {command}\n{r.Output}{r.Error}\n");
            Assert.True(r.Ok, command + ": " + r.Error);
            return r.Output;
        }

        var add = OmpMcp.AddCommand("gui-probe", "project", "stdio", options.Command ?? "bun", [script], null, null);
        Assert.True(OmpMcp.IsAdded(await Run(add), "gui-probe"));
        var entry = Assert.Single(OmpMcp.ParseList(await Run("/mcp list"))!, e => e.Name == "gui-probe");
        Assert.Equal(("stdio", "project", true), (entry.Transport, entry.Scope, entry.Enabled));
        var test = OmpMcp.ParseTest(await Run("/mcp test gui-probe"));
        Assert.True(test.Connected, test.Message);
        Assert.Equal(["echo", "add"], test.Tools);
        Assert.Contains(new McpItem("gui-probe", "memo://today", null), OmpMcp.ParseItems(await Run("/mcp resources"), out _));
        Assert.Contains(new McpItem("gui-probe", "summarize", "Summarize a text"), OmpMcp.ParseItems(await Run("/mcp prompts"), out _));
        Assert.True(OmpMcp.IsToggled(await Run("/mcp disable gui-probe"), "gui-probe", false));
        Assert.False(OmpMcp.ParseList(await Run("/mcp list"))!.Single(e => e.Name == "gui-probe").Enabled);
        Assert.True(OmpMcp.IsRemoved(await Run(OmpMcp.RemoveCommand("gui-probe", "project")), "gui-probe"));
        Assert.DoesNotContain(OmpMcp.ParseList(await Run("/mcp list")) ?? [], e => e.Name == "gui-probe");
        Assert.NotNull(OmpPlugins.ParseList((await OmpCli.RunAsync(options.ToCliLaunchSpec(["plugin", "list", "--json"], project))).Stdout));
    }
}
