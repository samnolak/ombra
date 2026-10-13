using OmpGui.App.Localization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// Settings → Model providers → Local and custom: model servers on this computer (Ollama, LM Studio, llama.cpp, vLLM)
/// and providers of the user's own (an OpenAI- or Anthropic-compatible address). omp 18.8.0 reads them from models.yml
/// in its agent folder (<c>omp config path</c>) when it starts; network privacy counts a provider named there as added
/// (ProviderGate), so a local server is used only once it is added here. Each change is written to models.yml
/// (<see cref="ModelsConfig"/>: the user's other entries and comments kept, a file that doesn't parse left alone) and
/// every chat's omp restarts to read it. API keys typed here go into models.yml, omp's own configuration, and nowhere else.
/// </summary>
public sealed partial class CustomProvidersViewModel : WorkspacePageViewModel
{
    public const string PageKey = "providers";

    /// <summary>The discovery type of a custom provider whose models come from the server.</summary>
    public const string ListDiscovery = "openai-models-list";

    private string? _path;

    public CustomProvidersViewModel(MainViewModel main) : base(main, PageKey) { }

    internal CancellationToken MainLifetime => Main.Lifetime;

    /// <summary>The local servers to show (tests point them at their own ports).</summary>
    internal IReadOnlyList<LocalModelServer>? Servers { get; set; }

