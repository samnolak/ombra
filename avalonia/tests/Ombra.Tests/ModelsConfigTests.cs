using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>omp's models.yml as the providers page reads and edits it: other providers, settings and comments kept, a broken file refused.</summary>
public sealed class ModelsConfigTests
{
    private const string UserFile = """
        # my providers
        providers:
          # the work proxy
          work:
            baseUrl: https://llm.example.com/v1
            api: openai-completions
            apiKey: WORK_KEY
            headers:
              X-Team: core
            models:
              - id: big
                contextWindow: 200000

          # local box
          box:
            baseUrl: http://10.0.0.5:8000/v1
            api: openai-completions
            auth: none
            discovery:
              type: openai-models-list
        # trailing comment
        equivalence:
          overrides: {}
        """;

    private static ModelsConfig Parse(string text) => ModelsConfig.Parse("/tmp/models.yml", text);

    private static readonly ModelProviderEntry Mine = new()
    {
        Id = "my-server",
        BaseUrl = "http://127.0.0.1:9000/v1",
        Api = ModelApis.ChatCompletions,
        ApiKey = "sk-\"quoted\"",
        Discovery = "openai-models-list",
        InjectV1 = false,
    };

    [Fact]
    public void Entries_are_read_and_those_with_settings_beyond_the_form_are_not_editable()
    {
        var c = Parse(UserFile);
        Assert.Equal(["work", "box"], c.Providers.Select(p => p.Id));
        Assert.Contains("headers", c.Providers[0].NotEditable);
        Assert.Contains("model settings beyond the id", c.Providers[0].NotEditable);
        Assert.Equal(["big"], c.Providers[0].Models);
        var box = c.Providers[1];
        Assert.Null(box.NotEditable);
        Assert.Equal(("http://10.0.0.5:8000/v1", "openai-models-list", true), (box.BaseUrl, box.Discovery, box.NoAuth));
    }

    [Fact]
    public void Adding_keeps_every_other_provider_setting_and_comment()
    {
        var c = Parse(UserFile);
        var text = c.WithProvider(Mine);
        Assert.StartsWith(UserFile[..UserFile.IndexOf("# trailing comment", StringComparison.Ordinal)].TrimEnd('\n'), text, StringComparison.Ordinal);
        Assert.Contains("  my-server:\n    baseUrl: \"http://127.0.0.1:9000/v1\"\n", text);
        Assert.Contains("# trailing comment\nequivalence:\n  overrides: {}", text);
        var back = Parse(text);
        Assert.Equal(["work", "box", "my-server"], back.Providers.Select(p => p.Id));
        Assert.True(back.Providers[2].SameAs(Mine));
        Assert.Equal("sk-\"quoted\"", back.Providers[2].ApiKey);
        // The comment above "box" stays with it
        Assert.Contains("  # local box\n  box:", text);
    }

    [Fact]
    public void Replacing_changes_only_that_provider()
    {
        var c = Parse(UserFile);
        var edited = c.Providers[1] with { BaseUrl = "http://10.0.0.6:8000/v1", Models = ["a", "b"], Discovery = null, NoAuth = false, ApiKey = "KEY" };
        var text = c.WithProvider(edited);
        Assert.Equal(UserFile[..UserFile.IndexOf("  box:", StringComparison.Ordinal)], text[..UserFile.IndexOf("  box:", StringComparison.Ordinal)]);
        Assert.EndsWith("# trailing comment\nequivalence:\n  overrides: {}", text);
        var back = Parse(text);
        Assert.Equal(["work", "box"], back.Providers.Select(p => p.Id));
        Assert.True(back.Providers[1].SameAs(edited));
        Assert.DoesNotContain("10.0.0.5", text);
    }

