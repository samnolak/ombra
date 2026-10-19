using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>Fork from here / Fork in the window, and the agent reading a terminal tab (<c>terminal_read</c>).</summary>
public sealed class ForkHostToolsUiTests
{
    private static async Task Until(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static (MainViewModel Vm, MainWindow Window) Open(string scenario)
    {
        var s = new SessionController(TestProcesses.FakeFactory(scenario, TestProcesses.TempDir("forkui-sessions")), new LaunchRequest(TestProcesses.TempDir("forkui-project")));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
        w.Show();
        vm.OnWindowOpened();
        return (vm, w);
    }

    private static async Task SendAsync(MainViewModel vm, string text, int replies)
    {
        vm.ComposerText = text;
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<AssistantRowViewModel>().Count(r => !r.Streaming) == replies, "reply " + replies);
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [AvaloniaFact]
    public async Task Fork_from_a_reply_shows_the_new_session_up_to_that_reply()
    {
        var (vm, w) = Open("normal");
        try
        {
            await Until(() => vm.Phase == SessionPhase.Ready, "ready");
            await SendAsync(vm, "first question", 1);
            await SendAsync(vm, "second question", 2);
            var original = vm.Session.SessionFile;
            var first = vm.Rows.OfType<AssistantRowViewModel>().First();
            Assert.True(vm.ForkFromCommand.CanExecute(first));

            await vm.ForkFromCommand.ExecuteAsync(first);
            await Until(() => vm.SessionCard?.State == SessionCardState.Done, "fork card");
            Assert.Equal(Loc.T("Forked"), vm.SessionCard!.Title);
            Assert.NotEqual(original, vm.Session.SessionFile);
            await Until(() => vm.Rows.OfType<UserRowViewModel>().Select(u => u.Text).SequenceEqual(["first question"]), "conversation up to the reply");
            Assert.Single(vm.Rows.OfType<AssistantRowViewModel>());

            // The session menu's Fork (and /fork): the whole conversation in another new session
            vm.CloseSessionCardCommand.Execute(null);
            var forked = vm.Session.SessionFile;
            vm.ComposerText = "/fork";
            vm.SendCommand.Execute(null);
            await Until(() => vm.SessionCard is { Kind: "fork", State: SessionCardState.Done }, "second fork card");
            Assert.NotEqual(forked, vm.Session.SessionFile);
            Assert.Equal(["first question"], vm.Rows.OfType<UserRowViewModel>().Select(u => u.Text));
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public async Task A_busy_omp_refuses_the_fork_with_a_message()
    {
        var (vm, w) = Open("fork-busy");
        try
        {
            await Until(() => vm.Phase == SessionPhase.Ready, "ready");
            await SendAsync(vm, "question", 1);
            var original = vm.Session.SessionFile;
            await vm.ForkSessionCommand.ExecuteAsync(null);
            await Until(() => vm.SessionCard?.State == SessionCardState.Failed, "refusal card");
            Assert.Equal(Loc.T("Not forked"), vm.SessionCard!.Title);
            Assert.Equal(Loc.T("omp is still working (a reply, a command or a background task). Fork once it is idle."), vm.SessionCard.Message);
            Assert.Equal(original, vm.Session.SessionFile);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public async Task The_agent_reads_the_terminal_tab_the_user_has_open()
    {
        var (vm, w) = Open("normal");
        try
        {
            await Until(() => vm.Phase == SessionPhase.Ready, "ready");
            var none = vm.ReadTerminalForAgent(Args("{}"));
            Assert.True(none.IsError);

            vm.ToggleTerminalCommand.Execute(null);
            var tab = Assert.Single(vm.Terminals);
            var term = w.TerminalOf(tab)!;
            await Until(() => term.IsLive || tab.HasExited, "shell started");
            Assert.False(tab.HasExited, tab.Status);
            await term.SendInputAsync("echo ombra-termread-$((40+2))\r", CancellationToken.None);
            await Until(() => term.FindInBuffer("ombra-termread-42", default) >= 1, "echoed output");

            var read = vm.ReadTerminalForAgent(Args("{}"));
            Assert.False(read.IsError, read.Text);
            Assert.StartsWith("Terminal tab 1 of 1: ", read.Text);
            Assert.Contains("ombra-termread-42", read.Text);
            Assert.Contains("(shell, running, selected)", read.Text);
            // Bounded: one line asked, one line given (the prompt line after the output)
            var one = vm.ReadTerminalForAgent(Args("""{"lines":1}"""));
            Assert.Contains("--- last 1 lines ---", one.Text);
            Assert.True(vm.ReadTerminalForAgent(Args("""{"tab":3}""")).IsError);

            // A second tab: tabs are listed and chosen by number
            vm.NewShellCommand.Execute(null);
            await Until(() => vm.Terminals.Count == 2, "second tab");
            var byNumber = vm.ReadTerminalForAgent(Args("""{"tab":1}"""));
            Assert.StartsWith("Terminal tab 1 of 2: ", byNumber.Text);
            Assert.Contains("ombra-termread-42", byNumber.Text);
            Assert.Contains("Open tabs:", byNumber.Text);

            // The agent's own call (omp's host_tool_call) reaches the same tab and comes back in the reply
            vm.SelectTerminalCommand.Execute(tab);
            vm.ComposerText = "TERMREAD what is in my terminal";
            vm.SendCommand.Execute(null);
            await Until(() => vm.Phase == SessionPhase.Ready
                && vm.Rows.OfType<AssistantRowViewModel>().Any(r => r.Text.Contains("ombra-termread-42", StringComparison.Ordinal)), "reply with the terminal's text");
        }
        finally
        {
            w.Close();
            Dispatcher.UIThread.RunJobs();
            if (vm.SessionCard?.Kind == "quit") vm.SessionCard.Primary!.Command.Execute(null);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
