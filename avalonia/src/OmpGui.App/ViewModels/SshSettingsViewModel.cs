using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// Settings → SSH hosts: the machines omp's ssh tool can run commands on. omp 18.2.0 keeps them in ssh.json — for all
/// projects in its agent folder (<c>omp config path</c>/ssh.json, per profile) and for one project in
/// &lt;project&gt;/.omp/ssh.json (project entries win). <c>/ssh list|add|remove</c> over RPC read and change them
/// (slash-commands/helpers/ssh.ts); while omp isn't running the page reads the files itself and only shows them.
/// </summary>
public sealed partial class SshSettingsViewModel : WorkspacePageViewModel
{
    private string? _agentDir;

    public SshSettingsViewModel(MainViewModel main) : base(main, "ssh") { }

    public ObservableCollection<SshHostRowViewModel> Hosts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    private string _loadError = "";

    public bool HasLoadError => LoadError.Length > 0;

    [ObservableProperty] private bool _isEmpty;

    /// <summary>omp isn't running (or can't list hosts): the list comes from the files and cannot be changed here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText), nameof(ReadOnlyNote))]
    private bool _readOnly;

    public string EmptyText => ReadOnly ? "No SSH hosts saved yet." : "No SSH hosts yet. Add one, and omp can run commands on it.";

    public string ReadOnlyNote => Main.CanRunOmpCommands
        ? "These are the saved hosts. This version of omp can't change them from the app; update omp to add or remove hosts here."
        : "omp isn't running: these are the saved hosts. Start omp to add or remove them.";

    [ObservableProperty] private string _userFile = "";
    [ObservableProperty] private string _projectFile = "";

    public string FilesLabel => "Saved in omp's ssh.json files. All projects: " + UserFile + (ProjectFile.Length > 0 ? ". This project: " + ProjectFile + "." : ".");

    partial void OnUserFileChanged(string value) => OnPropertyChanged(nameof(FilesLabel));
    partial void OnProjectFileChanged(string value) => OnPropertyChanged(nameof(FilesLabel));

