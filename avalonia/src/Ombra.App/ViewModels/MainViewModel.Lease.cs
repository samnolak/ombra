using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.App.ViewModels;

/// <summary>
/// A chat another Ombra window or a terminal omp has open (SessionController.Lease.cs): shown read-only under a banner
/// that names the holder, with "Open here". The message box keeps its draft but takes no input meanwhile.
/// </summary>
public sealed partial class MainViewModel
{
    private SessionLease? _lease;

    /// <summary>Another process owns this chat's file: nothing can be sent until it is opened here.</summary>
    public bool IsLeaseReadOnly => _lease is not null;

    /// <summary>The message box takes no input (read-only history, or a chat open elsewhere); its text stays.</summary>
    public bool IsComposerLocked => IsReadOnly || IsLeaseReadOnly;

    /// <summary>"Open in another Ombra window (pid 4242)" / "This chat was opened elsewhere". Each holder is its own whole
    /// sentence, so a language words it as one (not "Open in" + a translated holder name).</summary>
    public string LeaseBannerText => _lease switch
    {
        null => "",
        { State: SessionLeaseState.Lost } => Loc.T("This chat was opened elsewhere"),
        { Holder: { } holder } => (holder.Kind, holder.Pid) switch
        {
            ("ombra", { } pid) => Loc.F("Open in another Ombra window (pid {0})", pid),
            ("ombra", null) => Loc.T("Open in another Ombra window"),
            ("omp", { } pid) => Loc.F("Open in omp in a terminal (pid {0})", pid),
            ("omp", null) => Loc.T("Open in omp in a terminal"),
            (_, { } pid) => Loc.F("Open in {0} (pid {1})", holder.Label ?? holder.Kind, pid),
            _ => Loc.F("Open elsewhere: {0}", holder.Label ?? holder.Kind),
        },
        _ => Loc.T("Open in another window"),
    };

    public string LeaseHint => _lease is { State: SessionLeaseState.Lost }
        ? Loc.T("It is read-only here now. Open here takes it back; a reply running in the other window is stopped.")
        : Loc.T("It is read-only here. Open here asks the other one to let go — a reply running there is stopped — then opens it in this window.");

    public bool IsTakingOver => _lease?.IsTakingOver == true;
    public string LeaseError => _lease?.Error is { } error ? Loc.T(error) : "";
    public bool HasLeaseError => LeaseError.Length > 0;

    private void ApplyLease(SessionSnapshot s)
    {
        if (Equals(s.Lease, _lease)) return;
        var wasLocked = IsLeaseReadOnly;
        _lease = s.Lease;
        OnPropertyChanged(nameof(LeaseBannerText));
        OnPropertyChanged(nameof(LeaseHint));
        OnPropertyChanged(nameof(IsTakingOver));
        OnPropertyChanged(nameof(LeaseError));
        OnPropertyChanged(nameof(HasLeaseError));
        OpenHereCommand.NotifyCanExecuteChanged();
        if (wasLocked == IsLeaseReadOnly) return;
        OnPropertyChanged(nameof(IsLeaseReadOnly));
        OnPropertyChanged(nameof(IsComposerLocked));
        OnPropertyChanged(nameof(ComposerHint));
        OnPropertyChanged(nameof(CanRecover));
        NotifyRecoverLayout();
        RecoverCommand.NotifyCanExecuteChanged();
    }

    private bool CanOpenHere() => _lease is { IsTakingOver: false };

    /// <summary>"Open here": the holder lets go (root <c>engine_take_session</c>; a reply running there is stopped), then the chat opens here.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenHere))]
    private async Task OpenHereAsync()
    {
        var session = Session;
        await session.TakeOverSessionAsync();
        if (ReferenceEquals(session, Session)) Apply(session.Snapshot());
    }
}
