using OmpGui.App.Localization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;
using OmpGui.ClientCore.Network;

namespace OmpGui.App.ViewModels;

/// <summary>An added provider and the addresses strict mode lets through for it.</summary>
public sealed record PrivacyProviderRow(string Provider, string Api, string SignIn)
{
    public bool HasSignIn => SignIn.Length > 0;
}

/// <summary>A destination strict mode refused: "pypi.org:443 · 14:02".</summary>
public sealed record PrivacyBlockedRow(string Host, string Detail);

/// <summary>
/// Settings › Advanced › Network privacy (<see cref="NetworkPrivacy"/>). The provider gate is always on and shows what
/// omp may contact; strict network privacy is a switch (saved, then every chat's omp starts again). The addresses the
/// user allows, and the refused destinations with "Allow", apply to the next connection without a restart.
/// </summary>
public sealed partial class NetworkPrivacyViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public NetworkPrivacyViewModel(MainViewModel main)
    {
        _main = main;
        main.SettingsCategoryShown += key =>
        {
            if (key == NetworkTrustViewModel.PageKey) Load();
        };
        if (main.Privacy is { } privacy) privacy.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
        if (main.IsSettingsOpen && main.SettingsCategory == NetworkTrustViewModel.PageKey) Avalonia.Threading.Dispatcher.UIThread.Post(Load);
    }

    /// <summary>What omp does that no setting stops (UPSTREAM OMP ISSUE: docs/upstream/omp-discovery-opt-in.md).</summary>
    public static string CatalogLimitation =>
        Loc.T("Known limitation: omp downloads its public model catalog from catalog.stencil.so (a copy of models.dev) in the background. The request carries no keys and no chat content, and omp has no setting that turns it off. Strict network privacy blocks it.");

    // ── State ──

    /// <summary>Which providers omp may contact, as the newest omp start found them.</summary>
    [ObservableProperty] private string _gateText = "";

    /// <summary>Why the providers could not be turned off (omp started with the user's own command); empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGateWarning))]
    private string _gateWarning = "";

    public bool HasGateWarning => GateWarning.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrictStatus), nameof(StrictButtonLabel))]
    private bool _isStrict;

    public string StrictStatus => IsStrict
        ? Loc.T("On: omp and the tools it runs reach only the addresses below. Anything else is refused and listed here.")
        : Loc.T("Off: omp reaches the providers you added, and whatever its tools and your connectors need.");

    public string StrictButtonLabel => IsStrict ? Loc.T("Turn off") : Loc.T("Turn on strict network privacy");

    public ObservableCollection<PrivacyProviderRow> Providers { get; } = [];

    public ObservableCollection<string> UserHosts { get; } = [];

    public ObservableCollection<PrivacyBlockedRow> Blocked { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlocked))]
    private int _blockedCount;

    public bool HasBlocked => BlockedCount > 0;

    /// <summary>The address being typed for "Allow".</summary>
    [ObservableProperty] private string _newHost = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = "";

    [ObservableProperty] private bool _messageIsError;

    public bool HasMessage => Message.Length > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleStrictCommand))]
    private bool _busy;

    // ── Reading ──

    /// <summary>Reads the saved switch and addresses again (the page is shown).</summary>
    public void Load()
    {
        var o = Settings();
        IsStrict = o.StrictNetworkPrivacy == true;
        Replace(UserHosts, o.StrictNetworkAllowedHosts ?? []);
        Refresh();
    }

    /// <summary>The gate's newest result and the refused list (the service changed).</summary>
    public void Refresh()
    {
        if (_main.Privacy is not { } privacy)
        {
            GateText = Loc.T("The app checks which providers you added each time omp starts.");
            return;
        }
        GateWarning = privacy.GateUnavailable ?? "";
        if (privacy.LastGate is { } gate)
        {
            var off = gate.Disabled.Count;
            GateText = gate.Added.Count == 0
                ? Loc.F("No provider is added, so omp contacts none: all {0} providers it knows, local model servers included, are off for omp. Sign in under Model providers to add one.", off)
                : Loc.F("omp may contact only the providers you added: {0}. The other {1} providers it knows, local model servers included, are off for omp until you add them.", string.Join(", ", gate.Added), off);
        }
        else GateText = Loc.T("The app checks which providers you added each time omp starts.");
        Replace(Providers, privacy.AddedProviderHosts().Select(p => new PrivacyProviderRow(p.Key, string.Join(", ", p.Value.Api), string.Join(", ", p.Value.SignIn))));
        var blocked = privacy.Blocked;
        Replace(Blocked, blocked.Select(b => new PrivacyBlockedRow(b.Host, $"{b.Host}:{b.Port} · {b.At.ToLocalTime():HH:mm}")));
        BlockedCount = blocked.Count;
    }

    private OmpRuntimeOptions Settings()
    {
        try { return _main.SettingsStore?.Load() ?? new OmpRuntimeOptions(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return new OmpRuntimeOptions(); }
    }

    // ── Changing ──

    private bool CanChange() => !Busy;

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ToggleStrictAsync()
    {
        var on = !IsStrict;
        Busy = true;
        try
        {
            if (!Save(o => o with { StrictNetworkPrivacy = on ? true : null })) return;
            IsStrict = on;
            Say((on ? Loc.T("Strict network privacy is on.") : Loc.T("Strict network privacy is off.")) + Loc.T(" Restarting omp…"));
            await _main.RestartEnginesAsync();
            Say((on ? Loc.T("Strict network privacy is on") : Loc.T("Strict network privacy is off"))
                + (_main.PendingEngineRestarts == 0 ? Loc.T(" for every chat.") : Loc.T("; chats that are working switch when their run ends."))
                + (_main.OpenOmpTerminals > 0 ? Loc.T(" omp terminal tabs already open keep the previous setting.") : ""));
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void AddHost() => Allow(NewHost.Trim());

    [RelayCommand]
    private void AllowBlocked(PrivacyBlockedRow? row)
    {
        if (row is not null) Allow(row.Host);
    }

    [RelayCommand]
    private void RemoveHost(string? host)
    {
        if (host is null) return;
        var hosts = UserHosts.Where(h => !h.Equals(host, StringComparison.OrdinalIgnoreCase)).ToList();
        if (Save(o => o with { StrictNetworkAllowedHosts = hosts.Count == 0 ? null : [.. hosts] }))
        {
            Replace(UserHosts, hosts);
            _main.Privacy?.SetUserHosts(hosts);
            Say(Loc.F("{0} is no longer allowed.", host));
        }
    }

    private void Allow(string host)
    {
        if (!HostAllowlist.IsValidEntry(host))
        {
            Say(Loc.T("Type an address such as pypi.org or *.example.com (no https://, path or port)."), error: true);
            return;
        }
        if (UserHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            Say(Loc.F("{0} is already allowed.", host));
            return;
        }
        var hosts = UserHosts.Append(host).ToList();
        if (!Save(o => o with { StrictNetworkAllowedHosts = [.. hosts] })) return;
        Replace(UserHosts, hosts);
        _main.Privacy?.SetUserHosts(hosts);
        NewHost = "";
        Say(Loc.F("{0} is allowed from now on.", host));
    }

    private bool Save(Func<OmpRuntimeOptions, OmpRuntimeOptions> change)
    {
        if (_main.SettingsStore is not { } store)
        {
            Say(Loc.T("Settings not saved: this window has no settings file."), error: true);
            return false;
        }
        try
        {
            store.Update(change);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Say(Loc.T("Settings not saved: ") + e.Message, error: true);
            return false;
        }
    }

    private void Say(string text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var list = items.ToList();
        if (list.SequenceEqual(target)) return;
        target.Clear();
        foreach (var x in list) target.Add(x);
    }
}