    // ── Add form ──

    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private bool _adding;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newHost = "";
    [ObservableProperty] private string _newUser = "";
    [ObservableProperty] private string _newPort = "";
    [ObservableProperty] private string _newKey = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewScopeIsUser), nameof(NewScopeIsProject))]
    private string _newScope = "user";

    public bool NewScopeIsUser => NewScope == "user";
    public bool NewScopeIsProject => NewScope == "project";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormError))]
    private string _formError = "";

    public bool HasFormError => FormError.Length > 0;

    public bool CanChange => IsOmpRunning && !ReadOnly;

    partial void OnReadOnlyChanged(bool value) => OnPropertyChanged(nameof(CanChange));

    protected override void OnPhaseChanged() => OnPropertyChanged(nameof(CanChange));

    public override async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        LoadError = "";
        var ct = Main.Lifetime;
        try
        {
            if (_agentDir is null && await Main.RunOmpCliAsync(["config", "path"], TimeSpan.FromSeconds(30), ct) is { Ok: true } p && p.Stdout.Trim() is { Length: > 0 } dir)
                _agentDir = dir;
            var userPath = _agentDir is null ? null : Path.Combine(_agentDir, "ssh.json");
            var projectPath = Main.ProjectFolder is { } f ? Path.Combine(f, ".omp", "ssh.json") : null;
            UserFile = userPath is null ? "omp's agent folder/ssh.json" : GitProbe.Tilde(userPath);
            ProjectFile = projectPath is null ? "" : GitProbe.Tilde(projectPath);
            var files = await Task.Run(() => (User: ReadFile(userPath, "user"), Project: ReadFile(projectPath, "project")), ct);

            // What /ssh list would print: project entries first, a user entry of the same name is shadowed.
            List<SshHostEntry> FromFiles() => files.Project.Select(h => h.Entry)
                .Concat(files.User.Select(h => h.Entry).Where(u => files.Project.All(p => p.Entry.Name != u.Name))).ToList();
            IReadOnlyList<SshHostEntry> hosts;
            if (Main.CanRunOmpCommands)
            {
                var r = await Main.RunOmpCommandAsync("/ssh list", TimeSpan.FromSeconds(20), ct);
                // An omp without /ssh: the files still show what it would use, read-only.
                ReadOnly = r.Unsupported;
                var listed = r.Ok ? WorkspaceParsers.ParseSshList(r.Output) : null;
                if (listed is null && !r.Unsupported)
                    LoadError = r.Ok && r.Output.Length > 0 ? r.Output : "Couldn't read the SSH hosts from omp" + (r.Error is { Length: > 0 } why ? ": " + why + "." : ". Try again.");
                hosts = listed ?? (r.Unsupported ? FromFiles() : []);
                IsEmpty = listed is { Count: 0 } || r.Unsupported && hosts.Count == 0;
            }
            else
            {
                ReadOnly = true;
                hosts = FromFiles();
                IsEmpty = hosts.Count == 0;
            }
            Hosts.Clear();
            foreach (var h in hosts)
            {
                var key = (h.IsProject ? files.Project : files.User).FirstOrDefault(x => x.Entry.Name == h.Name).KeyPath;
                Hosts.Add(new SshHostRowViewModel(this, h, key));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { IsLoading = false; }
    }

    /// <summary>An ssh.json as omp reads it (discovery/ssh.ts): {hosts: {name: {host, username, port, keyPath|key}}}.</summary>
    internal static IReadOnlyList<(SshHostEntry Entry, string? KeyPath)> ReadFile(string? path, string scope)
    {
        if (path is null || !File.Exists(path)) return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("hosts", out var hosts) || hosts.ValueKind != JsonValueKind.Object) return [];
            var list = new List<(SshHostEntry, string?)>();
            foreach (var h in hosts.EnumerateObject())
            {
                if (h.Value.ValueKind != JsonValueKind.Object || !h.Value.TryGetProperty("host", out var host) || host.GetString() is not { Length: > 0 } address) continue;
                string? Str(string name) => h.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var port = 22;
                if (h.Value.TryGetProperty("port", out var pv))
                {
                    if (pv.ValueKind == JsonValueKind.Number && pv.TryGetInt32(out var n)) port = n;
                    else if (pv.ValueKind == JsonValueKind.String && int.TryParse(pv.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var ps)) port = ps;
                }
                list.Add((new SshHostEntry(h.Name, address, Str("username"), port, scope), Str("keyPath") ?? Str("key")));
            }
            return list;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];
        }
    }

    [RelayCommand]
    private void BeginAdd()
    {
        (NewName, NewHost, NewUser, NewPort, NewKey, NewScope, FormError) = ("", "", "", "", "", "user", "");
        IsAdding = true;
    }

    [RelayCommand]
    private void CancelAdd() => IsAdding = false;

    [RelayCommand]
    private void SetScope(string? scope)
    {
        if (scope is "user" or "project") NewScope = scope;
    }

    /// <summary>The <c>/ssh add</c> line for the form (omp's grammar), or the problem with the form.</summary>
    internal (string? Command, string? Problem) BuildAddCommand()
    {
        var name = NewName.Trim();
        var host = NewHost.Trim();
        var user = NewUser.Trim();
        var port = NewPort.Trim();
        var key = NewKey.Trim();
        if (name.Length == 0) return (null, "Give the host a name, such as build-box.");
        if (name.Length > 100 || !Regex.IsMatch(name, "^[a-zA-Z0-9_.-]+$")) return (null, "The name can only use letters, digits, - _ and . (no spaces).");
        if (host.Length == 0) return (null, "Enter the address: a host name or an IP address.");
        if (host.StartsWith('-') || user.StartsWith('-')) return (null, "The address and the user can't start with “-”.");
        if (port.Length > 0 && (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535))
            return (null, "The port is a number from 1 to 65535.");
        var parts = new List<string> { "/ssh add", name, "--host", WorkspaceParsers.QuoteArg(host) ?? "" };
        if (user.Length > 0) parts.AddRange(["--user", WorkspaceParsers.QuoteArg(user) ?? ""]);
        if (port.Length > 0) parts.AddRange(["--port", port]);
        if (key.Length > 0)
        {
            if (WorkspaceParsers.QuoteArg(key) is not { } quoted) return (null, "The key path can't hold both kinds of quotes.");
            parts.AddRange(["--key", quoted]);
        }
        parts.AddRange(["--scope", NewScope]);
        return (string.Join(' ', parts), null);
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var (command, problem) = BuildAddCommand();
        if (command is null) { FormError = problem ?? ""; return; }
        if (!Main.CanRunOmpCommands) { FormError = "omp isn't running: start it to add hosts."; return; }
        FormError = "";
        Adding = true;
        try
        {
            var r = await Main.RunOmpCommandAsync(command, TimeSpan.FromSeconds(20), Main.Lifetime);
            if (!r.Ok) { FormError = "omp did not add it: " + r.Error; return; }
            if (!r.Output.StartsWith("Added SSH host", StringComparison.Ordinal)) { FormError = r.Output.Length > 0 ? r.Output : "omp did not add it."; return; }
            IsAdding = false;
            Say($"Added {NewName.Trim()} ({(NewScope == "user" ? "all projects" : "this project")}). omp can use it right away.");
            await LoadAsync();
        }
        finally { Adding = false; }
    }

    internal async Task<string?> RemoveAsync(SshHostRowViewModel row)
    {
        if (!Main.CanRunOmpCommands) return "omp isn't running: start it to remove hosts.";
        var r = await Main.RunOmpCommandAsync($"/ssh remove {row.Name} --scope {row.Entry.Scope}", TimeSpan.FromSeconds(20), Main.Lifetime);
        if (!r.Ok) return "omp did not remove it: " + r.Error;
        if (!r.Output.StartsWith("Removed SSH host", StringComparison.Ordinal)) return r.Output.Length > 0 ? r.Output : "omp did not remove it.";
        Say($"Removed {row.Name}.");
        await LoadAsync();
        return null;
    }

    /// <summary>Time ssh gets for the TCP connection and, separately, for the server's SSH greeting.</summary>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// A harmless check, run only when the user presses Test: it connects the way omp's ssh tool does
    /// (<see cref="SshCheck.Arguments"/>), runs <c>exit</c>, and says stage by stage what ssh's log proves. The log
    /// itself (addresses, user, paths) is read here and dropped: neither shown whole nor stored.
    /// </summary>
    internal async Task<(bool Ok, string Text)> TestAsync(SshHostEntry host, string? keyPath)
    {
        // omp's own guard (ssh/utils.ts buildSshTarget): a destination starting with "-" would be read as an ssh option
        if (host.Host.StartsWith('-') || host.User?.StartsWith('-') == true)
            return (false, "omp refuses this host: an SSH address or user can't start with “-”.");
        if (keyPath is { Length: > 0 })
        {
            var key = ExpandHome(keyPath);
            if (!File.Exists(key)) return (false, $"SSH key not found: {keyPath}");
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(key);
                if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                    return (false, $"omp refuses this key: its permissions must be 600 or stricter (chmod 600 {keyPath}).");
            }
            keyPath = key;
        }
        var watch = Stopwatch.StartNew();
        var r = await Main.RunTool(new ToolCommand("ssh", SshCheck.Arguments(host, keyPath, ConnectTimeout), Main.ProjectFolder,
            SshCheck.ToolTimeout(ConnectTimeout)), Main.Lifetime);
        if (r.NotFound) return (false, "ssh was not found on this computer. omp needs the OpenSSH client too.");
        var d = SshCheck.Diagnose(r);
        var via = d.Route switch { SshRoute.ProxyJump => " through the jump host", SshRoute.ProxyCommand => " through the proxy command", _ => "" };
        // A local or cached connection takes milliseconds: "0.0 s" had read like no time at all
        if (d.Connected) return (true, watch.Elapsed.TotalSeconds < 1
            ? string.Create(CultureInfo.InvariantCulture, $"Connected{via} in {Math.Max(1, (int)watch.Elapsed.TotalMilliseconds)} ms.")
            : string.Create(CultureInfo.InvariantCulture, $"Connected{via} in {watch.Elapsed.TotalSeconds:0.0} s."));
        return (false, Describe(d, r.Message, r.ExitCode));
    }

    /// <summary>
    /// The test's answer: what failed, each stage with what the log proves about it, the route, the check that is
    /// left outside the app, ssh's own last line and the code of the outcome. Nothing beyond what ssh printed.
    /// </summary>
    internal static string Describe(SshDiagnosis d, string sshMessage, int exitCode)
    {
        var secs = ((int)ConnectTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        var proxy = d.Route == SshRoute.ProxyJump ? "jump host" : "proxy command";
        string Label(SshStage s) => s switch
        {
            SshStage.Resolve => "Address",
            SshStage.Tcp => "TCP connection",
            SshStage.Banner => "Server's SSH greeting",
            SshStage.Kex => "Protocol negotiation",
            SshStage.HostKey => "Host key check",
            _ => "Sign-in",
        };
        string Passed(SshStage s) => s switch
        {
            SshStage.Resolve => "resolved",
            SshStage.Tcp => "established",
            SshStage.Banner => "received",
            SshStage.Kex => "agreed",
            SshStage.HostKey => "passed",
            _ => "signed in",
        };
        var failed = d.Failure switch
        {
            SshFailure.ResolveFailed => "the name does not resolve on this computer",
            SshFailure.TcpRefused => "refused",
            SshFailure.TcpTimeout => $"no answer within {secs} s",
            SshFailure.TcpUnreachable => "no route to the address",
            SshFailure.BannerTimeout => $"not received within {secs} s",
            SshFailure.ClosedBeforeBanner => "the connection closed before it came",
            SshFailure.NotSsh => "the answer is not SSH",
            SshFailure.ProxyFailed => $"the {proxy} stopped before it came",
            SshFailure.KexNoMatch => "no algorithm both sides support",
            SshFailure.HostKeyChanged => "the key differs from the one in known_hosts",
            SshFailure.HostKeyRejected => "ssh rejected the key",
            SshFailure.KeyUnusable => "ssh could not use the key file",
            SshFailure.AuthDenied => "the server refused the keys offered",
            SshFailure.ConnectionClosed => "the connection closed",
            SshFailure.NoAnswer => "ssh stopped answering",
            _ => "failed",
        };
        var stage = d.FailedAt is { } at ? Label(at).ToLowerInvariant() : "";
        var text = new List<string>
        {
            d.Failure switch
            {
                SshFailure.ResolveFailed => "The server's name does not resolve on this computer.",
                SshFailure.TcpRefused => "The TCP connection was refused.",
                SshFailure.TcpTimeout => $"The TCP connection got no answer within {secs} s.",
                SshFailure.TcpUnreachable => "This computer has no route to the server's address.",
                SshFailure.TcpFailed => "The TCP connection failed.",
                SshFailure.BannerTimeout => d.Proxied
                    ? $"The server's SSH greeting did not come through the {proxy} within {secs} s."
                    : $"TCP connection established, but the server's SSH greeting did not arrive within {secs} s.",
                SshFailure.ClosedBeforeBanner => "TCP connection established, but it closed before the server's SSH greeting.",
                SshFailure.NotSsh => "Something answers on this port, but it does not speak SSH.",
                SshFailure.ProxyFailed => $"The {proxy} stopped before the server's SSH greeting came through.",
                SshFailure.KexNoMatch => "The server answers with SSH, but client and server share no algorithm.",
                SshFailure.HostKeyChanged => "The server's host key differs from the one known_hosts has for it.",
                SshFailure.HostKeyRejected => "ssh rejected the server's host key.",
                SshFailure.KeyUnusable => "ssh could not use the key file, so the sign-in failed.",
                SshFailure.AuthDenied => "The server answers, but refused the sign-in.",
                SshFailure.ConnectionClosed => $"The connection closed during the {stage}.",
                SshFailure.NoAnswer => d.FailedAt is null ? "ssh did not finish in time." : $"ssh did not finish in time; it stopped at the {stage}.",
                SshFailure.RemoteCommandFailed => $"Signed in, but the test command (exit) ended with code {exitCode}.",
                _ => "ssh could not connect.",
            },
        };
        if (d.FailedAt is { } failedAt)
        {
            foreach (var s in Enum.GetValues<SshStage>())
            {
                text.Add(d.Proxied && s is SshStage.Resolve or SshStage.Tcp ? $"– {Label(s)}: made by the {proxy}, not checked here"
                    : s < failedAt ? $"✓ {Label(s)}: {Passed(s)}"
                    : s == failedAt ? $"✗ {Label(s)}: {failed}"
                    : $"– {Label(s)}: not reached");
            }
        }
        if (d.Route switch
            {
                SshRoute.Direct => "Route: direct" + (d.Port is { } p ? string.Create(CultureInfo.InvariantCulture, $", port {p}.") : "."),
                SshRoute.ProxyJump => "Route: through a jump host (ProxyJump in your SSH config).",
                SshRoute.ProxyCommand => "Route: through a proxy command (ProxyCommand in your SSH config).",
                _ => null,
            } is { } route) text.Add(route);
        if (d.ProxyMessage is { Length: > 0 } said) text.Add($"The {proxy} said: {said}");
        if (d.Failure switch
            {
                SshFailure.BannerTimeout when d.Proxied => $"Why no greeting came is not visible from this computer. Still to check: from the {proxy}'s side, whether the server answers on its SSH port; whether sshd runs on the server (the hosting provider's console).",
                SshFailure.BannerTimeout => "Why the server stays silent is not visible from this computer. Still to check: whether sshd runs and logs this connection (the hosting provider's console), and the same test from another network.",
                SshFailure.ClosedBeforeBanner => "Who closed it is not visible from this computer. Still to check: sshd's log on the server for this connection (the hosting provider's console).",
                SshFailure.NotSsh => "Check the port: in this host's entry or in your SSH config.",
                SshFailure.TcpRefused => "Check the address and the port, and whether sshd runs on the server.",
                SshFailure.TcpTimeout or SshFailure.TcpUnreachable => "Check the address and the port; compare with the same test from another network.",
                SshFailure.ResolveFailed => "Check the spelling, or whether the name needs another DNS server or a VPN.",
                SshFailure.ProxyFailed => $"Check the {proxy} by itself first.",
                SshFailure.HostKeyChanged => "If the server was reinstalled, confirm its new key with whoever runs it before changing known_hosts. The app doesn't change known_hosts.",
                SshFailure.KeyUnusable => "omp signs in with keys only and can't type passphrases: check the key file's permissions (600) and format.",
                SshFailure.AuthDenied => "omp signs in with keys only (no passwords): put your public key on the server, or choose the key file.",
                _ => null,
            } is { } next) text.Add(next);
        text.Add(sshMessage.StartsWith("ssh", StringComparison.Ordinal) ? sshMessage : "ssh: " + sshMessage);
        text.Add("Code: " + d.Code);
        return string.Join('\n', text);
    }

    private static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "")
            : path;
}

