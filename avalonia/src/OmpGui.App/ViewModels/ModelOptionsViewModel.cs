using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// The switches under the model list, each on what omp 18.2.0 offers over RPC, read when the menu opens:
/// <list type="bullet">
/// <item>Fast mode: <c>set_fast_mode</c>, state in get_state (<c>fastModeEnabled</c>); per session, only for models with
/// a priority tier (OpenAI, Anthropic…), omp says so otherwise.</item>
/// <item>Extended context: <c>/extended-context on|off|status</c>; omp saves it in its settings.</item>
/// <item>Advisor: <c>/advisor on|off|status</c>, for this session; it needs a model in omp's "advisor" role, which the
/// GUI sets with <c>omp config set modelRoles</c> and a restart of omp (omp reads roles at start).</item>
/// <item>Auto-compact: <c>set_auto_compaction</c> (state: get_state <c>autoCompactionEnabled</c>); saved in omp's settings.</item>
/// <item>Auto-retry: <c>set_auto_retry</c> (state: <c>omp config get retry.enabled</c>, get_state has none); saved too.</item>
/// </list>
/// </summary>
public sealed partial class ModelOptionsViewModel(MainViewModel owner) : ObservableObject
{
    private const string NoFastTier = "Fast mode is unavailable for the current model.";
    private bool _quiet;
    private int? _retryReadFor;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoaded;