    public ObservableCollection<LocalServerRowViewModel> LocalServers { get; } = [];
    public ObservableCollection<CustomProviderRowViewModel> CustomProviders { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError), nameof(CanChange))]
    private string _loadError = "";

    public bool HasLoadError => LoadError.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChange))]
    private bool _busy;

    /// <summary>models.yml can be changed: its folder is known and the file reads.</summary>
    public bool CanChange => _path is not null && !HasLoadError && !Busy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileLabel))]
    private string _configFile = "";

    public string FileLabel => ConfigFile.Length == 0 ? "" : Loc.F("Saved in omp's {0}: {1}", System.IO.Path.GetFileName(_path ?? ModelsConfig.FileName), ConfigFile);

    public bool HasCustomProviders => CustomProviders.Count > 0;

    private Task? _loading;
    private bool _loadAgain;

    /// <summary>
    /// Reads models.yml and checks the local servers. Asked while a read runs (omp restarted, a change was saved), it
    /// reads again once that one ends, and the task returned ends after that: what is shown is never older than the file.
    /// </summary>
    public override Task LoadAsync()
    {
        if (_loading is { IsCompleted: false })
        {
            _loadAgain = true;
            return _loading;
        }
        return _loading = LoadLoopAsync();
    }

    private async Task LoadLoopAsync()
    {
        IsLoading = true;
        try
        {
            do
            {
                _loadAgain = false;
                await LoadOnceAsync();
            } while (_loadAgain);
        }
        finally { IsLoading = false; }
    }

    private async Task LoadOnceAsync()
    {
        var ct = Main.Lifetime;
        try
        {
            // Asked each time: a profile saved under Settings → Advanced moves omp's agent folder
            var agentDir = await Main.RunOmpCliAsync(["config", "path"], TimeSpan.FromSeconds(30), ct) is { Ok: true } p && p.Stdout.Trim() is { Length: > 0 } dir ? dir : null;
            Servers ??= LocalModelServer.Defaults(EnvironmentOfOmp());
            ModelsConfig? config = null;
            if (agentDir is null)
            {
                _path = null;
                LoadError = Loc.T("Couldn't find omp's agent folder (omp config path did not answer), so models.yml can't be read here.");
            }
            else
            {
                _path = ModelsConfig.PathIn(agentDir);
                ConfigFile = GitProbe.Tilde(_path);
                try
                {
                    var path = _path;
                    config = await Task.Run(() => ModelsConfig.Load(path), ct);
                    LoadError = "";
                }
                catch (ModelsConfigException e)
                {
                    LoadError = e.Message + Loc.F(" Fix it in {0}; the app doesn't change a file it can't read.", ConfigFile);
                }
            }
            OnPropertyChanged(nameof(CanChange));
            var entries = config?.Providers ?? [];
            LocalServers.Clear();
            foreach (var s in Servers)
                LocalServers.Add(new LocalServerRowViewModel(this, s, entries.FirstOrDefault(e => e.Id == s.Id)));
            CustomProviders.Clear();
            foreach (var e in entries.Where(e => Servers.All(s => s.Id != e.Id)))
                CustomProviders.Add(new CustomProviderRowViewModel(this, e));
            OnPropertyChanged(nameof(HasCustomProviders));
            foreach (var row in LocalServers) _ = row.CheckAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>The environment omp starts with: the app's, with Settings → Advanced's variables over it.</summary>
    private Func<string, string?> EnvironmentOfOmp()
    {
        var own = Main.RuntimeEnvironment();
        return name => own.TryGetValue(name, out var v) ? v : Environment.GetEnvironmentVariable(name);
    }

    /// <summary>
    /// Re-reads models.yml, applies <paramref name="edit"/> and writes it atomically; then every chat's omp restarts to
    /// read it. Null when done, else why nothing was written.
    /// </summary>
    internal async Task<string?> ChangeAsync(Func<ModelsConfig, string> edit, string done)
    {
        if (_path is not { } path) return Loc.T("omp's agent folder is not known, so models.yml can't be changed.");
        Busy = true;
        try
        {
            try { await Task.Run(() => ModelsConfig.Save(path, edit(ModelsConfig.Load(path))), Main.Lifetime); }
            catch (ModelsConfigException e) { return e.Message; }
            await LoadAsync();
            Say(done + Loc.T(" Restarting omp…"));
            var now = await Main.RestartOmpToApplyAsync();
            Say(now
                ? done + Loc.T(" omp restarted with it.")
                : done + Loc.T(" omp is replying: it restarts with the change when the reply ends."));
            return null;
        }
        catch (OperationCanceledException) when (Main.Lifetime.IsCancellationRequested) { return null; }
        finally { Busy = false; }
    }

    internal Task<string?> RemoveAsync(string id, string name) =>
        ChangeAsync(c => c.WithoutProvider(id), Loc.F("Removed {0} from models.yml.", name));

    // ── Add or edit a custom provider ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormTitle), nameof(IsEditing), nameof(IsNew))]
    private string? _editingId;

    public bool IsEditing => EditingId is not null;
    public bool IsNew => EditingId is null;
    public string FormTitle => EditingId is { } id ? Loc.F("Edit {0}", id) : Loc.T("Add a custom provider");

    [ObservableProperty] private bool _isFormOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormIdNote))]
    private string _formName = "";

    [ObservableProperty] private string _formBaseUrl = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormApiIsChat), nameof(FormApiIsResponses), nameof(FormApiIsAnthropic), nameof(BaseUrlHint))]
    private string _formApi = ModelApis.ChatCompletions;

    public bool FormApiIsChat => FormApi == ModelApis.ChatCompletions;
    public bool FormApiIsResponses => FormApi == ModelApis.Responses;
    public bool FormApiIsAnthropic => FormApi == ModelApis.AnthropicMessages;

    public string BaseUrlHint => FormApiIsAnthropic
        ? Loc.T("The address before /v1, as https://api.anthropic.com")
        : Loc.T("Usually ends in /v1, as http://192.168.1.20:8000/v1");

    [ObservableProperty] private string _formApiKey = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormListsModels))]
    private bool _formDiscover = true;

    public bool FormListsModels => !FormDiscover;

    /// <summary>The model ids, one per line (or separated by commas).</summary>
    [ObservableProperty] private string _formModels = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormError))]
    private string _formError = "";

    public bool HasFormError => FormError.Length > 0;

    // A problem the user is fixing stops being shown as they type
    partial void OnFormNameChanged(string value) => FormError = "";
    partial void OnFormBaseUrlChanged(string value) => FormError = "";
    partial void OnFormModelsChanged(string value) => FormError = "";

    [ObservableProperty] private bool _testing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    private string _testResult = "";

    [ObservableProperty] private bool _testOk;

    public bool HasTestResult => TestResult.Length > 0;

    /// <summary>The id omp will know the provider by (models are picked as id/model).</summary>
    public string FormIdNote => IsEditing || FormName.Trim().Length == 0 ? "" : Loc.F("omp will call it {0}.", NewId());

    private string NewId()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        taken.UnionWith(CustomProviders.Select(p => p.Id));
        taken.UnionWith((Servers ?? []).Select(s => s.Id));
        taken.UnionWith(Main.LoginProviders.Select(p => p.Id));
        if (Main.Privacy?.LastGate is { } gate) taken.UnionWith(gate.Added.Concat(gate.Disabled));
        return ModelsConfig.Slug(FormName, taken);
    }

    [RelayCommand]
    private void BeginAdd()
    {
        EditingId = null;
        (FormName, FormBaseUrl, FormApi, FormApiKey, FormDiscover, FormModels) = ("", "", ModelApis.ChatCompletions, "", true, "");
        (FormError, TestResult) = ("", "");
        IsFormOpen = true;
    }

    internal void BeginEdit(ModelProviderEntry e)
    {
        EditingId = e.Id;
        (FormName, FormBaseUrl, FormApi, FormApiKey) = (e.Id, e.BaseUrl, e.Api ?? ModelApis.ChatCompletions, e.ApiKey ?? "");
        (FormDiscover, FormModels) = (e.Discovery is not null, string.Join('\n', e.Models));
        (FormError, TestResult) = ("", "");
        IsFormOpen = true;
    }

    [RelayCommand]
    private void CancelForm() => IsFormOpen = false;

    [RelayCommand]
    private void SetApi(string? api)
    {
        if (api is not null && ModelApis.All.Contains(api)) FormApi = api;
    }

    [RelayCommand]
    private void SetDiscover(string? on) => FormDiscover = on == "true";

    private IReadOnlyList<string> ModelIds() =>
        FormModels.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The models.yml entry for the form, or what is missing.</summary>
    internal (ModelProviderEntry? Entry, string? Problem) BuildEntry()
    {
        if (IsNew && FormName.Trim().Length == 0) return (null, Loc.T("Give the provider a name, such as My server."));
        var url = FormBaseUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.Host.Length == 0)
            return (null, Loc.T("Enter the address as a URL starting with http:// or https://."));
        var models = ModelIds();
        if (!FormDiscover && models.Count == 0) return (null, Loc.T("List at least one model id, or let omp discover them from the server."));
        var key = FormApiKey.Trim();
        return (new ModelProviderEntry
        {
            Id = EditingId ?? NewId(),
            BaseUrl = url,
            Api = FormApi,
            ApiKey = key.Length > 0 ? key : null,
            NoAuth = key.Length == 0,
            Discovery = FormDiscover ? ListDiscovery : null,
            // omp asks <baseUrl>/models as written (the chat requests go to the same base); Anthropic's base has no /v1
            InjectV1 = FormDiscover && !FormApiIsAnthropic ? false : null,
            Models = FormDiscover ? [] : models,
        }, null);
    }

    [RelayCommand]
    private async Task SaveFormAsync()
    {
        var (entry, problem) = BuildEntry();
        if (entry is null) { FormError = problem ?? ""; return; }
        FormError = "";
                var error = await ChangeAsync(c => c.WithProvider(entry), IsEditing ? Loc.F("Saved {0}.", entry.Id) : Loc.F("Added {0}.", entry.Id));
        if (error is not null) { FormError = error; return; }
        IsFormOpen = false;
    }

    /// <summary>
    /// Asks the server for its models as omp will (the key as a bearer token; Anthropic's headers for Anthropic
    /// Messages), from the app, before anything is saved. The key is sent only to the address typed in the form.
    /// </summary>
    [RelayCommand]
    private async Task TestAsync()
    {
        var url = FormBaseUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
        {
            (TestOk, TestResult) = (false, Loc.T("Enter the address as a URL starting with http:// or https://."));
            return;
        }
        Testing = true;
        TestResult = "";
        try
        {
            var headers = new Dictionary<string, string>();
            var note = "";
            var key = FormApiKey.Trim();
            if (key.StartsWith('!')) note = Loc.T(" The key comes from a command, which omp runs when it starts: the test went without it.");
            else if (key.Length > 0)
            {
                // omp's resolveConfigValue: an environment variable of that name, else the text itself
                var value = EnvironmentOfOmp()(key) is { Length: > 0 } env ? env : key;
                headers["Authorization"] = "Bearer " + value;
                if (FormApiIsAnthropic) headers["x-api-key"] = value;
            }
            if (FormApiIsAnthropic) headers["anthropic-version"] = "2023-06-01";
            var modelsUrl = (FormApiIsAnthropic ? ModelServerProbe.WithV1(url) : ModelServerProbe.Bare(url)) + "/models";
            var answer = await ModelServerProbe.ListAsync(modelsUrl, headers, ct: Main.Lifetime);
            if (!answer.Ok && answer.Status == 404 && !FormApiIsAnthropic && ModelServerProbe.Bare(url) != ModelServerProbe.WithV1(url))
            {
                var v1 = await ModelServerProbe.ListAsync(ModelServerProbe.WithV1(url) + "/models", headers, ct: Main.Lifetime);
                if (v1.Ok)
                {
                    (TestOk, TestResult) = (false, Loc.F("Nothing at {0}, but {1} answers with {2}. Use {1} as the address.", modelsUrl, ModelServerProbe.WithV1(url), Count(v1.Models!.Count)));
                    return;
                }
            }
            if (answer.Models is { } models)
            {
                var shown = string.Join(", ", models.Take(6)) + (models.Count > 6 ? ", …" : "");
                var filled = "";
                if (!FormDiscover && ModelIds().Count == 0 && models.Count > 0)
                {
                    FormModels = string.Join('\n', models);
                    filled = Loc.T(" They are in the model list below.");
                }
                (TestOk, TestResult) = (true, models.Count == 0
                    ? Loc.T("Connected, but the server lists no models.") + note
                    : Loc.F("Connected: {0} ({1}).", Count(models.Count), shown) + filled + note);
            }
            else (TestOk, TestResult) = (false, (answer.Problem ?? Loc.T("No answer.")) + note);
        }
        catch (OperationCanceledException) when (Main.Lifetime.IsCancellationRequested) { }
        finally { Testing = false; }
    }

    internal static string Count(int n) => n == 1 ? Loc.T("1 model") : Loc.F("{0} models", n);
}