    [Fact]
    public void Removing_takes_out_the_providers_lines_only()
    {
        var c = Parse(UserFile);
        var text = c.WithoutProvider("work");
        Assert.Equal(["box"], Parse(text).Providers.Select(p => p.Id));
        Assert.StartsWith("# my providers\nproviders:\n  # local box\n  box:", text);
        // The last provider: an empty mapping, the rest of the file kept
        var none = Parse(text).WithoutProvider("box");
        Assert.Equal("# my providers\nproviders: {}\n# trailing comment\nequivalence:\n  overrides: {}", none);
        Assert.Empty(Parse(none).Providers);
        // and it can take a provider again
        var again = Parse(none).WithProvider(Mine);
        Assert.Equal(["my-server"], Parse(again).Providers.Select(p => p.Id));
        Assert.Contains("equivalence:", again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# only a comment\n")]
    [InlineData("modelOverrides: {}")]
    [InlineData("providers:\n")]
    [InlineData("providers: null\nother: 1\n")]
    public void A_file_without_providers_gets_a_block(string text)
    {
        var result = Parse(text).WithProvider(Mine);
        var back = Parse(result);
        Assert.True(Assert.Single(back.Providers).SameAs(Mine));
        if (text.Contains("other")) Assert.Contains("other: 1", result);
        if (text.Contains("comment")) Assert.StartsWith("# only a comment\n", result);
    }

    [Fact]
    public void Windows_line_endings_are_kept()
    {
        var text = UserFile.Replace("\n", "\r\n");
        var result = Parse(text).WithProvider(Mine);
        Assert.DoesNotContain("\n", result.Replace("\r\n", ""));
        Assert.Equal(["work", "box", "my-server"], Parse(result).Providers.Select(p => p.Id));
        Assert.Equal(["box", "my-server"], Parse(Parse(result).WithoutProvider("work")).Providers.Select(p => p.Id));
    }

    [Fact]
    public void A_broken_file_is_refused_with_the_line()
    {
        var e = Assert.Throws<ModelsConfigException>(() => Parse("providers:\n  a:\n    baseUrl: [unclosed\n"));
        Assert.Contains("not valid YAML (line", e.Message);
        Assert.Throws<ModelsConfigException>(() => Parse("- just\n- a list\n"));
        Assert.Throws<ModelsConfigException>(() => Parse("providers: [1, 2]\n"));
    }

    [Fact]
    public void A_one_line_provider_list_is_not_rewritten()
    {
        var c = Parse("providers: {a: {baseUrl: 'http://x'}}\n");
        Assert.Equal("a", Assert.Single(c.Providers).Id);
        Assert.Throws<ModelsConfigException>(() => c.WithProvider(Mine));
        Assert.Throws<ModelsConfigException>(() => c.WithoutProvider("a"));
    }

    [Fact]
    public void Save_is_atomic_keeps_permissions_and_a_new_file_is_private()
    {
        var dir = TestProcesses.TempDir("models-config");
        var path = Path.Combine(dir, "agent", "models.yml");
        ModelsConfig.Save(path, "providers: {}\n");
        Assert.Equal("providers: {}\n", File.ReadAllText(path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }
        ModelsConfig.Save(path, Parse("providers: {}\n").WithProvider(Mine));
        Assert.Equal("my-server", Assert.Single(ModelsConfig.Load(path).Providers).Id);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(path));
        Assert.Equal(["models.yml"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact]
    public void The_file_omp_reads_is_models_yml_or_models_yaml_and_a_models_json_waits_for_omp()
    {
        var dir = TestProcesses.TempDir("models-path");
        Assert.Equal(Path.Combine(dir, "models.yml"), ModelsConfig.PathIn(dir));
        Assert.Empty(ModelsConfig.Load(ModelsConfig.PathIn(dir)).Providers);
        File.WriteAllText(Path.Combine(dir, "models.yaml"), "providers: {}\n");
        Assert.Equal(Path.Combine(dir, "models.yaml"), ModelsConfig.PathIn(dir));
        var other = TestProcesses.TempDir("models-json");
        File.WriteAllText(Path.Combine(other, "models.json"), "{}");
        Assert.Contains("Start omp once", Assert.Throws<ModelsConfigException>(() => ModelsConfig.Load(ModelsConfig.PathIn(other))).Message);
    }

    [Theory]
    [InlineData("My Server", "my-server")]
    [InlineData("  LLM @ work!! ", "llm-work")]
    [InlineData("123", "p-123")]
    [InlineData("Привет", "custom")]
    [InlineData("ollama", "ollama-2")]
    [InlineData("Taken", "taken-3")]
    public void Names_become_unique_provider_ids(string name, string id) =>
        Assert.Equal(id, ModelsConfig.Slug(name, ["ollama", "taken", "Taken-2"]));
}

/// <summary>What the local servers and a custom provider answer when asked for their models.</summary>
public sealed class ModelServerProbeTests
{
    [Fact]
    public void Ollama_tags_and_OpenAI_model_lists_are_read()
    {
        Assert.Equal(["llama3.2:latest", "qwen3:8b"], ModelServerProbe.ParseModels("""{"models":[{"name":"llama3.2:latest","model":"llama3.2:latest","size":1},{"model":"qwen3:8b"}]}"""));
        Assert.Equal(["gpt-x", "local"], ModelServerProbe.ParseModels("""{"object":"list","data":[{"id":"gpt-x","object":"model"},{"id":"local"},{"object":"model"}]}"""));
        Assert.Empty(ModelServerProbe.ParseModels("""{"data":[]}""")!);
        Assert.Null(ModelServerProbe.ParseModels("<html>Not found</html>"));
        Assert.Null(ModelServerProbe.ParseModels("""{"error":"nope"}"""));
        Assert.Null(ModelServerProbe.ParseModels("[1,2]"));
    }

    [Fact]
    public void Local_servers_use_omps_addresses_and_environment()
    {
        var none = LocalModelServer.Defaults(_ => null);
        Assert.Equal(["ollama", "lm-studio", "llama.cpp", "vllm"], none.Select(s => s.Id));
        Assert.Equal(["http://127.0.0.1:11434", "http://127.0.0.1:1234/v1", "http://127.0.0.1:8080", "http://127.0.0.1:8000/v1"], none.Select(s => s.BaseUrl));
        Assert.Equal(["http://127.0.0.1:11434/api/tags", "http://127.0.0.1:1234/v1/models", "http://127.0.0.1:8080/v1/models", "http://127.0.0.1:8000/v1/models"],
            none.Select(s => s.ModelsUrl(s.BaseUrl)));
        var env = new Dictionary<string, string> { ["OLLAMA_HOST"] = "0.0.0.0", ["LLAMA_CPP_BASE_URL"] = "http://gpu:9090" };
        var set = LocalModelServer.Defaults(k => env.GetValueOrDefault(k));
        Assert.Equal(("http://0.0.0.0:11434", "OLLAMA_HOST"), (set[0].BaseUrl, set[0].EnvironmentVariable));
        Assert.Equal(("http://gpu:9090", "LLAMA_CPP_BASE_URL"), (set[2].BaseUrl, set[2].EnvironmentVariable));
        Assert.Equal("http://127.0.0.1:7000", LocalModelServer.OllamaHost(":7000"));
        Assert.Equal("https://ollama.example.com", LocalModelServer.OllamaHost("https://ollama.example.com"));
        var entry = none[0].Entry(" http://127.0.0.1:11434 ");
        Assert.Equal(("ollama", "http://127.0.0.1:11434", ModelApis.Responses, true, "ollama"), (entry.Id, entry.BaseUrl, entry.Api, entry.NoAuth, entry.Discovery));
    }

    [Fact]
    public void Base_urls_are_normalized_as_omp_does()
    {
        Assert.Equal("http://h:1/v1", ModelServerProbe.WithV1("http://h:1"));
        Assert.Equal("http://h:1/v1", ModelServerProbe.WithV1("http://h:1/v1/"));
        Assert.Equal("https://h/api/v1", ModelServerProbe.WithV1("https://h/api?x=1"));
        Assert.Equal("https://h/v3/compat", ModelServerProbe.Bare("https://h/v3/compat/"));
        Assert.Equal("http://h:11434", ModelServerProbe.Root("http://h:11434/v1"));
    }

    [Fact]
    public async Task A_closed_port_is_reported_as_not_running()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var answer = await ModelServerProbe.ListAsync($"http://127.0.0.1:{port}/v1/models");
        Assert.False(answer.Running);
        Assert.False(answer.Ok);
        Assert.NotNull(answer.Problem);
    }
}