    [ObservableProperty] private bool _fastMode;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFastNote))] private string _fastNote = "";
    public bool HasFastNote => FastNote.Length > 0;

    [ObservableProperty] private bool _extendedContext;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasExtendedNote))] private string _extendedNote = "";
    public bool HasExtendedNote => ExtendedNote.Length > 0;

    [ObservableProperty] private bool _advisor;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasAdvisorNote))] private string _advisorNote = "";
    public bool HasAdvisorNote => AdvisorNote.Length > 0;
    /// <summary>The advisor is on for this session but omp has no model in its "advisor" role.</summary>
    [ObservableProperty] private bool _advisorNeedsModel;
    [ObservableProperty] private bool _isChoosingAdvisorModel;

    [ObservableProperty] private bool _autoCompaction;
    [ObservableProperty] private bool _autoRetry;
    [ObservableProperty] private bool _autoRetryKnown;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSettingsNote))] private string _settingsNote = "";
    public bool HasSettingsNote => SettingsNote.Length > 0;

    /// <summary>Models for the advisor role (omp's available models, the list the menu shows).</summary>
    public IEnumerable<ModelItemViewModel> AdvisorModels => owner.Models;

    /// <summary>Reads every option's state from omp (the menu just opened).</summary>
    internal async Task LoadAsync()
    {
        if (!owner.CanRunOmpCommands) return;
        IsLoading = true;
        try
        {
            var state = await owner.Session.GetOptionsStateAsync(owner.Closing);
            var ext = await owner.RunOmpCommandAsync("/extended-context status");
            var adv = await owner.RunOmpCommandAsync("/advisor status");
            // omp's CLI starts a process: read once per omp (the switch keeps it current afterwards)
            var retry = _retryReadFor == owner.Session.ProcessId && AutoRetryKnown ? (AutoRetry ? "true" : "false") : await owner.ConfigValueAsync("retry.enabled");
            _retryReadFor = owner.Session.ProcessId;
            Quietly(() =>
            {
                if (state is not null)
                {
                    FastMode = state.FastModeEnabled;
                    AutoCompaction = state.AutoCompaction;
                }
                if (ext.Ok && SessionOutputs.ParseOnOff(ext.Output, "Extended context") is { } on) ExtendedContext = on;
                if (adv.Ok) ApplyAdvisor(adv.Output);
                AutoRetryKnown = retry is not null;
                AutoRetry = retry != "false";
            });
            FastNote = "";
            ExtendedNote = "";
            SettingsNote = "";
        }
        finally
        {
            IsLoading = false;
            IsLoaded = true;
        }
    }

    private void Quietly(Action a)
    {
        _quiet = true;
        try { a(); }
        finally { _quiet = false; }
    }

    partial void OnFastModeChanged(bool value)
    {
        if (!_quiet) _ = SetFastAsync(value);
    }

    private async Task SetFastAsync(bool on)
    {
        var (enabled, error) = await owner.Session.SetFastModeAsync(on, owner.Closing);
        Quietly(() => FastMode = error is null ? enabled : !on);
        FastNote = error is null ? "" : error == NoFastTier ? "Not available for this model: it has no priority tier." : error;
    }

    partial void OnExtendedContextChanged(bool value)
    {
        if (!_quiet) _ = SetExtendedAsync(value);
    }

    private async Task SetExtendedAsync(bool on)
    {
        var r = await owner.RunOmpCommandAsync(on ? "/extended-context on" : "/extended-context off");
        var now = r.Ok ? SessionOutputs.ParseOnOff(r.Output, "Extended context") : null;
        Quietly(() => ExtendedContext = now ?? !on);
        ExtendedNote = now is null ? "Not changed: " + (r.Error ?? r.Output) : "";
    }

    partial void OnAdvisorChanged(bool value)
    {
        if (!_quiet) _ = SetAdvisorAsync(value);
    }

    private async Task SetAdvisorAsync(bool on)
    {
        var r = await owner.RunOmpCommandAsync(on ? "/advisor on" : "/advisor off");
        if (r.Ok) Quietly(() => ApplyAdvisor(r.Output));
        else
        {
            Quietly(() => Advisor = !on);
            AdvisorNote = "Not changed: " + r.Error;
        }
    }

    private void ApplyAdvisor(string text)
    {
        var state = SessionOutputs.ParseAdvisor(text.Trim());
        Advisor = state is AdvisorState.On or AdvisorState.NeedsModel;
        AdvisorNeedsModel = state == AdvisorState.NeedsModel;
        AdvisorNote = state switch
        {
            AdvisorState.NeedsModel => "Needs a model for omp's advisor role.",
            AdvisorState.On when SessionOutputs.AdvisorModel(text.Trim()) is { } m => "Reviewing with " + m,
            _ => "",
        };
        if (!AdvisorNeedsModel) IsChoosingAdvisorModel = false;
    }

    [RelayCommand]
    private void ChooseAdvisorModel() => IsChoosingAdvisorModel = !IsChoosingAdvisorModel;

    /// <summary>
    /// Puts <paramref name="model"/> in omp's "advisor" role (<c>omp config set modelRoles</c>, the other roles kept),
    /// restarts omp on this session so it reads the role, and turns the advisor on again.
    /// </summary>
    [RelayCommand]
    private async Task SetAdvisorModelAsync(ModelItemViewModel? model)
    {
        if (model is null) return;
        IsChoosingAdvisorModel = false;
        AdvisorNote = "Setting the advisor's model…";
        var roles = await owner.RunOmpCliAsync(["config", "get", "modelRoles", "--json"], TimeSpan.FromSeconds(30), owner.Closing);
        JsonObject map;
        try
        {
            map = roles.Ok && JsonNode.Parse(roles.Stdout)?["value"] is JsonObject v ? (JsonObject)v.DeepClone() : [];
        }
        catch (JsonException) { map = []; }
        map["advisor"] = model.Key;
        var set = await owner.RunOmpCliAsync(["config", "set", "modelRoles", map.ToJsonString(), "--json"], TimeSpan.FromSeconds(30), owner.Closing);
        if (!set.Ok)
        {
            AdvisorNote = "omp did not take it: " + (set.Stderr.Trim() is { Length: > 0 } e ? e : "exit " + set.ExitCode);
            return;
        }
        // omp reads model roles when it starts
        if (!await owner.RestartOmpToApplyAsync())
        {
            AdvisorNote = $"Saved: {model.Name}. It applies once the current reply ends and omp restarts.";
            return;
        }
        var r = await owner.RunOmpCommandAsync("/advisor on");
        Quietly(() => ApplyAdvisor(r.Output));
    }

    partial void OnAutoCompactionChanged(bool value)
    {
        if (!_quiet) _ = SetSettingAsync(value, owner.Session.SetAutoCompactionAsync, v => AutoCompaction = v);
    }

    partial void OnAutoRetryChanged(bool value)
    {
        if (!_quiet) _ = SetSettingAsync(value, owner.Session.SetAutoRetryAsync, v => AutoRetry = v);
    }

    private async Task SetSettingAsync(bool on, Func<bool, CancellationToken, Task<string?>> set, Action<bool> revert)
    {
        var error = await set(on, owner.Closing);
        if (error is null)
        {
            SettingsNote = "";
            return;
        }
        Quietly(() => revert(!on));
        SettingsNote = "Not changed: " + error;
    }
}
