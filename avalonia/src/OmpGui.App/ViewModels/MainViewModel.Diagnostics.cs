using CommunityToolkit.Mvvm.Input;
using OmpGui.App.Services;

namespace OmpGui.App.ViewModels;

/// <summary>Settings → Diagnostics: a redacted zip for bug reports (see <see cref="DiagnosticsBundle"/>).</summary>
public sealed partial class MainViewModel
{
    /// <summary>The view asks where to save (suggested file name) and returns the opened file, or null when cancelled.</summary>
    public event Func<string, Task<Stream?>>? SaveFileRequested;

    [RelayCommand]
    private async Task SaveDiagnosticsAsync()
    {
        var name = $"omp-gui-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        try
        {
            if (SaveFileRequested is not { } ask || await ask(name) is not { } stream) return;
            await using (stream)
            {
                await new DiagnosticsBundle
                {
                    SettingsPath = _settings?.Path ?? OmpGui.ClientCore.OmpRuntimeOptions.DefaultConfigPath,
                    Session = Session.Snapshot(),
                    Installer = RuntimeInstaller,
                    StartupLogPath = _args.StartupLogPath,
                    ClientLog = ClientLog,
                }.WriteAsync(stream, _cts.Token);
            }
            SettingsMessage = "Diagnostics saved. They hold versions, settings with secrets removed, omp's state and the app's own errors — no conversation text.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SettingsMessage = "Could not save diagnostics: " + e.Message;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
    }

    /// <summary>Where the app's own errors go (<see cref="Services.ClientLog"/>); tests point it at a folder of their own.</summary>
    public ClientLog ClientLog { get; init; } = ClientLog.Shared;

    private bool _clientErrorShown;

    /// <summary>
    /// An error escaped a UI handler and was logged instead of closing the app. Said once, without interrupting: the run
    /// and the conversation carry on, and the details are what a bug report needs.
    /// </summary>
    internal void ReportClientError(Exception error)
    {
        ClientLog.Write("ui", error);
        if (_clientErrorShown) return;
        _clientErrorShown = true;
        ComposerMessage = "Something went wrong in OMP GUI; it carried on. Settings → Diagnostics saves the details for a bug report.";
    }
}
