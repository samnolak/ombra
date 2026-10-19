using Avalonia.Headless.XUnit;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>The recover card during an automatic restart (PARITY_ROADMAP O4): countdown, Cancel, then manual recovery.</summary>
public sealed class AutoRecoverUiTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [AvaloniaFact]
    public async Task Countdown_card_offers_cancel_and_a_restart_never_sends_the_draft()
    {
        var fixture = new AutoRecoverTests.Fixture("auto-recover-ui");
        var settings = new ClientSettingsStore(Path.Combine(fixture.Root, "config.json"));
        var session = fixture.Controller();
        var vm = new MainViewModel(session, new AppArgs(), settings: settings);
        try
        {
            await session.StartAsync();
            vm.Apply(session.Snapshot());
            vm.ComposerText = "draft kept";

            await AutoRecoverTests.CrashEngineAsync(session);
            vm.Apply(await AutoRecoverTests.CountdownAsync(session, 1));
            Assert.True(vm.CanRecover);
            Assert.True(vm.IsAutoRecovering);
            Assert.True(vm.CancelAutoRecoverCommand.CanExecute(null));
            Assert.Equal(Loc.T("omp stopped unexpectedly"), vm.RecoverTitle);
            Assert.Contains(Loc.F("Restarting in {0} s (attempt {1} of {2}). Unsent input is kept and nothing is resent.", 1, 1, 3), vm.RecoverHint);
            Assert.Equal(Loc.T("Restart now"), vm.RecoverLabel);

            // Cancel keeps omp stopped: the ordinary manual card, and the countdown never fires.
            vm.CancelAutoRecoverCommand.Execute(null);
            Assert.False(vm.IsAutoRecovering);
            Assert.False(vm.CancelAutoRecoverCommand.CanExecute(null));
            Assert.Equal(Loc.T("omp stopped"), vm.RecoverTitle);
            Assert.Equal(Loc.T("Restart omp"), vm.RecoverLabel);
            fixture.Clock.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(300);
            Assert.Equal(SessionPhase.Faulted, session.Snapshot().Phase);
            Assert.Equal(1, fixture.EngineStarts);

            await vm.RecoverCommand.ExecuteAsync(null);
            Assert.Equal(SessionPhase.Ready, vm.Phase);

            // A countdown that runs out brings the chat back by itself; the draft stays in the message box, unsent.
            await AutoRecoverTests.CrashEngineAsync(session);
            vm.Apply(await AutoRecoverTests.CountdownAsync(session, 1));
            Assert.True(vm.IsAutoRecovering);
            fixture.Clock.Advance(TimeSpan.FromSeconds(0.5));
            vm.Apply(await TestProcesses.Eventually(session.Snapshot, s => s.Phase == SessionPhase.Ready, Wait, "restarted by itself"));
            Assert.False(vm.IsAutoRecovering);
            Assert.False(vm.CanRecover);
            Assert.Equal(SessionPhase.Ready, vm.Phase);
            Assert.Equal("draft kept", vm.ComposerText);
            Assert.Equal(3, fixture.EngineStarts);
            Assert.DoesNotContain(fixture.Inputs, line => line.Contains("draft kept", StringComparison.Ordinal));
        }
        finally { await vm.DisposeAsync(); }
    }
}
