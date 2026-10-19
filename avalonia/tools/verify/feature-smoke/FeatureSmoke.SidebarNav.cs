#if OMBRA_FEATURE_SMOKE
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] SidebarNavSections() => [("sidebar-nav", SidebarNav)];

        private string? ShownFile() => vm.Session.SessionFile;

        /// <summary>
        /// Three chats: ⌘[ / ⌘] walk back and forward through them, ⌘1 opens the sidebar's first row; one is archived
        /// (folded Archived group, then Unarchive all); the Working chip keeps only the chat whose reply is running and
        /// ⌥⌘A finds nothing waiting. Native NSEvents through the app's own key handling.
        /// </summary>
        private async Task SidebarNav()
        {
            var work = vm.Session.CurrentLaunch.WorkingDirectory ?? Environment.CurrentDirectory;
            var files = new List<string>();
            foreach (var word in new[] { "kestrel", "heron" })
            {
                await vm.OpenFolderInNewSessionAsync(work);
                await Until(() => vm.Phase == SessionPhase.Ready, 60);
                await Prompt($"A {word} on the sidebar");
                await Until(() => ShownFile() is not null, 20);
                files.Add(ShownFile()!);
            }
            await Until(() => files.All(f => vm.Sessions.Any(s => s.Model.Path == f)), 20);
            Activate();
            FocusComposer();

            KeyCmd("[", 0x21);
            Check("nav-back", await Until(() => ShownFile() is { } f && f != files[1], 20), $"shown={ShownFile()}");
            var back = ShownFile();
            KeyCmd("]", 0x1E);
            Check("nav-forward", await Until(() => ShownFile() == files[1] && vm.Phase == SessionPhase.Ready, 20), $"shown={ShownFile()} back={back}");

            var rows = vm.SessionGroups.Where(g => g.IsExpanded).SelectMany(g => g.Items).ToList();
            var second = rows.Count > 1 ? rows[1].Model.Path : null;
            KeyCmd("2", 0x13);
            Check("nav-cmd-2", await Until(() => ShownFile() == second && vm.Phase == SessionPhase.Ready, 20), $"shown={ShownFile()} want={second}");

            var row = vm.Sessions.First(s => s.Model.Path == files[0]);
            row.ToggleArchiveCommand!.Execute(row);
            var archive = vm.SessionGroups.LastOrDefault();
            Check("archive-group", archive is { IsArchiveGroup: true, IsExpanded: false } && archive.Items.Contains(row) && File.Exists(files[0]),
                $"groups=[{string.Join(" | ", vm.SessionGroups.Select(g => $"{g.Name}:{g.Items.Count}:{g.IsExpanded}"))}]");
            await Task.Delay(400);
            Shot("40-archive-folded-dark");
            archive?.ToggleExpandedCommand.Execute(null);
            await Task.Delay(400);
            Shot("41-archive-open-dark");
            archive?.UnarchiveAllCommand?.Execute(null);
            Check("archive-unarchive-all", !vm.Sessions.Any(s => s.IsArchived) && !vm.SessionGroups.Any(g => g.IsArchiveGroup));

            await Send("SLOWSHORT working chip");
            Check("chip-running", await Until(() => vm.IsRunning, 15), $"phase={vm.Phase}");
            var running = ShownFile();
            vm.IsSessionSearchOpen = true;
            vm.SetStatusFilterCommand.Execute(SessionStatusFilter.Working);
            Check("chip-working", await Until(() => vm.Sessions.Select(s => s.Model.Path).SequenceEqual([running!]), 10),
                $"rows=[{string.Join(" | ", vm.Sessions.Select(s => s.Title))}]");
            await Task.Delay(400);
            Shot("42-status-chip-working-dark");
            Key("a", 0x00, Cmd | Alt);
            await Task.Delay(300);
            Check("next-waiting-none", ShownFile() == running, $"shown={ShownFile()}");
            vm.SetStatusFilterCommand.Execute(SessionStatusFilter.Working);
            vm.IsSessionSearchOpen = false;
            Check("chip-cleared", vm.StatusFilter == SessionStatusFilter.All && vm.Sessions.Count >= 3, $"rows={vm.Sessions.Count}");
            await Until(() => !vm.IsRunning, 60);
        }
    }
}
#endif
