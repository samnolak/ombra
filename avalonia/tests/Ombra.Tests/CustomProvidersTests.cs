using System.Net;
using System.Net.Sockets;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;
using Ombra.ClientCore.Network;
using Ombra.Rpc;

namespace Ombra.Tests;

/// <summary>
/// A model server for the tests: Ollama's <c>/api/tags</c> and an OpenAI-style <c>/v1/models</c> listing
/// <see cref="Models"/>, and a chat reply for <c>/v1/chat/completions</c>; anything else is 404. It records each
/// request's Authorization header.
/// </summary>
internal sealed class StubModelServer : IDisposable
{
    private readonly HttpListener _listener = new();
    public int Port { get; }
    public string Root => $"http://127.0.0.1:{Port}";
    public IReadOnlyList<string> Models { get; init; } = ["stub-small", "stub-large"];
    public List<(string Path, string? Authorization)> Requests { get; } = [];

    public StubModelServer()
    {
        Port = FreePort();
        _listener.Prefixes.Add(Root + "/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext c;
            try { c = await _listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            var path = c.Request.Url!.AbsolutePath;
            lock (Requests) Requests.Add((path, c.Request.Headers["Authorization"]));
            var body = path switch
            {
                "/api/tags" => System.Text.Json.JsonSerializer.Serialize(new { models = Models.Select(m => new { name = m, model = m }) }),
                "/v1/models" => System.Text.Json.JsonSerializer.Serialize(new { @object = "list", data = Models.Select(m => new { id = m, @object = "model" }) }),
                _ => null,
            };
            c.Response.StatusCode = body is null ? 404 : 200;
            c.Response.ContentType = "application/json";
            var bytes = System.Text.Encoding.UTF8.GetBytes(body ?? """{"error":"not found"}""");
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }
    }

    public void Dispose() => _listener.Close();
}

/// <summary>Settings → Model providers → Local and custom over the fake omp: models.yml written, omp restarted, a broken file refused.</summary>
public sealed class CustomProvidersTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMBRA_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static Task Until(Func<bool> c, string what, int seconds = 20) => WorkspaceFixture.Until(c, what, seconds);

