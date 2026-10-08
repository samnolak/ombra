using Avalonia.Controls;
using Avalonia.Threading;

namespace OmpGui.App.Views;

/// <summary>
/// The close sequence. Every way out ends here: the close button, ⌘W and Window → Close, ⌘Q (Avalonia closes each
/// window through this before it shuts down) and the tray's Quit. Closing stops omp (stdin EOF, then the process tree is
/// killed after a grace period) and every shell tab, so while that would cost something the window asks first.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>omp and the shells are stopped: the next close goes through.</summary>
    private bool _closeConfirmed;

    /// <summary>The user chose Quit on the card (or nothing was at stake): stop everything without asking again.</summary>
    private bool _quitAccepted;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_closeConfirmed || Vm is not { } vm)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        // IsEnabled turns false once the stop has begun: a second close request just waits for it.
        if (!_quitAccepted && IsEnabled && vm.QuitWarning(_terminals.Values.Count(c => c.IsLive)) is { } warning)
        {
            // From the tray or ⌘Q the window may be minimized or behind others: the question has to be seen.
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();
            vm.AskBeforeQuit(warning, QuitWithoutAsking);
            // The keyboard lands on "Keep working": Enter or Space right after ⌘Q must not stop the run.
            Dispatcher.UIThread.Post(() => SessionCardHost.FindControl<Button>("CardSecondary")?.Focus(), DispatcherPriority.Loaded);
            return;
        }
        _quitAccepted = true;
        KillAllTerminals();
        if (IsEnabled)
        {
            IsEnabled = false;
            Title += " — closing";
        }
        try
        {
            await vm.DisposeAsync();
        }
        finally
        {
            _closeConfirmed = true;
            Close();
        }
    }

    /// <summary>Closes and stops everything: the user already said so (Quit on the card, Restart now for an update).</summary>
    private void QuitWithoutAsking()
    {
        _quitAccepted = true;
        Close();
    }
}
