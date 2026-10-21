using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>
/// A thinking level picked while omp works belongs to the message queued next: the current run keeps its level, and
/// Ombra sets the new one right before omp starts that message (omp's prompt and follow_up take no level).
/// </summary>
public sealed class QueuedThinkingTests
{
    private static async Task<(MainWindow w, MainViewModel vm, SessionController s, string log)> OpenAsync()
    {
        var project = TestProcesses.TempDir("qt-project");
        var log = Path.Combine(project, "thinking.log");
        var factory = TestProcesses.FakeFactory("deferred-input", TestProcesses.TempDir("qt-sessions"));
        var s = new SessionController(request =>
        {
            var spec = factory(request);
            return spec with { Environment = new Dictionary<string, string?>(spec.Environment)
            {
                ["FAKE_OMP_THINKING_LOG"] = log,
                // LONG: 200 deltas 25 ms apart, time enough to change the level and queue while it runs
                ["FAKE_OMP_LONG_DELTAS"] = "200",
            } };
        }, new LaunchRequest(project));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready && vm.SelectedThinking == "medium", "ready");
        return (w, vm, s, log);
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static string[] Log(string log) => File.Exists(log) ? File.ReadAllLines(log) : [];

    private static void Queue(MainWindow w, string text)
    {
        w.FindControl<TextBox>("Composer")!.Focus();
        w.KeyTextInput(text);
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
    }

