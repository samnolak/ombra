using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// The recover card while omp restarts by itself after an unexpected exit (SessionController.AutoRecover.cs): a
/// countdown with the attempt, Restart now and Cancel. Cancel, or the attempt limit, leaves the ordinary manual card.
/// </summary>
public sealed partial class MainViewModel
{
    private DispatcherTimer? _autoRecoverTimer;

    private AutoRecoverStatus? AutoRecoverShown => CanRecover ? _open.Last?.AutoRecover : null;

    /// <summary>omp stopped unexpectedly and the shown chat restarts by itself when the countdown ends.</summary>
    public bool IsAutoRecovering => AutoRecoverShown is not null;

    public string AutoRecoverText => AutoRecoverShown is { } status
        ? Loc.F("Restarting in {0} s (attempt {1} of {2}). Unsent input is kept and nothing is resent.",
            (int)Math.Ceiling(status.Remaining.TotalSeconds), status.Attempt, status.MaxAttempts)
        : "";

    /// <summary>Keeps omp stopped: the card offers the manual restart instead.</summary>
    [RelayCommand(CanExecute = nameof(IsAutoRecovering))]
    private void CancelAutoRecover()
    {
        var open = _open;
        open.Controller.CancelAutoRecover();
        ApplyIfShown(open);
    }

    /// <summary>Called from ApplyModelInfo when the recover state changed.</summary>
    private void ApplyAutoRecover()
    {
        OnPropertyChanged(nameof(IsAutoRecovering));
        OnPropertyChanged(nameof(AutoRecoverText));
        CancelAutoRecoverCommand.NotifyCanExecuteChanged();
        if (IsAutoRecovering && !_cts.IsCancellationRequested)
        {
            _autoRecoverTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => TickAutoRecover());
            _autoRecoverTimer.Start();
        }
        else _autoRecoverTimer?.Stop();
    }

    private void TickAutoRecover()
    {
        if (!IsAutoRecovering || _cts.IsCancellationRequested)
        {
            _autoRecoverTimer?.Stop();
            return;
        }
        OnPropertyChanged(nameof(AutoRecoverText));
        OnPropertyChanged(nameof(RecoverHint));
    }
}
