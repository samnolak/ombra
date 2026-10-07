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

    /// <summary>
    /// A harmless check that connects the way omp's ssh tool does (connection-manager.ts: BatchMode, new host keys
    /// accepted, the key file and port of the entry) and runs <c>exit</c>, with a short connect timeout.
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
        var args = new List<string> { "-n", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=8" };
        if (host.Port != 22) args.AddRange(["-p", host.Port.ToString(CultureInfo.InvariantCulture)]);
        if (keyPath is { Length: > 0 }) args.AddRange(["-i", keyPath]);
        args.Add(host.User is { Length: > 0 } u ? $"{u}@{host.Host}" : host.Host);
        args.Add("exit");
        var watch = Stopwatch.StartNew();
        var r = await Main.RunTool(new ToolCommand("ssh", args, Main.ProjectFolder, TimeSpan.FromSeconds(15)), Main.Lifetime);
        if (r.NotFound) return (false, "ssh was not found on this computer. omp needs the OpenSSH client too.");
        // A local or cached connection takes milliseconds: "0.0 s" had read like no time at all
        if (r.Ok) return (true, watch.Elapsed.TotalSeconds < 1
            ? string.Create(CultureInfo.InvariantCulture, $"Connected in {Math.Max(1, (int)watch.Elapsed.TotalMilliseconds)} ms.")
            : string.Create(CultureInfo.InvariantCulture, $"Connected in {watch.Elapsed.TotalSeconds:0.0} s."));
        var why = r.Message;
        var hint = why.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            ? " omp signs in with keys only (no passwords): put your public key on the server, or choose the key file."
            : why.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase) || why.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase)
                ? " The server's key doesn't match the one known for it (known_hosts)."
                : "";
        return (false, why + hint);
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