    private static async Task StartLongRun(MainWindow w, MainViewModel vm)
    {
        vm.ComposerText = "LONG";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Running && vm.Rows.OfType<AssistantRowViewModel>().Any(), "running");
    }

    [AvaloniaFact]
    public async Task A_level_picked_while_omp_works_applies_to_the_queued_message_not_the_current_run()
    {
        var (w, vm, s, log) = await OpenAsync();
        try
        {
            await StartLongRun(w, vm);

            // Queued before the change: omp's own follow-up, still at the run's level.
            Queue(w, "before the change");
            await Until(() => vm.QueuedMessages.Count == 1, "first queued");

            vm.SetThinkingCommand.Execute("high");
            await Until(() => s.Snapshot().NextThinkingLevel == "high", "level kept for the next message");
            vm.Apply(s.Snapshot());
            Assert.Equal("high", vm.SelectedThinking);
            Assert.Equal("medium", s.Snapshot().ThinkingLevel);

            Queue(w, "after the change");
            await Until(() => vm.QueuedMessages.Count == 2, "second queued");
            var queued = vm.QueuedMessages[1];
            Assert.Equal(QueueKind.DeferredFollowUp, queued.Model.Kind);
            Assert.Equal("high", queued.Model.ThinkingLevel);
            Assert.Equal("High", queued.ThinkingLabel);
            Assert.False(vm.QueuedMessages[0].HasThinking);
            var tags = w.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "QueuedThinkingTag").ToArray();
            Assert.Equal([false, true], tags.Select(t => t.IsEffectivelyVisible));
            Assert.Equal(SessionPhase.Running, vm.Phase);
            MicroUxTabTests.Shot(w, "queued-thinking-chip");
            // Nothing reached omp while its run goes on.
            Assert.DoesNotContain(Log(log), l => l.StartsWith("set_thinking_level", StringComparison.Ordinal));

            await Until(() => vm.Phase == SessionPhase.Ready && vm.QueuedMessages.Count == 0
                && vm.Rows.OfType<UserRowViewModel>().Count() == 3 && s.Snapshot().NextThinkingLevel is null, "both delivered", 40);
            Assert.Equal(["LONG", "before the change", "after the change"], vm.Rows.OfType<UserRowViewModel>().Select(u => u.Text));
            Assert.Equal(
                ["turn LONG thinking=medium", "turn before the change thinking=medium", "set_thinking_level high idle", "turn after the change thinking=high"],
                Log(log));
            Assert.Equal("high", s.Snapshot().ThinkingLevel);
            Assert.Equal("high", vm.SelectedThinking);
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task Editing_a_queued_message_brings_its_level_back_and_requeueing_keeps_it()
    {
        var (w, vm, s, log) = await OpenAsync();
        try
        {
            await StartLongRun(w, vm);
            vm.SetThinkingCommand.Execute("high");
            await Until(() => s.Snapshot().NextThinkingLevel == "high", "high kept");
            Queue(w, "edit me");
            await Until(() => vm.QueuedMessages.Count == 1, "queued");
            Assert.Equal("high", vm.QueuedMessages[0].Model.ThinkingLevel);

            // Another level for later messages; the queued one keeps its own.
            vm.SetThinkingCommand.Execute("low");
            await Until(() => s.Snapshot().NextThinkingLevel == "low", "low kept");
            Assert.Equal("high", s.Snapshot().Queued[0].ThinkingLevel);

            // Edit: the text returns to the box and the picker returns to the message's level.
            await vm.QueuedMessages[0].EditCommand.ExecuteAsync(null);
            await Until(() => vm.ComposerText == "edit me" && vm.QueuedMessages.Count == 0, "back in the box");
            Assert.Equal("high", s.Snapshot().NextThinkingLevel);
            Assert.Equal("high", vm.SelectedThinking);

            vm.ComposerText = "edited";
            w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await Until(() => vm.QueuedMessages.Count == 1, "queued again");
            Assert.Equal("high", vm.QueuedMessages[0].Model.ThinkingLevel);

            // ×: withdrawn without sending; the run and the rest of the queue are untouched.
            vm.SetThinkingCommand.Execute("low");
            await Until(() => s.Snapshot().NextThinkingLevel == "low", "low kept again");
            Queue(w, "drop me");
            await Until(() => vm.QueuedMessages.Count == 2, "two queued");
            Assert.Equal("low", vm.QueuedMessages[1].Model.ThinkingLevel);
            await vm.QueuedMessages[1].RemoveCommand.ExecuteAsync(null);
            Assert.Equal(["edited"], vm.QueuedMessages.Select(q => q.Text));

            await Until(() => vm.Phase == SessionPhase.Ready && vm.QueuedMessages.Count == 0
                && vm.Rows.OfType<UserRowViewModel>().Count() == 2 && s.Snapshot().NextThinkingLevel is null, "delivered", 40);
            Assert.Equal(["LONG", "edited"], vm.Rows.OfType<UserRowViewModel>().Select(u => u.Text));
            // The picker's last choice (low) becomes the session's once nothing waits.
            Assert.Equal(
                ["turn LONG thinking=medium", "set_thinking_level high idle", "turn edited thinking=high", "set_thinking_level low idle"],
                Log(log));
            Assert.Equal("low", s.Snapshot().ThinkingLevel);
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task Stop_returns_the_queued_message_with_its_level_and_sends_nothing()
    {
        var (w, vm, s, log) = await OpenAsync();
        try
        {
            await StartLongRun(w, vm);
            vm.SetThinkingCommand.Execute("high");
            await Until(() => s.Snapshot().NextThinkingLevel == "high", "high kept");
            Queue(w, "stop me");
            await Until(() => vm.QueuedMessages.Count == 1, "queued");
            vm.SetThinkingCommand.Execute("low");
            await Until(() => s.Snapshot().NextThinkingLevel == "low", "low kept");

            await vm.AbortCommand.ExecuteAsync(null);
            await Until(() => vm.Phase == SessionPhase.Ready && vm.QueuedMessages.Count == 0 && vm.ComposerText.Contains("stop me")
                && s.Snapshot().ThinkingLevel == "high" && s.Snapshot().NextThinkingLevel is null, "recovered with its level");
            Assert.Equal("high", vm.SelectedThinking);
            Assert.Equal(["LONG"], vm.Rows.OfType<UserRowViewModel>().Select(u => u.Text));
            // The run was stopped at its own level; the recovered message's level is the session's now, nothing was sent.
            Assert.Equal(["turn LONG thinking=medium", "set_thinking_level high idle"], Log(log));
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }
}
