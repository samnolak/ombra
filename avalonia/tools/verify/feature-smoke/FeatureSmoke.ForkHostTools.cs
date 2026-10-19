#if OMBRA_FEATURE_SMOKE
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] ForkHostToolsSections() => [("fork", Fork), ("terminal-read", TerminalRead)];

        /// <summary>The buttons of a control's open flyout (its content is hosted in the flyout's own popup window).</summary>
        private static IEnumerable<Button> FlyoutButtons(Avalonia.Controls.Primitives.FlyoutBase? flyout) =>
            flyout is Flyout { IsOpen: true, Content: Control content }
                ? content.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible)
                : [];

        /// <summary>
        /// Fork from here on a reply (its right-click menu), then Fork in the chat menu, against the real engine's
        /// <c>fork</c>; then a fork while omp runs a shell command: omp refuses (<c>session_busy</c>) and the card says why.
        /// </summary>
        private async Task Fork()
        {
            await Prompt("fork smoke alpha");
            await Prompt("fork smoke beta");
            var original = vm.Session.SessionFile;
            var alpha = vm.Rows.OfType<AssistantRowViewModel>().LastOrDefault(r => r.Text.Contains("fork smoke alpha", StringComparison.Ordinal));
            if (!Check("fork-alpha-reply", alpha is not null, $"rows={vm.Rows.Count}")) return;
            var panel = window.GetVisualDescendants().OfType<StackPanel>()
                .FirstOrDefault(p => p.Classes.Contains("assistant") && ReferenceEquals(p.DataContext, alpha) && p.IsEffectivelyVisible);
            if (!Check("fork-alpha-row-shown", panel is not null)) return;
            Activate();
            RightClick(panel!);
            Button? item = null;
            Check("fork-from-here-in-menu", await Until(() => (item = FlyoutButtons(panel!.ContextFlyout).FirstOrDefault(b => ReferenceEquals(b.Command, vm.ForkFromCommand))) is not null, 5),
                "menu: " + string.Join(" | ", FlyoutButtons(panel!.ContextFlyout).Select(Label)));
            await Task.Delay(300);
            ShotScreenRegion("40-fork-from-here-menu-dark");
            if (item is null) return;
            Click(item);
            var forked = await Until(() => vm.SessionCard is { Kind: "fork", State: SessionCardState.Done } && vm.Phase == SessionPhase.Ready, 30);
            Check("fork-from-here-done", forked && vm.SessionCard!.Title == Loc.T("Forked"), $"card='{vm.SessionCard?.Title}: {vm.SessionCard?.Message}' notices=[{Notices()}]");
            Check("fork-new-session-file", vm.Session.SessionFile is { } f && f != original && File.Exists(original!), $"{original} -> {vm.Session.SessionFile}");
            Check("fork-ends-at-reply", await Until(() => UserRowsContain("fork smoke alpha") && !UserRowsContain("fork smoke beta"), 10),
                "users=[" + string.Join(" | ", vm.Rows.OfType<UserRowViewModel>().Select(u => u.Text)) + "]");
            await Task.Delay(400);
            Shot("41-forked-from-reply-dark");

            // The chat menu's Fork: the whole conversation in a new file
            vm.CloseSessionCardCommand.Execute(null);
            var before = vm.Session.SessionFile;
            var menu = window.GetVisualDescendants().OfType<Ombra.App.Views.Session.SessionMenuButton>()
                .Select(m => m.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "MenuButton")).FirstOrDefault(b => b is { IsEffectivelyVisible: true });
            if (!Check("chat-menu-button", menu is not null)) return;
            Click(menu!);
            Button? fork = null;
            Check("chat-menu-fork-item", await Until(() => (fork = FlyoutButtons(menu!.Flyout).FirstOrDefault(b => b.Name == "ForkItem")) is not null, 5));
            await Task.Delay(300);
            ShotScreenRegion("42-chat-menu-fork-dark");
            if (fork is null) return;
            Click(fork);
            var whole = await Until(() => vm.SessionCard is { Kind: "fork", State: SessionCardState.Done } && vm.Session.SessionFile != before, 30);
            Check("fork-session-done", whole && UserRowsContain("fork smoke alpha"), $"card='{vm.SessionCard?.Title}' file={vm.Session.SessionFile}");

            // omp is busy with a shell command it runs for the user: the engine refuses the fork (session_busy)
            vm.CloseSessionCardCommand.Execute(null);
            await Send("!sleep 6");
            await Until(() => vm.SessionCard is { Kind: "shell" }, 5);
            await Task.Delay(500);
            vm.CloseSessionCardCommand.Execute(null); // the shell's card would keep the menu's actions disabled
            var busyFile = vm.Session.SessionFile;
            Click(menu!);
            fork = null;
            await Until(() => (fork = FlyoutButtons(menu!.Flyout).FirstOrDefault(b => b.Name == "ForkItem")) is { IsEffectivelyEnabled: true }, 5);
            if (fork is not null) Click(fork);
            var refused = await Until(() => vm.SessionCard is { Kind: "fork", State: SessionCardState.Failed }, 20);
            Check("fork-busy-refused", refused && vm.SessionCard!.Message == Loc.T("omp is still working (a reply, a command or a background task). Fork once it is idle.")
                && vm.Session.SessionFile == busyFile, $"card='{vm.SessionCard?.Title}: {vm.SessionCard?.Message}' file={vm.Session.SessionFile}");
            Check("fork-busy-from-engine", vm.Session.Snapshot().DebugTail.Any(d => d.Type == "fork" && d.Summary.StartsWith("refused", StringComparison.Ordinal)),
                string.Join(" ; ", vm.Session.Snapshot().DebugTail.Where(d => d.Type == "fork").Select(d => d.Summary)));
            await Task.Delay(300);
            Shot("43-fork-busy-dark");
            await Until(() => vm.Rows.OfType<ToolRowViewModel>().Any(t => t.Summary.Contains("sleep 6", StringComparison.Ordinal) && !t.IsRunning), 20);
            vm.CloseSessionCardCommand.Execute(null);
        }

        /// <summary>The agent reads the user's terminal tab: a shell where the user typed a command, the model's
        /// <c>terminal_read</c> call (Ombra's host tool), approved on its card, and the reply that quotes the tab.</summary>
        private async Task TerminalRead()
        {
            vm.ToggleTerminalCommand.Execute(null);
            var tab = vm.Terminals.LastOrDefault();
            var term = tab is null ? null : ((MainWindow)window).TerminalOf(tab);
            if (!Check("terminal-open", term is not null && await Until(() => term.IsLive, 20), $"tabs={vm.Terminals.Count}")) return;
            await Task.Delay(800);
            Click(term!);
            await Task.Delay(300);
            // Into the shell as written (posted key codes go through the desk's keyboard layout, a Russian one types Cyrillic)
            await term!.SendInputAsync("echo smoke-termread-$((70+7))\r", CancellationToken.None);
            Check("terminal-echo", await Until(() => term.FindInBuffer("smoke-termread-77", default) >= 1, 10));
            await Task.Delay(300);
            Shot("50-terminal-dark");

            await Send("TERMREAD what is in my terminal");
            if (await Until(() => vm.CurrentDialog is not null, 20)) await ApproveThroughCard("51-terminal-read-approval");
            var replied = await Until(() => !vm.IsRunning && AssistantRowsContain("TERMREAD done"), 90);
            Check("terminal-read-reply", replied && AssistantRowsContain("smoke-termread-77"),
                $"reply='{vm.Rows.OfType<AssistantRowViewModel>().LastOrDefault()?.Text}' notices=[{Notices()}]");
            Check("terminal-read-tool-row", vm.Rows.OfType<ToolRowViewModel>().Any(t => t.Name == "terminal_read"));
            await Task.Delay(400);
            Shot("52-terminal-read-reply-dark");
            vm.CloseTerminalCommand.Execute(tab);
            await Task.Delay(300);
        }
    }
}
#endif