/// <summary>Ollama, LM Studio, llama.cpp or vLLM: whether it runs on this computer, and whether omp uses it.</summary>
public sealed partial class LocalServerRowViewModel : ObservableObject
{
    private readonly CustomProvidersViewModel _owner;

    public LocalServerRowViewModel(CustomProvidersViewModel owner, LocalModelServer server, ModelProviderEntry? entry)
    {
        _owner = owner;
        Server = server;
        Entry = entry;
        _url = entry?.BaseUrl is { Length: > 0 } added ? added : server.BaseUrl;
    }

    public LocalModelServer Server { get; }
    public ModelProviderEntry? Entry { get; }
    public string Id => Server.Id;
    public string Name => Server.Name;

    /// <summary>omp uses it: models.yml names it.</summary>
    public bool IsAdded => Entry is not null;
    public bool IsNotAdded => Entry is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private string _url;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private string _status = Loc.T("Checking…");

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _checking;

    public string Detail => Url + (Entry is null && Server.EnvironmentVariable is { } v && Url == Server.BaseUrl ? Loc.F(" (from {0})", v) : "") + " · " + Status;

    /// <summary>The address panel of Add (the server may listen elsewhere).</summary>
    [ObservableProperty] private bool _isAddOpen;

    [ObservableProperty] private bool _isConfirmingRemove;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string _problem = "";

