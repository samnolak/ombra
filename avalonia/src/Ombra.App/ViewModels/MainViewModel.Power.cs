using Ombra.App.Localization;
using Ombra.App.Platform.Linux;
using Ombra.App.Platform.Mac;
using Ombra.App.Platform.Windows;

namespace Ombra.App.ViewModels;

public sealed partial class MainViewModel
{
    private IDisposable? _powerNotifications;
    private Task? _powerSubscription;
    private int _powerFailed;
    public bool PowerNotificationsSubscribed => _powerNotifications is not null && Volatile.Read(ref _powerFailed) == 0 && _disposed == 0;
    public string? PowerNotificationsError { get; private set; }

    /// <summary>Real native subscription, on the window's UI thread. Also the native smoke entrypoint.</summary>
    public Task EnablePowerNotificationsAsync() => _powerSubscription ??= SubscribePowerAsync();

    private async Task SubscribePowerAsync()
    {
        if (_disposed != 0) return;
        try
        {
            IDisposable subscription;
            if (OperatingSystem.IsMacOS()) subscription = new MacPowerNotifications(OnPowerChanged);
            else if (OperatingSystem.IsWindows()) subscription = new WindowsPowerNotifications(OnPowerChanged);
            else if (OperatingSystem.IsLinux()) subscription = await LinuxPowerNotifications.SubscribeAsync(OnPowerChanged, OnPowerUnavailable);
            else throw new PlatformNotSupportedException("This operating system has no supported sleep/wake notification API.");
            if (_disposed != 0 || Volatile.Read(ref _powerFailed) != 0) { subscription.Dispose(); return; }
            _powerNotifications = subscription;
            OnPropertyChanged(nameof(PowerNotificationsSubscribed));
        }
        catch (Exception error) { OnPowerUnavailable(error.Message); }
    }

    private void OnPowerChanged(bool sleeping) => UiDispatcher.Post(async () =>
    {
        if (_disposed != 0) return;
        if (sleeping) _host.NotifySuspending();
        else
        {
            try { await _host.ReconcileAfterResumeAsync(); }
            catch (Exception error) { if (_disposed == 0) ReportClientError(error); }
        }
    });

    private void OnPowerUnavailable(string error)
    {
        Interlocked.Exchange(ref _powerFailed, 1); // A background failure must win over the subscription continuation.
        UiDispatcher.Post(() =>
        {
            if (_disposed != 0) return;
            DisposePowerNotifications();
            PowerNotificationsError = Loc.F("Sleep/wake notifications are unavailable: {0}", error);
            OnPropertyChanged(nameof(PowerNotificationsSubscribed));
            OnPropertyChanged(nameof(PowerNotificationsError));
            ComposerMessage = Controls.VisibleText.FromUntrustedOutput(PowerNotificationsError + " "
                + Loc.T("Automatic wake reconciliation is not active; check the engine connection after sleep."));
        });
    }

    private void DisposePowerNotifications()
    {
        _powerNotifications?.Dispose();
        _powerNotifications = null;
    }
}