/// <summary>One SSH host: test the connection, remove it (confirmed).</summary>
public sealed partial class SshHostRowViewModel(SshSettingsViewModel owner, SshHostEntry entry, string? keyPath) : ObservableObject
{
    public SshHostEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string? KeyPath { get; } = keyPath;
    public string Address => (Entry.User is { Length: > 0 } u ? u + "@" : "") + Entry.Host + (Entry.Port != 22 ? ":" + Entry.Port.ToString(CultureInfo.InvariantCulture) : "");
    public string Detail => KeyPath is { Length: > 0 } k ? $"{Address} · key {k}" : Address;
    public string ScopeLabel => Entry.IsProject ? "This project" : "All projects";
    public string RemoveQuestion => $"Remove {Name} from omp's hosts ({(Entry.IsProject ? "this project" : "all projects")})? Nothing changes on the machine itself.";

    [ObservableProperty] private bool _testing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    private string _testResult = "";

    [ObservableProperty] private bool _testOk;

    public bool HasTestResult => TestResult.Length > 0;

    [ObservableProperty] private bool _isConfirming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string _problem = "";

    public bool HasProblem => Problem.Length > 0;

    [RelayCommand]
    private async Task TestAsync()
    {
        Testing = true;
        TestResult = "";
        var (ok, text) = await owner.TestAsync(Entry, KeyPath);
        TestOk = ok;
        TestResult = text;
        Testing = false;
    }

    [RelayCommand]
    private void AskRemove()
    {
        Problem = "";
        IsConfirming = true;
    }

    [RelayCommand]
    private void CancelRemove() => IsConfirming = false;

    [RelayCommand]
    private async Task ConfirmRemoveAsync()
    {
        IsConfirming = false;
        Problem = await owner.RemoveAsync(this) ?? "";
    }
}
