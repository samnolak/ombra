using OmpGui.Rpc;

namespace OmpGui.ClientCore.Network;

/// <summary>Why omp was not started: the app could not make sure omp contacts only what the user allowed.</summary>
public sealed class NetworkPrivacyException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Settings › Advanced › Network privacy, applied to every omp the app starts.
/// <para>Always: <see cref="ProviderGate"/> turns off, for omp, every model provider the user has not added.</para>
/// <para>Strict network privacy (opt-in, <see cref="OmpRuntimeOptions.StrictNetworkPrivacy"/>): omp and the tools it
/// runs get the app's <see cref="PrivacyProxy"/> as their proxy, which lets through only the API and sign-in addresses of
/// the added providers, the addresses the user allowed, and — while the user signs in to a provider from the app — that
/// provider's addresses. Everything else is refused and listed in <see cref="Blocked"/>. omp sends all its requests
/// through <c>PI_PROXY</c> (provider streams, token refresh, usage, discovery, the model catalog), except loopback,
/// private-range and <c>NO_PROXY</c> targets, which it always reaches directly. Programs that ignore proxy variables
/// are not covered either.</para>
/// </summary>
public sealed class NetworkPrivacy : IAsyncDisposable
{
    /// <summary>How many refused destinations are kept (the newest; one entry per host).</summary>
    public const int MaxBlocked = 50;

    /// <summary>Loopback stays direct for every program (omp's own local servers, MCP servers, the app's bridges).</summary>
    public const string LoopbackBypass = "localhost,127.0.0.1,::1";

    /// <summary>For <see cref="AllowSignIn"/> when the user picks the provider in omp itself (<c>omp login</c>).</summary>
    public const string AnyProvider = "*";

    /// <summary>The variables strict mode points at the app's proxy (omp reads PI_PROXY; tools read the others).</summary>
    public static readonly IReadOnlyList<string> ProxyVariables =
        ["PI_PROXY", "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy"];

    private const string ProviderProxyPrefix = "PI_PROXY_";

    private readonly string _overlayDirectory;
    private readonly Func<string, string?> _inherited;
    private readonly Func<IEnumerable<string>> _inheritedNames;
    private readonly Func<OmpLaunchSpec, CancellationToken, Task<ProviderGateResult>> _computeGate;
    private readonly object _lock = new();
    // One gate result per (profile, project) launched in this run; strict mode allows the added providers of all of them
    private readonly Dictionary<string, ProviderGateResult> _gates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _signIns = new(StringComparer.Ordinal);
    private readonly List<BlockedConnection> _blocked = [];
    private IReadOnlyList<string> _userHosts = [];
    private volatile HostAllowlist _allowed = new([]);
    private PrivacyProxy? _proxy;

    /// <param name="overlayDirectory">Where the <c>--config</c> overlays go (the app's settings folder).</param>
    /// <param name="inheritedEnvironment">The environment omp inherits (null: this process's; tests replace it).</param>
    /// <param name="inheritedNames">The names in that environment (strict mode removes per-provider proxies).</param>
    /// <param name="computeGate">Runs the gate for a launch (null: <see cref="ProviderGate.ComputeAsync"/>; tests replace it).</param>
    public NetworkPrivacy(string overlayDirectory, Func<string, string?>? inheritedEnvironment = null, Func<IEnumerable<string>>? inheritedNames = null,
        Func<OmpLaunchSpec, CancellationToken, Task<ProviderGateResult>>? computeGate = null)
    {
        _overlayDirectory = overlayDirectory;
        _inherited = inheritedEnvironment ?? System.Environment.GetEnvironmentVariable;
        _inheritedNames = inheritedNames ?? (() => System.Environment.GetEnvironmentVariables().Keys.Cast<object>().Select(k => k.ToString()!));
        _computeGate = computeGate ?? ProviderGate.ComputeAsync;
    }

    /// <summary>The app's folder for the <c>--config</c> overlays, next to its settings file.</summary>
    public static string DirectoryFor(string settingsPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "network-privacy");