    private static void Shot(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ShotDir);
        using var frame = w.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(ShotDir, name + ".png"));
        frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private const string UserModels = """
        # written by hand
        providers:
          work:
            baseUrl: https://llm.example.com/v1
            api: openai-completions
            apiKey: WORK_KEY
            headers:
              X-Team: core
            models:
              - id: big
        """;

    /// <summary>The fixture's omp CLI answers <c>config path</c> with a temp agent folder holding <paramref name="models"/>.</summary>
    private static string AgentDir(WorkspaceFixture f, string? models = UserModels)
    {
        var agent = Path.Combine(f.Dir, "agent");
        Directory.CreateDirectory(agent);
        f.Cli["config path"] = new { stdout = agent + "\n" };
        if (models is not null) File.WriteAllText(Path.Combine(agent, "models.yml"), models);
        return agent;
    }

    /// <summary>Ollama at the stub; the others at ports where nothing listens.</summary>
    private static IReadOnlyList<LocalModelServer> Servers(StubModelServer stub)
    {
        var d = LocalModelServer.Defaults(_ => null);
        return
        [
            d[0] with { BaseUrl = stub.Root },
            d[1] with { BaseUrl = $"http://127.0.0.1:{StubModelServer.FreePort()}/v1" },
            d[2] with { BaseUrl = $"http://127.0.0.1:{StubModelServer.FreePort()}" },
            d[3] with { BaseUrl = $"http://127.0.0.1:{StubModelServer.FreePort()}/v1" },
        ];
    }

    [AvaloniaFact]
    public async Task A_running_local_server_is_added_to_models_yml_and_omp_restarts_with_it()
    {
        using var stub = new StubModelServer();
        var f = new WorkspaceFixture();
        var agent = AgentDir(f);
        var (w, vm) = await f.OpenAsync(TestProcesses.TempDir("providers-project"), height: 900);
        var page = vm.CustomProviders;
        page.Servers = Servers(stub);
        await vm.OpenSettingsAtAsync("providers");
        await Until(() => page.LocalServers.Count == 4 && page.LocalServers.All(r => !r.Checking && r.Status != Loc.T("Checking…")), "local servers checked");
        Assert.Equal([Loc.T("Running · ") + Loc.F("{0} models", 2), Loc.T("Not running"), Loc.T("Not running"), Loc.T("Not running")], page.LocalServers.Select(r => r.Status));
        Assert.True(page.LocalServers[0].IsRunning);
        var work = Assert.Single(page.CustomProviders);
        Assert.False(work.IsEditable);
        Assert.Contains("headers", work.ReadOnlyNote);
        Assert.EndsWith("agent/models.yml", page.FileLabel.Replace('\\', '/'));

        var ollama = page.LocalServers[0];
        ollama.BeginAddCommand.Execute(null);
        Assert.True(ollama.IsAddOpen);
        var pid = vm.Session.ProcessId;
        await ollama.ConfirmAddCommand.ExecuteAsync(null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Session.ProcessId != pid, "omp restarted");
        var config = ModelsConfig.Load(Path.Combine(agent, "models.yml"));
        Assert.Equal(["work", "ollama"], config.Providers.Select(p => p.Id));
        Assert.True(config.Providers[1].SameAs(new ModelProviderEntry { Id = "ollama", BaseUrl = stub.Root, Api = ModelApis.Responses, NoAuth = true, Discovery = "ollama" }));
        Assert.StartsWith(UserModels, File.ReadAllText(Path.Combine(agent, "models.yml")));
        Assert.Contains(Loc.F("Added {0}.", "Ollama"), page.Message);
        await Until(() => page.LocalServers[0].IsAdded && page.LocalServers[0].IsRunning, "listed as added");
        await WorkspaceFixture.Settle();
        Shot(w, "ui-providers-local");
        vm.SettingsTheme = "dark";
        await WorkspaceFixture.Settle();
        Shot(w, "ui-providers-local-dark");
        vm.SettingsTheme = "system";

        // Remove: the trash icon asks first
        var added = page.LocalServers[0];
        added.AskRemoveCommand.Execute(null);
        Assert.True(added.IsConfirmingRemove);
        pid = vm.Session.ProcessId;
        await added.ConfirmRemoveCommand.ExecuteAsync(null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Session.ProcessId != pid, "omp restarted after removal");
        Assert.Equal(UserModels, File.ReadAllText(Path.Combine(agent, "models.yml")).TrimEnd('\n'));
        Assert.False(page.LocalServers[0].IsAdded);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_custom_provider_is_tested_saved_edited_and_removed()
    {
        using var stub = new StubModelServer();
        var f = new WorkspaceFixture();
        var agent = AgentDir(f);
        var (w, vm) = await f.OpenAsync(TestProcesses.TempDir("providers-project"), height: 1000);
        var page = vm.CustomProviders;
        page.Servers = Servers(stub);
        await vm.OpenSettingsAtAsync("providers");
        await Until(() => page.CanChange && page.CustomProviders.Count == 1, "page loaded");

        page.BeginAddCommand.Execute(null);
        await page.SaveFormCommand.ExecuteAsync(null);
        Assert.Contains(Loc.T("Give the provider a name, such as My server."), page.FormError);
        page.FormName = "My Server";
        Assert.Equal(Loc.F("omp will call it {0}.", "my-server"), page.FormIdNote);
        page.FormBaseUrl = stub.Root;
        page.FormApiKey = "sk-test-key";
        await page.TestCommand.ExecuteAsync(null);
        Assert.False(page.TestOk);
        Assert.Contains(Loc.F("Nothing at {0}, but {1} answers with {2}. Use {1} as the address.", "\0", stub.Root + "/v1", "\0").Split('\0')[^1], page.TestResult);
        page.FormBaseUrl = stub.Root + "/v1";
        await page.TestCommand.ExecuteAsync(null);
        Assert.True(page.TestOk, page.TestResult);
        Assert.Equal(Loc.F("Connected: {0} ({1}).", Loc.F("{0} models", 2), "stub-small, stub-large"), page.TestResult);
        Assert.Contains(("/v1/models", "Bearer sk-test-key"), stub.Requests);
        Assert.DoesNotContain("sk-test-key", page.TestResult);
        await WorkspaceFixture.Settle();
        w.GetVisualDescendants().OfType<Border>().First(b => b.Name == "CustomProviderForm").BringIntoView();
        await WorkspaceFixture.Settle();
        Shot(w, "ui-providers-custom-form");
        vm.SettingsTheme = "dark";
        await WorkspaceFixture.Settle();
        Shot(w, "ui-providers-custom-form-dark");
        vm.SettingsTheme = "system";

        var pid = vm.Session.ProcessId;
        await page.SaveFormCommand.ExecuteAsync(null);
        Assert.False(page.IsFormOpen, page.FormError);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Session.ProcessId != pid, "omp restarted");
        var saved = ModelsConfig.Load(Path.Combine(agent, "models.yml")).Providers;
        Assert.Equal(["work", "my-server"], saved.Select(p => p.Id));
        Assert.True(saved[1].SameAs(new ModelProviderEntry
        {
            Id = "my-server", BaseUrl = stub.Root + "/v1", Api = ModelApis.ChatCompletions, ApiKey = "sk-test-key",
            Discovery = "openai-models-list", InjectV1 = false,
        }));
        Assert.Equal(["work", "my-server"], page.CustomProviders.Select(p => p.Id));
        var mine = page.CustomProviders[1];
        Assert.True(mine.IsEditable);
        Assert.Equal($"{stub.Root}/v1 · Chat Completions · {Loc.T("models from the server")}", mine.Detail);

        // Edit: a listed model set, no key
        mine.EditCommand.Execute(null);
        Assert.Equal(("my-server", "sk-test-key", true), (page.EditingId, page.FormApiKey, page.FormDiscover));
        page.SetDiscoverCommand.Execute("false");
        page.FormApiKey = "";
        await page.TestCommand.ExecuteAsync(null);
        Assert.Equal("stub-small\nstub-large", page.FormModels); // the test fills an empty model list
        page.FormModels = "stub-small";
        await page.SaveFormCommand.ExecuteAsync(null);
        Assert.False(page.IsFormOpen, page.FormError);
        var edited = ModelsConfig.Load(Path.Combine(agent, "models.yml")).Providers[1];
        Assert.True(edited.SameAs(new ModelProviderEntry { Id = "my-server", BaseUrl = stub.Root + "/v1", Api = ModelApis.ChatCompletions, NoAuth = true, Models = ["stub-small"] }));

        // A file that no longer parses is shown, not changed
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        var broken = "providers:\n  my-server: [unclosed\n";
        File.WriteAllText(Path.Combine(agent, "models.yml"), broken);
        await page.LoadAsync();
        Assert.Contains(Loc.F("{0} is not valid YAML (line {1}): {2}", "\0", "\0", "\0").Split('\0')[1], page.LoadError);
        Assert.False(page.CanChange);
        Assert.Empty(page.CustomProviders);
        Assert.Equal(broken, File.ReadAllText(Path.Combine(agent, "models.yml")));

        File.WriteAllText(Path.Combine(agent, "models.yml"), UserModels + "\n" + ModelsConfig.Serialize(edited, 2));
        await page.LoadAsync();
        var row = page.CustomProviders.Single(p => p.Id == "my-server");
        row.AskRemoveCommand.Execute(null);
        await row.ConfirmRemoveCommand.ExecuteAsync(null);
        Assert.Equal(["work"], ModelsConfig.Load(Path.Combine(agent, "models.yml")).Providers.Select(p => p.Id));
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Without_omps_agent_folder_nothing_can_be_changed()
    {
        var f = new WorkspaceFixture();
        f.Cli["config path"] = new { stderr = "no", exit = 1 };
        var vm = await f.StartAsync(TestProcesses.TempDir("providers-project"));
        var page = vm.CustomProviders;
        page.Servers = [];
        await page.LoadAsync();
        Assert.Contains(Loc.T("Couldn't find omp's agent folder (omp config path did not answer), so models.yml can't be read here."), page.LoadError);
        Assert.False(page.CanChange);
        await vm.DisposeAsync();
    }
}

