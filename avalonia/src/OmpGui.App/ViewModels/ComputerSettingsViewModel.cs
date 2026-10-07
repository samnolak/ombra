using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// Settings → Computer use. omp 18.2.0: computer use is the "computer" eval prelude, on when computer.enabled is true
/// and the eval tool is active (sdk.ts getEvalPreludes). <c>omp config set computer.enabled</c> saves it; <c>/computer
/// on|off</c> turns it on or off for the running session only (a session override, never saved); <c>/computer
/// status</c> reports both. Screenshots: computer.display / maxWidth / maxHeight. Approvals: tools.approval.computer
/// (allow / prompt / deny) over the permission mode (screenshots and reads are the read tier, input the exec tier).
/// </summary>
public sealed partial class ComputerSettingsViewModel : WorkspacePageViewModel
{
    /// <summary>The permission choices: no entry (the permission mode decides), or tools.approval.computer.</summary>
    public static IReadOnlyList<string> Policies { get; } = ["default", "prompt", "allow", "deny"];

    private bool _applying;
    private bool? _pendingSessionToggle;
    private ScreenValues _saved = new("all", "3840", "2400");

    private sealed record ScreenValues(string Display, string MaxWidth, string MaxHeight);

    public ComputerSettingsViewModel(MainViewModel main) : base(main, "computer") { }

    /// <summary>The saved setting (computer.enabled), shown by the switch.</summary>
    [ObservableProperty] private bool _enabled;

    /// <summary>The switch can be used (settings loaded, nothing being saved).</summary>
    [ObservableProperty] private bool _canToggle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    private string _loadError = "";

    public bool HasLoadError => LoadError.Length > 0;

    /// <summary>What <c>/computer status</c> said; null while omp isn't running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionLabel), nameof(SessionOn), nameof(SessionBlocked), nameof(SessionDetail), nameof(HasSessionStatus))]
    private ComputerUseStatus? _session;

    /// <summary>The eval tool in this session: true on, false off, null unknown (omp not running).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionLabel), nameof(SessionBlocked), nameof(SessionDetail), nameof(EvalOff))]
    private bool? _evalActive;

    [ObservableProperty] private bool _evalJs = true;
    [ObservableProperty] private bool _evalPy = true;

    public bool HasSessionStatus => Session is not null;
    public bool SessionOn => Session is { Enabled: true, Active: true };

    /// <summary>On in the settings but not usable in this session (the eval tool is off).</summary>
    public bool SessionBlocked => Session is { Enabled: true, Active: false } || (Enabled && EvalActive == false);

    public bool EvalOff => EvalActive == false;

    public string SessionLabel => Session switch
    {
        null => "",
        { Enabled: true, Active: true } => "On in this session",
        { Enabled: true } => "Can't run in this session",
        _ when EvalActive == false => "Off · eval tool off",
        _ => "Off in this session",
    };

    public string SessionDetail => Session switch
    {
        null => "",
        { Enabled: true, Active: false } => "Computer use runs through omp's eval tool, which is off in this session.",
        _ => $"Screenshots: {(Session.Display is "all" or "" ? "all displays" : "display " + Session.Display)}, at most {Session.MaxWidth} × {Session.MaxHeight} px.",
    };

    // ── Permission for computer actions (tools.approval.computer) ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PolicyDescription))]
    private string _policy = "default";

    public string PolicyDescription => Policy switch
    {
        "prompt" => "omp asks before every computer action, screenshots included.",
        "allow" => "omp takes screenshots, clicks and types without asking, whatever the permission mode.",
        "deny" => "omp can't use the computer at all, even with computer use on.",
        _ => Main.ApprovalMode switch
        {
            "yolo" => "Follows the permission mode. Now “Bypass permissions”: omp clicks and types without asking.",
            "always-ask" => "Follows the permission mode. Now “Ask permissions”: screenshots and reading run, clicks and typing ask first.",
            "write" => "Follows the permission mode. Now “Accept edits”: screenshots and reading run, clicks and typing ask first.",
            _ => "Follows the permission mode by the message box: screenshots and reading run; clicks and typing ask first, unless it is “Bypass permissions”.",
        },
    };

