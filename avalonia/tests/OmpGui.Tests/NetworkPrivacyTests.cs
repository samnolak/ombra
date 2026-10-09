using System.Net;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using System.Net.Sockets;
using System.Text;
using OmpGui.ClientCore;
using OmpGui.ClientCore.Network;
using OmpGui.Rpc;

namespace OmpGui.Tests;

/// <summary>
/// Settings › Advanced › Network privacy: every omp start turns off the providers the user has not added (a --config
/// overlay after the runtime's own arguments), a failed check stops the start, a custom omp command is started and said
/// to be ungated, and strict mode points every proxy variable at the app's filter, which lets through only the added
/// providers, the user's addresses and a provider being signed in to.
/// </summary>
public sealed class NetworkPrivacyTests
{
    private static readonly string[] Prefix = ["--config=/rt/omp/bunfig.toml", "--no-env-file", "--no-install", "/rt/omp/node_modules/@oh-my-pi/pi-coding-agent/src/cli.ts"];

    private static readonly OmpRuntimeOptions Runtime = new() { Command = "/rt/bun/bun", PrefixArgs = Prefix };

    private static ProviderGateResult Gate(params string[] added) => new(
        added,
        ["mine"],
        [.. new[] { "alibaba-coding-plan", "kilo", "mine", "ollama", "openai-codex", "anthropic" }.Except(added).Order()],
        new Dictionary<string, ProviderHosts>
        {
            ["anthropic"] = new(["api.anthropic.com"], ["api.anthropic.com", "claude.ai"]),
            ["openai-codex"] = new(["chatgpt.com"], ["auth.openai.com"]),
            ["alibaba-coding-plan"] = new(["coding-intl.dashscope.aliyuncs.com"], []),
        });

    private static NetworkPrivacy Privacy(string dir, ProviderGateResult gate, Func<string, string?>? inherited = null, IEnumerable<string>? names = null) =>
        new(Path.Combine(dir, "network-privacy"), inherited ?? (_ => null), () => names ?? [], (_, _) => Task.FromResult(gate));

    [Fact]
    public void The_check_runs_on_omps_runtime_in_its_package_folder_with_the_launchs_profile_and_project()
    {
        var options = Runtime with { Environment = new() { ["OMP_PROFILE"] = "work", ["ANTHROPIC_API_KEY"] = "k" } };
        var launch = options.ToLaunchSpec(new LaunchRequest("/projects/app"));
        // An omp --config after cli.ts (the overlay of an earlier start) is omp's, not Bun's
        var gated = ProviderGate.Apply(launch, "/settings/network-privacy/providers-x.yml", Prefix.Length);
        foreach (var spec in new[] { launch, gated })
        {
            var helper = ProviderGate.HelperSpec(spec)!;
            Assert.Equal("/rt/bun/bun", helper.FileName);
            Assert.Equal("/rt/omp", helper.WorkingDirectory); // omp's packages resolve from there
            Assert.Equal(["--config=/rt/omp/bunfig.toml", "--no-env-file", "-e", ProviderGate.Script], helper.Arguments);
            Assert.Equal("work", helper.Environment["OMP_PROFILE"]);
            Assert.Equal("k", helper.Environment["ANTHROPIC_API_KEY"]); // a key in the environment adds its provider
            Assert.Equal("/projects/app", helper.Environment[ProviderGate.CwdVariable]);
        }
        Assert.Null(ProviderGate.HelperSpec(new OmpRuntimeOptions { Command = "omp" }.ToLaunchSpec()));
    }

    [Fact]
    public async Task Providers_not_added_are_turned_off_with_an_overlay_after_the_runtime_arguments()
    {
        var dir = TestProcesses.TempDir("privacy-gate");
        await using var privacy = Privacy(dir, Gate("anthropic"));
        var project = Path.Combine(dir, "project");
        var spec = await privacy.ApplyAsync(Runtime.ToLaunchSpec(new LaunchRequest(project)), Runtime);

        // omp's options come after cli.ts: Bun would take a --config before it as its own bunfig
        Assert.Equal(Prefix, spec.Arguments.Take(Prefix.Length));
        var overlay = spec.Arguments[Prefix.Length];
        Assert.StartsWith("--config=", overlay);
        Assert.Equal(["--mode", "rpc-ui"], spec.Arguments.Skip(Prefix.Length + 1).Take(2));
        var listed = File.ReadAllLines(overlay["--config=".Length..]).Where(l => l.StartsWith("  - ")).Select(l => l[4..].Trim('"')).ToList();
        Assert.Equal(["alibaba-coding-plan", "kilo", "mine", "ollama", "openai-codex"], listed);
        Assert.Equal(["anthropic"], privacy.LastGate!.Added);
        Assert.Null(privacy.GateUnavailable);

        // The same project reuses its file; another project gets its own (its .omp/config.yml may differ)
        var again = await privacy.ApplyAsync(Runtime.ToLaunchSpec(new LaunchRequest(project)), Runtime);
        var other = await privacy.ApplyAsync(Runtime.ToLaunchSpec(new LaunchRequest(Path.Combine(dir, "other"))), Runtime);
        Assert.Equal(overlay, again.Arguments[Prefix.Length]);
        Assert.NotEqual(overlay, other.Arguments[Prefix.Length]);
    }