    public bool HasProblem => Problem.Length > 0;

    public string RemoveQuestion => Loc.F("Remove {0} from omp's models.yml? omp stops using it; the server itself is not touched.", Name);

    /// <summary>Asks the server for its models (a short wait: it is on this computer or the local network).</summary>
    [RelayCommand]
    internal async Task CheckAsync()
    {
        Checking = true;
        Status = Loc.T("Checking…");
        try
        {
            var answer = await ModelServerProbe.ListAsync(Server.ModelsUrl(Url), ct: _owner.MainLifetime);
            IsRunning = answer.Ok;
            Status = answer.Models is { } m ? Loc.T("Running · ") + CustomProvidersViewModel.Count(m.Count)
                : answer.Running ? Loc.T("Answers, but ") + LowerFirst(answer.Problem ?? Loc.T("lists no models"))
                : Loc.T("Not running");
        }
        catch (OperationCanceledException) { }
        finally { Checking = false; }
    }

    private static string LowerFirst(string s) => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    [RelayCommand]
    private void BeginAdd()
    {
        Problem = "";
        IsAddOpen = true;
    }

    [RelayCommand]
    private void CancelAdd()
    {
        IsAddOpen = false;
        if (Entry is null) Url = Server.BaseUrl;
        _ = CheckAsync();
    }