    // ── Screenshots (computer.display, computer.maxWidth, computer.maxHeight) ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScreenChanged))]
    private string _display = "all";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScreenChanged))]
    private string _maxWidth = "3840";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScreenChanged))]
    private string _maxHeight = "2400";

    public bool ScreenChanged => Display.Trim() != _saved.Display || MaxWidth.Trim() != _saved.MaxWidth || MaxHeight.Trim() != _saved.MaxHeight;

    public ObservableCollection<RequirementCheck> Requirements { get; } = [];

    [ObservableProperty] private bool _checking;

    public string PlatformName => OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows" : "Linux";

    /// <summary>Environment for the requirement checks (tests replace it).</summary>
    public Func<string, string?> Environment { get; set; } = System.Environment.GetEnvironmentVariable;

    /// <summary>Which platform's checks to run (tests pin it); null: this computer's.</summary>
    public string? CheckPlatform { get; set; }

    public override async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        CanToggle = false;
        LoadError = "";
        var ct = Main.Lifetime;
        try
        {
            var checks = CheckRequirementsAsync();
            var (config, error) = await WorkspaceConfig.LoadAsync(Main, ct);
            if (config is null) LoadError = error ?? "Couldn't read omp's settings.";
            else ApplyConfig(config);
            await ReadSessionAsync();
            await checks;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            IsLoading = false;
            CanToggle = LoadError.Length == 0;
        }
    }

    private void ApplyConfig(WorkspaceConfig config)
    {
        _applying = true;
        try
        {
            Enabled = config.Bool("computer.enabled") ?? false;
            EvalJs = config.Bool("eval.js") ?? true;
            EvalPy = config.Bool("eval.py") ?? true;
            Policy = ReadPolicy(config);
            _saved = new(config.String("computer.display") ?? "all", config.String("computer.maxWidth") ?? "3840", config.String("computer.maxHeight") ?? "2400");
            Display = _saved.Display;
            MaxWidth = _saved.MaxWidth;
            MaxHeight = _saved.MaxHeight;
        }
        finally { _applying = false; }
    }

    /// <summary>tools.approval.computer as omp normalizes it (approval.ts normalizePolicy): anything else is no policy.</summary>
    private static string ReadPolicy(WorkspaceConfig config)
    {
        try
        {
            var p = config.Record("tools.approval")?["computer"]?.GetValue<string>()?.Trim().ToLowerInvariant();
            return p is "allow" or "prompt" or "deny" ? p : "default";
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return "default"; // not a string
        }
    }

    /// <summary><c>/computer status</c> and <c>/tools</c> (is eval active?), when omp runs.</summary>
    private async Task ReadSessionAsync()
    {
        if (!Main.CanRunOmpCommands)
        {
            Session = null;
            EvalActive = null;
            return;
        }
        var status = await Main.RunOmpCommandAsync("/computer status", TimeSpan.FromSeconds(20), Main.Lifetime);
        Session = status.Ok ? WorkspaceParsers.ParseComputerStatus(status.Output) : null;
        var tools = await Main.RunOmpCommandAsync("/tools", TimeSpan.FromSeconds(20), Main.Lifetime);
        EvalActive = tools.Ok && WorkspaceParsers.ParseTools(tools.Output) is { Count: > 0 } t ? t.TryGetValue("eval", out var on) && on : null;
    }

    private IReadOnlyList<RequirementCheck> _platformChecks = [];

    private async Task CheckRequirementsAsync()
    {
        Checking = true;
        try
        {
            _platformChecks = await ComputerChecks.RunAsync(Main.RunTool, Environment, Main.Lifetime, CheckPlatform);
            RebuildRequirements();
        }
        finally { Checking = false; }
    }

    /// <summary>omp's eval tool first (computer use runs inside it), then what this computer needs.</summary>
    private void RebuildRequirements()
    {
        Requirements.Clear();
        Requirements.Add(EvalCheck());
        foreach (var c in _platformChecks) Requirements.Add(c);
    }

    private RequirementCheck EvalCheck()
    {
        const string title = "omp's eval tool";
        var fix = EvalJs ? null : "Turn on JavaScript eval";
        return EvalActive switch
        {
            true => new(title, "On in this session. Computer use runs inside it: omp scripts the desktop from JavaScript or Python.", CheckState.Ok),
            false => new(title, "Off in this session, and computer use runs inside it. " + (EvalJs
                    ? "JavaScript eval is on in the settings: restart omp, or check the tool list omp is started with."
                    : "Turn on JavaScript eval (eval.js), then restart omp."), CheckState.Failed, fix, fix is null ? null : RequirementCheck.TurnOnEvalAction),
            null when EvalJs || EvalPy => new(title, "On in omp's settings (" + (EvalJs && EvalPy ? "JavaScript and Python" : EvalJs ? "JavaScript" : "Python")
                    + "). Computer use runs inside it.", CheckState.Ok),
            null => new(title, "JavaScript and Python eval are both off in omp's settings, and computer use runs inside eval.", CheckState.Failed, fix, RequirementCheck.TurnOnEvalAction),
        };
    }

    partial void OnEvalActiveChanged(bool? value) => RebuildRequirements();
    partial void OnEvalJsChanged(bool value) => RebuildRequirements();
    partial void OnEvalPyChanged(bool value) => RebuildRequirements();

    [RelayCommand]
    private Task CheckAgain() => CheckRequirementsAsync();

    [RelayCommand]
    private async Task RunCheckActionAsync(RequirementCheck? check)
    {
        if (check?.Action == RequirementCheck.TurnOnEvalAction) await TurnOnEvalAsync();
        else if (check?.Action is { } url) Main.OpenExternal(url);
    }

    partial void OnEnabledChanged(bool value)
    {
        if (_applying) return;
        _ = SaveEnabledAsync(value);
    }

    /// <summary>The switch: saved in omp's settings, then turned on or off in the running session with /computer.</summary>
    private async Task SaveEnabledAsync(bool on)
    {
        CanToggle = false;
        try
        {
            var (ok, value, error) = await WorkspaceConfig.SetAsync(Main, "computer.enabled", on ? "true" : "false", Main.Lifetime);
            if (!ok)
            {
                Say("Not saved: " + error, error: true);
                _applying = true;
                Enabled = !on;
                _applying = false;
                return;
            }
            var overridden = value is { ValueKind: JsonValueKind.True or JsonValueKind.False } v && v.GetBoolean() != on;
            if (!Main.CanRunOmpCommands)
            {
                Say(overridden ? WorkspaceConfig.OverriddenNote("computer use") : on ? "Saved. omp can use this computer from its next start." : "Saved: computer use is off.");
                return;
            }
            if (Main.IsRunning)
            {
                _pendingSessionToggle = on;
                Say("Saved. It applies to this conversation when the current reply ends.");
                return;
            }
            await ApplyToSessionAsync(on, overridden);
        }
        catch (OperationCanceledException) when (Main.Lifetime.IsCancellationRequested) { }
        finally { CanToggle = true; }
    }

    private async Task ApplyToSessionAsync(bool on, bool overridden = false)
    {
        var r = await Main.RunOmpCommandAsync(on ? "/computer on" : "/computer off", TimeSpan.FromSeconds(30), Main.Lifetime);
        if (!r.Ok) Say("Saved, but omp did not answer /computer: " + r.Error, error: true);
        else if (r.Output.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
            Say("Saved, but omp can't use the computer in this session: it runs through omp's eval tool, which is off here. " + EvalFix, error: true);
        else if (overridden) Say(WorkspaceConfig.OverriddenNote("computer use") + " In this session it is " + (on ? "on." : "off."));
        else Say(on ? "Computer use is on, saved for the next sessions too." : "Computer use is off, in this session and the next ones.");
        // "/computer on" prints the new status with its confirmation; otherwise ask for it
        if (r.Ok && WorkspaceParsers.ParseComputerStatus(r.Output) is { } status) Session = status;
        else await ReadSessionAsync();
    }

    private string EvalFix => !EvalJs && !EvalPy
        ? "Turn on JavaScript eval below, then restart omp."
        : !EvalJs ? "JavaScript eval is off and Python isn't available to omp: turn on JavaScript eval below, then restart omp."
        : "Check omp's tool settings, then restart omp.";

    protected override void OnPhaseChanged()
    {
        OnPropertyChanged(nameof(PolicyDescription));
        if (_pendingSessionToggle is { } on && Main.CanRunOmpCommands && !Main.IsRunning)
        {
            _pendingSessionToggle = null;
            _ = ApplyToSessionAsync(on);
        }
    }

    [RelayCommand]
    private async Task TurnOnEvalAsync()
    {
        var (ok, _, error) = await WorkspaceConfig.SetAsync(Main, "eval.js", "true", Main.Lifetime);
        if (!ok) { Say("Not saved: " + error, error: true); return; }
        EvalJs = true;
        SavedForNextStart("JavaScript eval is on. omp turns its eval tool on when it starts: restart omp, then turn computer use on.");
    }

    [RelayCommand]
    private async Task SetPolicyAsync(string? key)
    {
        if (key is null || !Policies.Contains(key) || key == Policy || _applying) return;
        var ct = Main.Lifetime;
        // tools.approval is one record for every tool: keep the other tools' entries.
        var (config, error) = await WorkspaceConfig.LoadAsync(Main, ct);
        if (config is null) { Say(error ?? "Couldn't read omp's settings.", error: true); return; }
        var record = config.Record("tools.approval") ?? [];
        if (key == "default") record.Remove("computer");
        else record["computer"] = key;
        var (ok, _, setError) = await WorkspaceConfig.SetAsync(Main, "tools.approval", record.ToJsonString(), ct);
        if (!ok) { Say("Not saved: " + setError, error: true); return; }
        Policy = key;
        SavedForNextStart();
    }

    [RelayCommand]
    private async Task SaveScreenAsync()
    {
        var display = Display.Trim();
        var width = MaxWidth.Trim();
        var height = MaxHeight.Trim();
        if (display.Length == 0) { Say("Display: “all”, or the id of one display.", error: true); return; }
        if (!int.TryParse(width, NumberStyles.None, CultureInfo.InvariantCulture, out var w) || w < 1
            || !int.TryParse(height, NumberStyles.None, CultureInfo.InvariantCulture, out var h) || h < 1)
        {
            Say("Width and height are whole numbers of pixels, such as 1920 and 1080.", error: true);
            return;
        }
        foreach (var (key, value, old) in new[] { ("computer.display", display, _saved.Display), ("computer.maxWidth", w.ToString(CultureInfo.InvariantCulture), _saved.MaxWidth), ("computer.maxHeight", h.ToString(CultureInfo.InvariantCulture), _saved.MaxHeight) })
        {
            if (value == old) continue;
            var (ok, _, error) = await WorkspaceConfig.SetAsync(Main, key, value, Main.Lifetime);
            if (!ok) { Say("Not saved: " + error, error: true); return; }
        }
        _saved = new(display, w.ToString(CultureInfo.InvariantCulture), h.ToString(CultureInfo.InvariantCulture));
        _applying = true;
        (Display, MaxWidth, MaxHeight) = (_saved.Display, _saved.MaxWidth, _saved.MaxHeight);
        _applying = false;
        OnPropertyChanged(nameof(ScreenChanged));
        SavedForNextStart();
    }

    [RelayCommand]
    private void RevertScreen()
    {
        (Display, MaxWidth, MaxHeight) = (_saved.Display, _saved.MaxWidth, _saved.MaxHeight);
    }
}