    [Fact]
    public async Task A_failed_check_stops_the_start()
    {
        var dir = TestProcesses.TempDir("privacy-fail");
        await using var privacy = new NetworkPrivacy(Path.Combine(dir, "network-privacy"), _ => null, () => [],
            (_, _) => throw new ProviderGateException("exit code 1"));
        var e = await Assert.ThrowsAsync<NetworkPrivacyException>(() => privacy.ApplyAsync(Runtime.ToLaunchSpec(), Runtime));
        Assert.Contains("exit code 1", e.Message);
        Assert.False(Directory.Exists(Path.Combine(dir, "network-privacy")));
    }

    [Fact]
    public async Task A_custom_omp_command_starts_unchanged_and_is_reported_as_not_gated()
    {
        var dir = TestProcesses.TempDir("privacy-custom");
        var computed = false;
        await using var privacy = new NetworkPrivacy(Path.Combine(dir, "network-privacy"), _ => null, () => [],
            (_, _) => { computed = true; return Task.FromResult(Gate()); });
        var options = new OmpRuntimeOptions { Command = "omp" };
        var before = options.ToLaunchSpec();
        var spec = await privacy.ApplyAsync(before, options);

        Assert.Equal(before.Arguments, spec.Arguments);
        Assert.False(computed);
        Assert.NotNull(privacy.GateUnavailable);
    }

    [Fact]
    public async Task Strict_mode_routes_every_proxy_variable_through_the_filter_and_drops_per_provider_proxies()
    {
        var dir = TestProcesses.TempDir("privacy-strict-env");
        var inherited = new Dictionary<string, string?> { ["PI_PROXY_ANTHROPIC"] = "http://corp:3128" };
        await using var privacy = Privacy(dir, Gate("anthropic"), k => inherited.GetValueOrDefault(k), inherited.Keys);

        var off = await privacy.ApplyAsync(Runtime.ToLaunchSpec(), Runtime);
        Assert.All(NetworkPrivacy.ProxyVariables, v => Assert.False(off.Environment.ContainsKey(v)));

        var strict = Runtime with
        {
            StrictNetworkPrivacy = true,
            Environment = new() { ["PI_PROXY_OPENAI"] = "http://other:8080" },
        };
        var spec = await privacy.ApplyAsync(strict.ToLaunchSpec(), strict);
        var address = spec.Environment["PI_PROXY"]!;
        var uri = new Uri(address);
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Contains(':', uri.UserInfo); // the filter takes only requests that carry its credentials
        Assert.All(NetworkPrivacy.ProxyVariables, v => Assert.Equal(address, spec.Environment[v]));
        Assert.Equal(NetworkPrivacy.LoopbackBypass, spec.Environment["NO_PROXY"]);
        Assert.Null(spec.Environment["PI_PROXY_ANTHROPIC"]);
        Assert.Null(spec.Environment["PI_PROXY_OPENAI"]);

        // CLI subcommands get the same filter, without a gate
        var cli = privacy.ApplyStrict(strict.ToCliLaunchSpec(["usage", "--json"]), strict);
        Assert.Equal(address, cli.Environment["HTTPS_PROXY"]);
    }

    [Fact]
    public async Task Strict_mode_allows_the_added_providers_the_users_addresses_and_a_provider_being_signed_in_to()
    {
        var dir = TestProcesses.TempDir("privacy-strict-allow");
        await using var privacy = Privacy(dir, Gate("anthropic"));
        var strict = Runtime with { StrictNetworkPrivacy = true, StrictNetworkAllowedHosts = ["pypi.org"] };
        await privacy.ApplyAsync(strict.ToLaunchSpec(), strict);

        Assert.Equal(["api.anthropic.com", "claude.ai", "pypi.org"], privacy.AllowedHosts);
        using (privacy.AllowSignIn("openai-codex"))
            Assert.Equal(["api.anthropic.com", "auth.openai.com", "chatgpt.com", "claude.ai", "pypi.org"], privacy.AllowedHosts);
        Assert.Equal(["api.anthropic.com", "claude.ai", "pypi.org"], privacy.AllowedHosts);

        // omp login: the user picks the provider in omp, so every known provider's addresses while it runs
        using (privacy.AllowSignIn(NetworkPrivacy.AnyProvider))
            Assert.Contains("coding-intl.dashscope.aliyuncs.com", privacy.AllowedHosts);
        Assert.DoesNotContain("coding-intl.dashscope.aliyuncs.com", privacy.AllowedHosts);

        privacy.SetUserHosts([]);
        Assert.Equal(["api.anthropic.com", "claude.ai"], privacy.AllowedHosts);
    }