/// <summary>
/// A provider the page writes to models.yml, against the real omp (OMBRA_TEST_CONFIG naming the runtime pack): the
/// gate counts it as added (and allows its address under strict privacy), and omp lists the models it discovers
/// from a stub server. omp runs with its own agent folder in a temp directory (PI_CODING_AGENT_DIR, no profile).
/// </summary>
public sealed class RealCustomProviderTests
{
    [Fact]
    public async Task A_provider_written_by_the_page_is_added_and_its_models_are_offered()
    {
        if (TestProcesses.RealOmp() is not { } options)
        {
            Assert.Skip("OMBRA_TEST_CONFIG is not set");
            return;
        }
        if (ProviderGate.HelperSpec(options.ToLaunchSpec()) is null)
        {
            Assert.Skip("OMBRA_TEST_CONFIG does not start omp from a runtime pack (bun … cli.ts)");
            return;
        }
        using var stub = new StubModelServer { Models = ["stub-alpha", "stub-beta"] };
        var agent = TestProcesses.TempDir("real-custom-agent");
        var path = ModelsConfig.PathIn(agent);
        var text = ModelsConfig.Load(path).WithProvider(new ModelProviderEntry
        {
            Id = "my-stub", BaseUrl = stub.Root + "/v1", Api = ModelApis.ChatCompletions, ApiKey = "sk-stub",
            Discovery = CustomProvidersViewModel.ListDiscovery, InjectV1 = false,
        });
        text = ModelsConfig.Parse(path, text).WithProvider(new ModelProviderEntry
        {
            Id = "remote", BaseUrl = "https://llm.example.test/v1", Api = ModelApis.ChatCompletions, ApiKey = "sk-x", Models = ["far-model"],
        });
        ModelsConfig.Save(path, text);
        // Local servers as the page adds them (LocalModelServer.Entry), answered by stubs
        var local = LocalModelServer.Defaults(_ => null);
        using var ollamaStub = new StubModelServer { Models = ["llama-stub:latest"] };
        ModelsConfig.Save(path, ModelsConfig.Load(path).WithProvider(local.Single(l => l.Id == "ollama").Entry(ollamaStub.Root)));
        ModelsConfig.Save(path, ModelsConfig.Load(path).WithProvider(local.Single(l => l.Id == "vllm").Entry(stub.Root + "/v1")));

        OmpLaunchSpec Own(OmpLaunchSpec s)
        {
            var env = new Dictionary<string, string?>(s.Environment) { ["OMP_PROFILE"] = null, ["PI_CODING_AGENT_DIR"] = agent };
            return s with { Environment = env };
        }
        var gate = await ProviderGate.ComputeAsync(Own(options.ToLaunchSpec(new LaunchRequest(options.WorkingDirectory))));
        Assert.Contains("my-stub", gate.Added);
        Assert.Contains("remote", gate.Added);
        Assert.DoesNotContain("my-stub", gate.Disabled);
        Assert.Superset(new HashSet<string> { "my-stub", "remote", "ollama", "vllm" }, gate.Added.ToHashSet());
        Assert.Contains("lm-studio", gate.Disabled);
        Assert.Contains("llm.example.test", gate.Hosts["remote"].Api);

        var dir = TestProcesses.TempDir("real-custom");
        await using var privacy = new NetworkPrivacy(Path.Combine(dir, "network-privacy"));
        await using var s = new SessionController((r, ct) => privacy.ApplyAsync(Own(options.ToLaunchSpec(r)), options, ct),
            new LaunchRequest(options.WorkingDirectory), TimeSpan.FromSeconds(90));
        await s.StartAsync();
        var models = await s.GetModelsAsync();
        TestContext.Current.TestOutputHelper?.WriteLine("omp offers: " + string.Join(", ", models.Select(m => m.Provider + "/" + m.Id)));
        Assert.Contains(models, m => m.Provider == "my-stub" && m.Id == "stub-alpha");
        Assert.Contains(models, m => m.Provider == "my-stub" && m.Id == "stub-beta");
        Assert.Contains(models, m => m.Provider == "remote" && m.Id == "far-model");
        Assert.Contains(models, m => m.Provider == "vllm" && m.Id == "stub-alpha");
        Assert.Contains(models, m => m.Provider == "ollama" && m.Id == "llama-stub:latest");
        Assert.Contains(stub.Requests, r => r.Path == "/v1/models" && r.Authorization == "Bearer sk-stub");
        Assert.Contains("my-stub", privacy.LastGate!.Added);
    }
}
