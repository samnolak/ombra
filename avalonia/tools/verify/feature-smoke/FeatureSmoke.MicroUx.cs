#if OMBRA_FEATURE_SMOKE
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Ombra.App.Platform.Mac;
using Ombra.App.ViewModels;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] MicroUxSections() => [("micro-ux-attention", MicroUxAttention)];

        private async Task MicroUxAttention()
        {
            await Send("SGRCALL show readable styled output");
            await ApproveThroughCard("40-sgr-approval");
            Check("sgr-command-finished", await Until(() => !vm.IsRunning && vm.Rows.OfType<ToolRowViewModel>().Any(r => r.Output.Contains("SGR red text")), 60));
            var tool = vm.Rows.OfType<ToolRowViewModel>().Last(r => r.Output.Contains("SGR red text"));
            tool.IsExpanded = true;
            foreach (var fold in vm.Rows.OfType<TurnFoldRowViewModel>()) fold.IsExpanded = true;
            foreach (var group in vm.Rows.OfType<StepGroupRowViewModel>()) group.IsExpanded = true;
            await Task.Delay(500);
            Check("sgr-tool-readable", tool.ConsoleOutput.Contains("SGR red text") && !tool.ConsoleOutput.Contains('\u001b') && !tool.ConsoleOutput.Contains('␛'), tool.ConsoleOutput);
            Shot("40-sgr-readable");

            var requests = 0;
            void Changed(int count, bool request) { if (request) requests++; }
            vm.DockAttentionChanged += Changed;
            var app = MacObjC.Send(MacObjC.Cls("NSApplication"), "sharedApplication");
            string? Badge() => MacObjC.ToStr(MacObjC.Send(MacObjC.Send(app, "dockTile"), "badgeLabel"));
            try
            {
                vm.NotificationsEnabled = true;
                await Send("ASKCALL ask me two things");
                MacObjC.SendVoid(app, "hide:", 0);
                vm.SetWindowActive(false);
                Check("dock-question-waits", await Until(() => vm.CurrentDialog?.Ask is not null && vm.DockWaitingCount == 1, 60));
                Check("dock-native-badge-one-chat", Badge() == "1", Badge() ?? "null");
                Check("dock-informational-once", requests == 1, requests.ToString());
                vm.Apply(vm.Session.Snapshot());
                Check("dock-repeat-snapshot-no-bounce", requests == 1, requests.ToString());
                MacObjC.SendVoid(app, "unhideWithoutActivation");
                window.Show();
                await Task.Delay(500);
                Shot("41-dock-waiting-question");
                if (CanShoot(Path.Combine(dir, "41-dock-desktop.png")))
                    using (var capture = Process.Start(new ProcessStartInfo("/usr/sbin/screencapture", ["-x", Path.Combine(dir, "41-dock-desktop.png")]))!) capture.WaitForExit();
                vm.NotificationsEnabled = false;
                Check("dock-settings-clear-badge", string.IsNullOrEmpty(Badge()) && vm.DockWaitingCount == 0, Badge() ?? "null");
                vm.NotificationsEnabled = true;
                Check("dock-settings-restore-no-bounce", Badge() == "1" && requests == 1, $"badge={Badge()} requests={requests}");
                Activate();
                vm.CurrentDialog!.DismissCommand.Execute(null);
                Check("dock-answer-clears-badge", await Until(() => vm.CurrentDialog is null && vm.DockWaitingCount == 0 && string.IsNullOrEmpty(Badge()), 30), Badge() ?? "null");
                await Task.Delay(500);
                Shot("41-dock-cleared");
            }
            finally
            {
                vm.DockAttentionChanged -= Changed;
                MacObjC.SendVoid(app, "unhideWithoutActivation");
                window.Show();
                Activate();
            }
        }
    }
}
#endif
