using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;
using OmpGui.ClientCore.Network;

namespace OmpGui.App.ViewModels;

/// <summary>A connection check's address: "Network request to api.anthropic.com without a key".</summary>
public sealed record ConnectionCheckTarget(string Host, string Url)
{
    public string Label => $"Network request to {Host} without a key";
}

/// <summary>
/// Settings › Advanced › Corporate network certificates (<see cref="CorporateTrust"/>). Nothing runs until the user asks:
/// "Set up…" explains what changes first, Enable saves the mode (and, for a file, the app's validated copy) and starts
/// every chat's omp again (idle ones now, working ones after their run); Turn off removes the mode and only the file the
/// app made. The connection check is separate and sends one request without a key. Logs carry codes only.
/// </summary>
public sealed partial class NetworkTrustViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public NetworkTrustViewModel(MainViewModel main)
    {
        _main = main;
        main.SettingsCategoryShown += key =>
        {
            if (key == PageKey) Load();
        };
        if (main.IsSettingsOpen && main.SettingsCategory == PageKey) Avalonia.Threading.Dispatcher.UIThread.Post(Load);
    }

    public const string PageKey = "advanced";

    /// <summary>The app's environment as omp inherits it (tests replace it).</summary>
    public Func<string, string?> Inherited { get; set; } = Environment.GetEnvironmentVariable;

    /// <summary>The macOS mode is offered (Bun's system store is verified there only); tests set it.</summary>
    public bool SystemModeAvailable { get; set; } = CorporateTrust.SystemModeSupported;

    /// <summary>The system's file picker for a certificate file; set by the view. Null: nothing chosen.</summary>
    public Func<Task<string?>>? PickCertificateFile { get; set; }

    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    // ── State ──

    /// <summary>The saved mode: null (off), <c>system</c> or <c>pem</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOn), nameof(SetUpLabel))]
    private string? _mode;

    public bool IsOn => Mode is not null;

    public string SetUpLabel => IsOn ? "Set up again…" : "Set up corporate network certificates…";

    /// <summary>"Off", "On: …", and what the user's own settings decide instead.</summary>
    [ObservableProperty] private string _statusText = OffText;

    public const string OffText = "Off: omp trusts only the certificate authorities built into its runtime.";

    /// <summary>The user's own variables that decide instead of the app (shown, never changed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUserSettingsText))]
    private string _userSettingsText = "";

    public bool HasUserSettingsText => UserSettingsText.Length > 0;

    /// <summary>The explanation card shown before anything changes: <c>system</c>, <c>pem</c> or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExplaining), nameof(IsExplainingSystem), nameof(IsExplainingPem), nameof(CanEnable), nameof(ExplainTitle))]
    private string? _explaining;

    public bool IsExplaining => Explaining is not null;
    public bool IsExplainingSystem => Explaining == CorporateTrust.SystemMode;
    public bool IsExplainingPem => Explaining == CorporateTrust.PemMode;

    public string ExplainTitle => IsExplainingSystem ? "Trust the certificate authorities macOS trusts" : "Trust the certificate authorities in a file";

    /// <summary>The file chosen on the card and what its check found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEnable), nameof(HasChosenFile), nameof(ChosenFileOk), nameof(ChosenFileText))]
    private CaFileCheck? _chosenFile;

    private string? _chosenPath;

    public bool HasChosenFile => ChosenFile is not null;
    public bool ChosenFileOk => ChosenFile?.Ok == true;
    public string ChosenFileText => ChosenFile?.Message ?? "";

    /// <summary>What blocks Enable on the card (the user's own variable); empty when nothing does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEnable), nameof(HasBlocker))]
    private string _blocker = "";

    public bool HasBlocker => Blocker.Length > 0;

    public bool CanEnable => Blocker.Length == 0 && (IsExplainingSystem || IsExplainingPem && ChosenFileOk);

    /// <summary>What just happened (enabled, turned off, refused); empty when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = "";

    [ObservableProperty] private bool _messageIsError;

    public bool HasMessage => Message.Length > 0;

    /// <summary>Restarts still waiting for a run to end, and omp terminal tabs that keep the old setting.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRestartNote))]
    private string _restartNote = "";

    public bool HasRestartNote => RestartNote.Length > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnableCommand), nameof(TurnOffCommand))]
    private bool _busy;

    public bool CanUseFileInstead => SystemModeAvailable;

    // ── Connection check ──

    public ObservableCollection<ConnectionCheckTarget> CheckTargets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCheckResult))]
    private string _checkResult = "";

    [ObservableProperty] private bool _checkIsOk;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCheckCommand))]
    private bool _isChecking;

    public bool HasCheckResult => CheckResult.Length > 0;

    // ── Reading ──

    /// <summary>Reads the saved mode and the user's own variables again (the page is shown).</summary>
    public void Load()
    {
        var o = Settings();
        Mode = o.CorporateTrust is CorporateTrust.SystemMode or CorporateTrust.PemMode ? o.CorporateTrust : null;
        StatusText = Status(o);
        UserSettingsText = UserSettingsLine(o);
        LoadTargets();
        UpdateRestartNote();
    }

    private OmpRuntimeOptions Settings()
    {
        try { return _main.SettingsStore?.Load() ?? new OmpRuntimeOptions(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return new OmpRuntimeOptions(); }
    }

    private UserTrustSettings Inspect(OmpRuntimeOptions o) =>
        CorporateTrust.Inspect(o.Environment, Inherited, o.CorporateTrustBundle);

    private string Status(OmpRuntimeOptions o)
    {
        var user = Inspect(o);
        switch (o.CorporateTrust)
        {
            case CorporateTrust.SystemMode when !SystemModeAvailable:
                return "Saved as \"macOS certificates\", which works on macOS only: nothing is added on this computer. Turn it off, or use a certificate file.";
            case CorporateTrust.SystemMode when user.SystemCaConflict:
                return "On, but your own setting decides, so the app adds nothing: " + string.Join("; ", user.SystemCaSettings.Select(s => s.Describe())) + ".";
            case CorporateTrust.SystemMode when user.SystemCaAlreadyOn:
                return "On. Your own environment already makes omp use the certificates macOS trusts, so the app adds nothing.";
            case CorporateTrust.SystemMode:
                return "On: omp trusts the certificate authorities macOS trusts.";
            case CorporateTrust.PemMode when o.CorporateTrustBundle is not { } bundle || !File.Exists(bundle):
                return "On, but the app's copy of your certificate file is missing: set it up again.";
            case CorporateTrust.PemMode when user.SetsExtraCa:
                return "On, but your own NODE_EXTRA_CA_CERTS decides, so the app adds nothing.";
            case CorporateTrust.PemMode:
                var check = CorporateCaFile.Validate(o.CorporateTrustBundle!, Now());
                return check.Ok ? "On: omp also trusts " + check.Message : "On, but the certificate file needs attention: " + check.Message;
            default:
                return OffText;
        }
    }

    private string UserSettingsLine(OmpRuntimeOptions o)
    {
        var user = Inspect(o);
        var set = user.SystemCaSettings.Concat(user.ExtraCa is { } e ? [e] : []).Select(s => s.Describe()).ToList();
        return set.Count == 0 ? "" : "Set by you: " + string.Join("; ", set) + ".";
    }

    private void LoadTargets()
    {
        var targets = new List<ConnectionCheckTarget>
        {
            new("api.anthropic.com", "https://api.anthropic.com/v1/models"),
            new("api.openai.com", "https://api.openai.com/v1/models"),
        };
        // The current model's provider when omp listed its address: scheme, host, port and path only
        if (_main.CurrentModelBaseUrl is { } b && Uri.TryCreate(b, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
            && targets.All(t => !string.Equals(t.Host, u.Host, StringComparison.OrdinalIgnoreCase)))
            targets.Add(new(u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}", u.GetLeftPart(UriPartial.Path)));
        if (targets.SequenceEqual(CheckTargets)) return;
        CheckTargets.Clear();
        foreach (var t in targets) CheckTargets.Add(t);
    }

    // ── The card ──

    [RelayCommand]
    private void BeginSetup() => Explain(SystemModeAvailable ? CorporateTrust.SystemMode : CorporateTrust.PemMode);

    [RelayCommand]
    private void UseFileInstead() => Explain(CorporateTrust.PemMode);

    [RelayCommand]
    private void UseSystemInstead()
    {
        if (SystemModeAvailable) Explain(CorporateTrust.SystemMode);
    }

    private void Explain(string mode)
    {
        Message = "";
        ChosenFile = null;
        _chosenPath = null;
        var o = Settings();
        var user = Inspect(o);
        Blocker = mode == CorporateTrust.SystemMode
            ? user.SystemCaConflict
                ? "Your own setting chooses another certificate store, so the app won't add anything: "
                  + string.Join("; ", user.SystemCaSettings.Select(s => s.Describe())) + ". Change or remove it first if you want this."
                : ""
            : user.ExtraCa is { } extra
                ? "You already use your own certificate file: " + extra.Describe()
                  + ". The app won't replace or merge it; add your organisation's certificates to that file instead, or remove the variable first."
                : "";
        // Set up again with a file: the app's copy is checked again and can be kept as it is
        if (mode == CorporateTrust.PemMode && o.CorporateTrust == CorporateTrust.PemMode && o.CorporateTrustBundle is { } bundle && File.Exists(bundle))
        {
            _chosenPath = bundle;
            ChosenFile = CorporateCaFile.Validate(bundle, Now());
        }
        Explaining = mode;
    }

    [RelayCommand]
    private void Cancel()
    {
        Explaining = null;
        ChosenFile = null;
        _chosenPath = null;
        Blocker = "";
    }

    [RelayCommand]
    private async Task ChooseFileAsync()
    {
        if (PickCertificateFile is not { } pick || await pick() is not { } path) return;
        ChooseFile(path);
    }

    /// <summary>The file the picker returned: checked at once, nothing copied yet.</summary>
    public void ChooseFile(string path)
    {
        _chosenPath = path;
        ChosenFile = CorporateCaFile.Validate(path, Now());
        Console.Error.WriteLine("corp-trust: file " + ChosenFile.Code + (ChosenFile.Ok ? $" ({ChosenFile.Count})" : ""));
    }

    private bool CanChange() => !Busy;

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task EnableAsync()
    {
        if (!CanEnable || _main.SettingsStore is not { } store) return;
        Busy = true;
        try
        {
            var before = Settings();
            OmpRuntimeOptions after;
            string? oldBundle = before.CorporateTrust == CorporateTrust.PemMode ? before.CorporateTrustBundle : null;
            bool changed;
            if (IsExplainingSystem)
            {
                changed = before.CorporateTrust != CorporateTrust.SystemMode;
                after = before with { CorporateTrust = CorporateTrust.SystemMode, CorporateTrustBundle = null };
            }
            else
            {
                // Checked again right before the copy: the file may have changed since it was chosen
                var check = CorporateCaFile.Validate(_chosenPath!, Now());
                ChosenFile = check;
                if (!check.Ok) return;
                var bundle = CorporateTrust.BundlePathFor(store.Path);
                var same = before.CorporateTrust == CorporateTrust.PemMode && before.CorporateTrustBundle == bundle
                    && File.Exists(bundle) && File.ReadAllText(bundle) == check.Pem;
                if (!same) CorporateCaFile.Install(check.Pem!, bundle);
                changed = !same;
                oldBundle = oldBundle == bundle ? null : oldBundle;
                after = before with { CorporateTrust = CorporateTrust.PemMode, CorporateTrustBundle = bundle };
            }
            if (!Save(_ => after)) return;
            if (oldBundle is not null) RemoveOurBundle(store, oldBundle);
            Console.Error.WriteLine("corp-trust: " + after.CorporateTrust + (changed ? " enabled" : " unchanged"));
            Explaining = null;
            ChosenFile = null;
            _chosenPath = null;
            Load();
            await RestartAsync(changed, changed ? "On." : "Already on with these settings: nothing changed.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("corp-trust: enable failed " + e.GetType().Name);
            Say("The app could not save its copy of the certificates (" + e.GetType().Name + "). Nothing changed.", error: true);
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task TurnOffAsync()
    {
        if (_main.SettingsStore is not { } store) return;
        Busy = true;
        try
        {
            var before = Settings();
            if (before.CorporateTrust is null && before.CorporateTrustBundle is null) return;
            if (!Save(o => o with { CorporateTrust = null, CorporateTrustBundle = null })) return;
            if (before.CorporateTrustBundle is { } bundle) RemoveOurBundle(store, bundle);
            Console.Error.WriteLine("corp-trust: off");
            Cancel();
            Load();
            await RestartAsync(true, "Off.");
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Deletes the app's own bundle only (a path in a hand-edited settings file that is not ours stays).</summary>
    private void RemoveOurBundle(ClientSettingsStore store, string bundle)
    {
        var ours = CorporateTrust.BundlePathFor(store.Path);
        if (!string.Equals(Path.GetFullPath(bundle), ours, StringComparison.Ordinal)) return;
        try { CorporateCaFile.Remove(ours); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("corp-trust: remove failed " + e.GetType().Name);
        }
    }

    private bool Save(Func<OmpRuntimeOptions, OmpRuntimeOptions> change)
    {
        try
        {
            _main.SettingsStore!.Update(change);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Say("Settings not saved: " + e.Message, error: true);
            return false;
        }
    }

    private void Say(string text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
    }

    /// <summary>Every chat's omp starts again with the change: idle ones now, working ones after their run.</summary>
    private async Task RestartAsync(bool changed, string done)
    {
        if (!changed)
        {
            Say(done);
            return;
        }
        Say(done + " Restarting omp…");
        await _main.RestartEnginesAsync();
        Say(done + (_main.PendingEngineRestarts == 0 ? " omp restarted with it; new chats start with it." : " New chats start with it."));
        UpdateRestartNote();
        _ = WatchPendingAsync();
    }

    private bool _watching;

    /// <summary>The note follows the chats that restart after their run until none is left.</summary>
    private async Task WatchPendingAsync()
    {
        if (_watching) return;
        _watching = true;
        try
        {
            while (_main.PendingEngineRestarts > 0 && !_main.Lifetime.IsCancellationRequested)
            {
                await Task.Delay(500);
                UpdateRestartNote();
            }
            UpdateRestartNote();
        }
        catch (OperationCanceledException) { }
        finally { _watching = false; }
    }

    private void UpdateRestartNote()
    {
        var notes = new List<string>();
        var pending = _main.PendingEngineRestarts;
        if (pending > 0)
            notes.Add(pending == 1 ? "A chat that is working (marked in the sidebar) restarts omp with the change when its run ends."
                : $"{pending} chats that are working (marked in the sidebar) restart omp with the change when their runs end.");
        if (_main.OpenOmpTerminals > 0 && HasMessage)
            notes.Add("omp terminal tabs that are already open keep the previous setting until you open them again.");
        RestartNote = string.Join(" ", notes);
    }

    // ── Connection check ──

    private bool CanRunCheck() => !IsChecking;

    [RelayCommand(CanExecute = nameof(CanRunCheck))]
    private async Task RunCheckAsync(ConnectionCheckTarget? target)
    {
        if (target is null) return;
        IsChecking = true;
        CheckResult = "Checking " + target.Host + "…";
        CheckIsOk = false;
        try
        {
            ConnectionCheckResult result;
            if (_main.OmpCliLaunch is not { } launch)
                result = new(ConnectionCheckKind.Unavailable, "no-launch", "The check needs omp's runtime, which is not set up here.");
            else
                result = await ConnectionCheck.RunAsync(launch([], null), target.Url, _main.Lifetime);
            Console.Error.WriteLine(result.LogLine);
            CheckIsOk = result.Kind == ConnectionCheckKind.Works;
            CheckResult = target.Host + ": " + result.Message
                + (result.Kind == ConnectionCheckKind.Tls && IsOn
                    ? " Corporate certificates are on: the certificate authority your network uses may be missing (try a certificate file from IT)."
                    : "");
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsChecking = false;
        }
    }
}