    [Fact]
    public async Task The_filter_refuses_what_is_not_allowed_lists_it_and_tunnels_what_is()
    {
        var dir = TestProcesses.TempDir("privacy-strict-proxy");
        var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var port = ((IPEndPoint)echo.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var c = await echo.AcceptTcpClientAsync();
            var s = c.GetStream();
            var buf = new byte[4];
            await s.ReadExactlyAsync(buf);
            await s.WriteAsync(buf);
        });
        try
        {
            await using var privacy = Privacy(dir, Gate("anthropic"));
            var strict = Runtime with { StrictNetworkPrivacy = true, StrictNetworkAllowedHosts = ["127.0.0.1"] };
            var spec = await privacy.ApplyAsync(strict.ToLaunchSpec(), strict);
            var proxy = new Uri(spec.Environment["PI_PROXY"]!);
            var auth = "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(Uri.UnescapeDataString(proxy.UserInfo))) + "\r\n";

            var (refused, _) = await ConnectAsync(proxy, $"CONNECT coding-intl.dashscope.aliyuncs.com:443 HTTP/1.1\r\nHost: coding-intl.dashscope.aliyuncs.com:443\r\n{auth}\r\n");
            Assert.StartsWith("HTTP/1.1 403", refused);
            for (var i = 0; i < 100 && privacy.Blocked.Count == 0; i++) await Task.Delay(50);
            var blocked = Assert.Single(privacy.Blocked);
            Assert.Equal(("coding-intl.dashscope.aliyuncs.com", 443), (blocked.Host, blocked.Port));

            var (ok, stream) = await ConnectAsync(proxy, $"CONNECT 127.0.0.1:{port} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n{auth}\r\n");
            Assert.StartsWith("HTTP/1.1 200", ok);
            await stream.WriteAsync("ping"u8.ToArray());
            var back = new byte[4];
            await stream.ReadExactlyAsync(back).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("ping", Encoding.ASCII.GetString(back));
        }
        finally
        {
            echo.Stop();
        }
    }

    /// <summary>Sends a request head to the proxy and returns its status head and the open stream.</summary>
    internal static async Task<(string Head, NetworkStream Stream)> ConnectAsync(Uri proxy, string request)
    {
        var client = new TcpClient();
        await client.ConnectAsync(proxy.Host, proxy.Port);
        var s = client.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes(request));
        var head = new StringBuilder();
        var one = new byte[1];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await s.ReadAsync(one, timeout.Token) == 1)
            head.Append((char)one[0]);
        return (head.ToString(), s);
    }
}

/// <summary>
/// The gate's script against the real omp (OMPGUI_TEST_CONFIG naming the runtime pack): it imports omp 18.8.0's
/// modules and reads its catalog rules (UNDOCUMENTED / VERSION-PINNED), so a new omp that moves them fails here. The
/// started omp offers models only from the providers the gate found added.
/// </summary>
public sealed class RealProviderGateTests
{
    [Fact]
    public async Task The_real_runtime_reports_its_providers_and_omp_offers_only_the_added_ones()
    {
        if (TestProcesses.RealOmp() is not { } options)
        {
            Assert.Skip("OMPGUI_TEST_CONFIG is not set");
            return;
        }
        if (ProviderGate.HelperSpec(options.ToLaunchSpec()) is null)
        {
            Assert.Skip("OMPGUI_TEST_CONFIG does not start omp from a runtime pack (bun … cli.ts)");
            return;
        }
        var gate = await ProviderGate.ComputeAsync(options.ToLaunchSpec(new LaunchRequest(options.WorkingDirectory)));

        var known = gate.Added.Concat(gate.Disabled).ToHashSet();
        Assert.Superset(new HashSet<string> { "anthropic", "openai-codex", "alibaba-coding-plan", "kilo", "zenmux", "ollama", "llama.cpp", "lm-studio" }, known);
        Assert.Empty(gate.Added.Intersect(gate.Disabled.Except(gate.UserDisabled)));
        // Built-ins that fetch no model list stay usable: on-device inference, web search, Apple on-device
        Assert.Empty(new[] { "local", "web", "apple" }.Intersect(gate.Disabled.Except(gate.UserDisabled)));
        Assert.Contains("api.anthropic.com", gate.Hosts["anthropic"].Api);
        Assert.Contains("auth.openai.com", gate.Hosts["openai-codex"].SignIn);
        Assert.Contains("coding-intl.dashscope.aliyuncs.com", gate.Hosts["alibaba-coding-plan"].Api);
        Assert.DoesNotContain(gate.Hosts.Values.SelectMany(h => h.Api.Concat(h.SignIn)), h => h is "localhost" || h.StartsWith("127.", StringComparison.Ordinal));

        var dir = TestProcesses.TempDir("real-gate");
        await using var privacy = new NetworkPrivacy(Path.Combine(dir, "network-privacy"));
        await using var s = new SessionController((r, ct) => privacy.ApplyAsync(options.ToLaunchSpec(r), options, ct),
            new LaunchRequest(options.WorkingDirectory), TimeSpan.FromSeconds(90));
        await s.StartAsync();
        var models = await s.GetModelsAsync();
        Assert.NotEmpty(models);
        Assert.All(models, m => Assert.Contains(m.Provider, privacy.LastGate!.Added));
    }
}