    [RelayCommand]
    private async Task ConfirmAddAsync()
    {
        if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
        {
            Problem = Loc.T("Enter the address as a URL starting with http:// or https://.");
            return;
        }
        Problem = "";
        var entry = Server.Entry(Url);
        if (await _owner.ChangeAsync(c => c.WithProvider(entry), Loc.F("Added {0}.", Name)) is { } error) Problem = error;
    }

    [RelayCommand]
    private void AskRemove()
    {
        Problem = "";
        IsConfirmingRemove = true;
    }

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemove = false;

    [RelayCommand]
    private async Task ConfirmRemoveAsync()
    {
        IsConfirmingRemove = false;
        if (await _owner.RemoveAsync(Id, Name) is { } error) Problem = error;
    }
}

/// <summary>A provider of the user's own in models.yml: edit it (when the form covers every setting it has) or remove it.</summary>
public sealed partial class CustomProviderRowViewModel(CustomProvidersViewModel owner, ModelProviderEntry entry) : ObservableObject
{
    public ModelProviderEntry Entry { get; } = entry;
    public string Id => Entry.Id;
    public bool IsEditable => Entry.NotEditable is null;

    public string Detail => string.Join(" · ", new[]
    {
        Entry.BaseUrl.Length > 0 ? Entry.BaseUrl : null,
        Entry.Api switch
        {
            ModelApis.ChatCompletions => "Chat Completions",
            ModelApis.Responses => "Responses",
            ModelApis.AnthropicMessages => "Anthropic Messages",
            { } other => other,
            null => null,
        },
        Entry.Discovery is not null ? Loc.T("models from the server") + (Entry.Models.Count > 0 ? " + " + CustomProvidersViewModel.Count(Entry.Models.Count) : "")
            : CustomProvidersViewModel.Count(Entry.Models.Count),
    }.Where(s => s is not null));

    public string ReadOnlyNote => Loc.F("Edit models.yml to change it: {0}.", Entry.NotEditable);

    public string RemoveQuestion => (Entry.ApiKey is not null ? Loc.F("Remove {0} from omp's models.yml? omp stops using it and forgets the key written there.", Id) : Loc.F("Remove {0} from omp's models.yml? omp stops using it.", Id));

    [ObservableProperty] private bool _isConfirmingRemove;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string _problem = "";

    public bool HasProblem => Problem.Length > 0;

    [RelayCommand]
    private void Edit()
    {
        if (IsEditable) owner.BeginEdit(Entry);
    }

    [RelayCommand]
    private void AskRemove()
    {
        Problem = "";
        IsConfirmingRemove = true;
    }

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemove = false;

    [RelayCommand]
    private async Task ConfirmRemoveAsync()
    {
        IsConfirmingRemove = false;
        if (await owner.RemoveAsync(Id, Id) is { } error) Problem = error;
    }
}