    /// <summary>The gate, the allowed addresses, or the refused list changed (raised on any thread).</summary>
    public event Action? Changed;

    /// <summary>The newest gate result (null before the first omp start, or when the gate cannot run).</summary>
    public ProviderGateResult? LastGate { get; private set; }

    /// <summary>
    /// Set when the newest omp start could not be gated because omp is started with the user's own command (Settings ›
    /// Advanced › omp runtime), where there are no omp modules to ask; null otherwise.
    /// </summary>
    public string? GateUnavailable { get; private set; }

    /// <summary>Destinations strict mode refused, newest first.</summary>
    public IReadOnlyList<BlockedConnection> Blocked
    {
        get { lock (_lock) return [.. _blocked]; }
    }

    /// <summary>The addresses strict mode lets through now.</summary>
    public IReadOnlyList<string> AllowedHosts => _allowed.Entries;

    /// <summary>
    /// A session's or terminal's omp launch with the gate and, when on, strict mode applied. <paramref name="options"/>
    /// are the settings the launch was built from (runtime applied), for the prefix and the strict-mode settings.
    /// </summary>
    /// <exception cref="NetworkPrivacyException">omp must not start: the gate or the strict-mode filter failed.</exception>
    public async Task<OmpLaunchSpec> ApplyAsync(OmpLaunchSpec spec, OmpRuntimeOptions options, CancellationToken ct = default)
    {
        if (ProviderGate.HelperSpec(spec) is null)
        {
            lock (_lock)
                GateUnavailable = "omp is started with your own command (Settings › Advanced › omp runtime), so the app can't turn off the providers you haven't added: omp may contact all of them.";
        }
        else
        {
            ProviderGateResult result;
            try { result = await _computeGate(spec, ct).ConfigureAwait(false); }
            catch (ProviderGateException e)
            {
                throw new NetworkPrivacyException(
                    "the app could not check which model providers you have added (" + e.Message.TrimEnd('.')
                    + "). Without that check omp would contact every provider it knows, so it was not started.", e);
            }
            var key = (spec.Environment.GetValueOrDefault("OMP_PROFILE") ?? _inherited("OMP_PROFILE") ?? "") + "\n" + (spec.WorkingDirectory ?? "");
            string overlay;
            try { overlay = ProviderGate.WriteOverlay(result, _overlayDirectory, key); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new NetworkPrivacyException("the list of providers omp must not contact could not be saved (" + e.Message + "), so omp was not started.", e);
            }
            spec = ProviderGate.Apply(spec, overlay, options.PrefixArgs.Length);
            lock (_lock)
            {
                _gates[key] = result;
                LastGate = result;
                GateUnavailable = null;
                Recompose();
            }
        }
        Changed?.Invoke();
        return ApplyStrict(spec, options);
    }