/// <summary>Settings › Advanced › Network privacy in the window over the fake omp, launches built as the app does.</summary>
public sealed class NetworkPrivacyUiTests
{
    private static async Task Until(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Strict_mode_turns_on_from_the_page_and_a_refused_address_can_be_allowed()
    {
        var dir = TestProcesses.TempDir("privacy-ui");
        var store = new ClientSettingsStore(Path.Combine(dir, "settings", "omp-gui.local.json"));
        var log = Path.Combine(dir, "launches.jsonl");
        var fake = TestProcesses.Fake("normal");
        var gate = new ProviderGateResult(["anthropic"], [], ["kilo", "ollama"],
            new Dictionary<string, ProviderHosts> { ["anthropic"] = new(["api.anthropic.com"], ["claude.ai"]) });
        await using var privacy = new NetworkPrivacy(Path.Combine(dir, "settings", "network-privacy"), _ => null, () => [], (_, _) => Task.FromResult(gate));
        Task<OmpLaunchSpec> Launch(LaunchRequest r, CancellationToken ct)
        {
            var o = store.Load() with
            {
                Command = fake.FileName, PrefixArgs = [.. fake.Arguments], InheritedEnvironment = _ => null,
                Environment = new() { ["FAKE_OMP_LAUNCH_LOG"] = log, ["FAKE_SESSION_DIR"] = dir },
            };
            return privacy.ApplyAsync(o.ToLaunchSpec(r), o, ct);
        }
        var s = new SessionController(Launch, new LaunchRequest(TestProcesses.TempDir("privacy-ui-project"), ApprovalMode: "write"));
        var vm = new MainViewModel(s, new AppArgs(), settings: store) { Privacy = privacy };
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 1400 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        await vm.OpenSettingsAtAsync("advanced");
        var page = vm.PrivacySettings;
        Assert.False(page.IsStrict);
        Assert.True(w.FindControl<OmpGui.App.Views.Settings.NetworkPrivacySettings>("NetworkPrivacyPage")!.FindControl<Border>("PrivacyCard")!.IsEffectivelyVisible);
        // The fake omp is not a Bun runtime pack: the page says the providers could not be turned off
        await Until(() => page.HasGateWarning, "the ungated start reported");
        string? Proxy() => File.ReadAllLines(log).Select(l => JsonNode.Parse(l)!).Last()["env"]!["PI_PROXY"]?.GetValue<string>();
        Assert.Null(Proxy());

        var pid = vm.Session.ProcessId;
        await page.ToggleStrictCommand.ExecuteAsync(null);
        Assert.True(store.Load().StrictNetworkPrivacy);
        await Until(() => vm.Session.ProcessId != pid && vm.Phase == SessionPhase.Ready, "omp restarted behind the filter");
        var proxy = new Uri(Proxy()!);

        var auth = "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(Uri.UnescapeDataString(proxy.UserInfo))) + "\r\n";
        var (refused, _) = await NetworkPrivacyTests.ConnectAsync(proxy, $"CONNECT pypi.org:443 HTTP/1.1\r\nHost: pypi.org:443\r\n{auth}\r\n");
        Assert.StartsWith("HTTP/1.1 403", refused);
        await Until(() => page.Blocked.Count == 1, "the refused address listed");
        Shot(w, "privacy-strict-refused");

        page.AllowBlockedCommand.Execute(page.Blocked[0]);
        Assert.Equal(["pypi.org"], store.Load().StrictNetworkAllowedHosts!);
        Assert.Contains("pypi.org", privacy.AllowedHosts);
        Assert.Equal(["pypi.org"], page.UserHosts);
        Shot(w, "privacy-strict-allowed");
        await vm.DisposeAsync();
        w.Close();
    }

    private static void Shot(Window w, string name)
    {
        var shots = Environment.GetEnvironmentVariable("OMPGUI_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(shots);
        Dispatcher.UIThread.RunJobs();
        w.FindControl<Control>("NetworkPrivacyPage")!.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        using var frame = w.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(shots, name + ".png"));
        frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