    /// <summary>
    /// Strict mode only (CLI subcommands and checks: they need no gate): <paramref name="spec"/> with every proxy variable
    /// pointing at the app's filter, loopback direct, and per-provider proxies removed. Unchanged when strict mode is off.
    /// </summary>
    /// <exception cref="NetworkPrivacyException">The filter could not start.</exception>
    public OmpLaunchSpec ApplyStrict(OmpLaunchSpec spec, OmpRuntimeOptions options)
    {
        if (options.StrictNetworkPrivacy != true) return spec;
        SetUserHosts(options.StrictNetworkAllowedHosts ?? []);
        var proxy = EnsureProxy(spec);
        var env = new Dictionary<string, string?>(spec.Environment);
        var address = proxy.Address.ToString().TrimEnd('/');
        foreach (var name in ProxyVariables) env[name] = address;
        env["NO_PROXY"] = LoopbackBypass;
        env["no_proxy"] = LoopbackBypass;
        // Node's fetch reads the proxy variables only with this (Bun's always does)
        env["NODE_USE_ENV_PROXY"] = "1";
        // A per-provider proxy would win over PI_PROXY and skip the filter
        foreach (var name in _inheritedNames().Concat(spec.Environment.Keys).Where(n => n.StartsWith(ProviderProxyPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
            env[name] = null;
        return spec with { Environment = env };
    }

    /// <summary>The user's own allowed addresses (Settings › Advanced › Network privacy); applies to the next connection.</summary>
    public void SetUserHosts(IEnumerable<string> hosts)
    {
        lock (_lock)
        {
            var list = hosts.Where(HostAllowlist.IsValidEntry).ToList();
            if (list.SequenceEqual(_userHosts, StringComparer.OrdinalIgnoreCase)) return;
            _userHosts = list;
            Recompose();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// While the user signs in to <paramref name="providerId"/> from the app, strict mode also lets through that
    /// provider's API and sign-in addresses (it is not added yet). Dispose the result when the sign-in ends.
    /// </summary>
    public IDisposable AllowSignIn(string providerId)
    {
        lock (_lock)
        {
            _signIns[providerId] = _signIns.GetValueOrDefault(providerId) + 1;
            Recompose();
        }
        Changed?.Invoke();
        return new SignInGrant(this, providerId);
    }

    /// <summary>The added providers' addresses from the newest gate, by provider.</summary>
    public IReadOnlyList<KeyValuePair<string, ProviderHosts>> AddedProviderHosts()
    {
        lock (_lock)
        {
            if (LastGate is not { } gate) return [];
            return [.. gate.Added.Select(p => new KeyValuePair<string, ProviderHosts>(p, gate.Hosts.GetValueOrDefault(p) ?? new ProviderHosts([], [])))];
        }
    }

    public async ValueTask DisposeAsync()
    {
        PrivacyProxy? proxy;
        lock (_lock) (proxy, _proxy) = (_proxy, null);
        if (proxy is not null) await proxy.DisposeAsync().ConfigureAwait(false);
    }

    private PrivacyProxy EnsureProxy(OmpLaunchSpec spec)
    {
        lock (_lock)
        {
            if (_proxy is not null) return _proxy;
            // The user's own proxy (a corporate one), as omp would have used it: the launch's settings, else inherited
            var upstream = UpstreamProxy.FromEnvironment(name => spec.Environment.TryGetValue(name, out var v) ? v : _inherited(name));
            try { _proxy = PrivacyProxy.Start((host, port) => _allowed.Matches(host), upstream); }
            catch (System.Net.Sockets.SocketException e)
            {
                throw new NetworkPrivacyException("strict network privacy is on and its filter could not start (" + e.Message + "), so omp was not started.", e);
            }
            _proxy.Blocked += OnBlocked;
            return _proxy;
        }
    }

    private void OnBlocked(BlockedConnection b)
    {
        lock (_lock)
        {
            _blocked.RemoveAll(x => x.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase));
            _blocked.Insert(0, b);
            if (_blocked.Count > MaxBlocked) _blocked.RemoveRange(MaxBlocked, _blocked.Count - MaxBlocked);
        }
        Changed?.Invoke();
    }

    private void EndSignIn(string providerId)
    {
        lock (_lock)
        {
            var n = _signIns.GetValueOrDefault(providerId) - 1;
            if (n > 0) _signIns[providerId] = n;
            else _signIns.Remove(providerId);
            Recompose();
        }
        Changed?.Invoke();
    }

    // Under _lock.
    private void Recompose()
    {
        var hosts = new HashSet<string>(_userHosts, StringComparer.OrdinalIgnoreCase);
        foreach (var gate in _gates.Values)
        {
            var providers = _signIns.ContainsKey(AnyProvider) ? gate.Hosts.Keys : gate.Added.Concat(_signIns.Keys);
            foreach (var p in providers)
            {
                if (!gate.Hosts.TryGetValue(p, out var h)) continue;
                hosts.UnionWith(h.Api);
                hosts.UnionWith(h.SignIn);
            }
        }
        _allowed = new HostAllowlist(hosts.Where(HostAllowlist.IsValidEntry).Order(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class SignInGrant(NetworkPrivacy owner, string providerId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.EndSignIn(providerId);
        }
    }
}
