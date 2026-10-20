#if OMBRA_FEATURE_SMOKE
using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App.Controls;
using Ombra.App.Nightly;
using Ombra.App.Platform.Mac;
using Ombra.App.ViewModels;
using Ombra.ClientCore;
using static Ombra.App.Platform.Mac.MacRuntime;

namespace Ombra.App.Smoke;

/// <summary>
/// Native macOS feature smoke, compiled into a build only through feature-smoke.targets (run.sh) and run
/// only when <c>OMBRA_FEATURE_SMOKE</c> names an output directory. Drives the real window: keys and clicks are NSEvents
/// posted to the app's own queue (<c>-[NSApp postEvent:atStart:]</c>, as NightlyAcceptance does: native menu key
/// equivalents, Avalonia's view and handlers; no Accessibility permission), screenshots with <c>screencapture</c>.
/// Results: <c>smoke.log</c> (PASS/FAIL per check). Env: <c>OMBRA_FEATURE_SMOKE_UNTRUSTED</c> (a folder with project
/// tools), <c>OMBRA_FEATURE_SMOKE_MARKER</c> (the file the project's extension writes when loaded),
/// <c>OMBRA_FEATURE_SMOKE_MOCKLOG</c> (the stand-in model's request log).
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class FeatureSmoke
{
    /// <summary>Compiled in by feature-smoke.targets only: waits for the main window, then runs the script once.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("OMBRA_FEATURE_SMOKE") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        RefuseUnlessIsolated(dir);
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(250);
                if (Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime) continue;
                var started = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if ((Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow
                        is not { IsVisible: true, DataContext: MainViewModel vm } window) return false;
                    _ = new Run(window, vm, dir).RunAsync();
                    return true;
                });
                if (started) return;
            }
        });
    }

    /// <summary>
    /// Before the app starts anything: the run's own settings (OMBRA_CONFIG, readable — an unreadable one makes the app
    /// fall back to the defaults, the user's omp profile and real model), an omp home and agent folder of its own, and
    /// the app's chats and teams in the run's folder. Otherwise the smoke writes why in smoke.log and the app exits.
    /// </summary>
    private static void RefuseUnlessIsolated(string dir)
    {
        var problems = new List<string>();
        if (Environment.GetEnvironmentVariable(OmpRuntimeOptions.ConfigEnvVar) is not { Length: > 0 } config)
            problems.Add($"{OmpRuntimeOptions.ConfigEnvVar} is not set: the app would use the user's settings");
        else
        {
            try
            {
                var launch = OmpRuntimeOptions.Load(config).ToLaunchSpec();
                problems.AddRange(Verify.HarnessIsolation.Problems(launch.Environment, Environment.GetEnvironmentVariable,
                    Environment.GetEnvironmentVariable(ChatFolders.RootEnvVar) ?? "", Environment.GetEnvironmentVariable("OMBRA_TEAMS_DIR") ?? ""));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                problems.Add($"the settings {config} cannot be read ({e.Message}): the app would run on the defaults, the user's omp profile");
            }
        }
        if (problems.Count == 0) return;
        File.WriteAllLines(Path.Combine(dir, "smoke.log"), [
            .. problems.Select(p => $"{DateTime.Now:HH:mm:ss.fff} FAIL isolation {p}"),
            $"{DateTime.Now:HH:mm:ss.fff} RESULT FAIL passed=0 failed={problems.Count} (refused to start: not isolated from the user's folders)",
        ]);
        Environment.Exit(3);
    }

    private sealed partial class Run(Window window, MainViewModel vm, string dir)
    {
        private readonly StreamWriter _log = new(Path.Combine(dir, "smoke.log")) { AutoFlush = true };
        private int _failures, _passes;
        private const ulong Cmd = 1 << 20, Shift = 1 << 17, Alt = 1 << 19;

        private void Log(string line) => _log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");

        private bool Check(string name, bool pass, string detail = "")
        {
            Log($"{(pass ? "PASS" : "FAIL")} {name} {detail}");
            if (pass) _passes++; else _failures++;
            return pass;
        }

        private static async Task<bool> Until(Func<bool> condition, double seconds)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition())
            {
                if (DateTime.UtcNow > end) return false;
                await Task.Delay(100);
            }
            return true;
        }

        public async Task RunAsync()
        {
            (string, Func<Task>)[] sections =
            [
                ("startup", Startup), ("find", Find), ("type-to-focus", TypeToFocus), ("palette", Palette),
                ("path-menu", PathMenu), ("session-search", SessionSearch), ("send-now", SendNow), ("subagent", Subagent),
                ("jobs", Jobs), ("plan", Plan), ("connectors", Connectors), ("trust", Trust), ("auto-recover", AutoRecover),
                ("interrupt-race", InterruptRace),
                .. BtwGoalSections(), .. AskPlanSections(), .. QueueRowsSections(), .. ForkHostToolsSections(), .. MarksSections(), .. SidebarNavSections(),
                .. MicroUxSections(), .. SharedMarksSections(), .. ShareSections(), .. TeamSections(), .. NightlyShareSections(),
                .. ProgressSections(), .. GitBellSections(), .. TitleBarSections(), .. PluginsHubSections(), .. ChatLinksSections(),
            ];
            var only = Environment.GetEnvironmentVariable("OMBRA_FEATURE_SMOKE_ONLY") is { Length: > 0 } o ? o.Split(',') : null;
            foreach (var (name, section) in sections)
            {
                if (only is not null && name != "startup" && !only.Contains(name)) continue;
                Log($"── {name}");
                try { await section(); }
                catch (Exception e) { Check(name + "-section", false, e.ToString()); }
                await Reset();
            }
            Log($"RESULT {(_failures == 0 ? "PASS" : "FAIL")} passed={_passes} failed={_failures}");
            _log.Dispose();
            window.Close();
        }

        /// <summary>Between sections: dark theme, no pane, no palette/find, composer empty, nothing running.</summary>
        private async Task Reset()
        {
            Key("\u001b", 0x35);
            await Task.Delay(200);
            if (vm.IsPaletteOpen) vm.IsPaletteOpen = false;
            if (vm.IsFindOpen) vm.CloseFindCommand.Execute(null);
            vm.ActivePane = SidePane.None;
            vm.SettingsTheme = "dark";
            if (vm.IsSettingsOpen) vm.IsSettingsOpen = false;
            if (vm.IsRunning) { vm.AbortCommand.Execute(null); await Until(() => !vm.IsRunning, 20); }
            vm.ComposerText = "";
            await Task.Delay(300);
        }

        // ── sections ──

        private async Task Startup()
        {
            // OMBRA_FEATURE_SMOKE_POSITION=x,y puts the window elsewhere (another display) than the top-left of the main one
            window.Position = Environment.GetEnvironmentVariable("OMBRA_FEATURE_SMOKE_POSITION")?.Split(',') is [var px, var py]
                && int.TryParse(px, out var x) && int.TryParse(py, out var y) ? new PixelPoint(x, y) : new PixelPoint(40, 40);
            window.Width = 1360;
            window.Height = 880;
            vm.SettingsTheme = "dark";
            Activate();
            Check("engine-ready", await Until(() => vm.Phase == SessionPhase.Ready, 180), $"phase={vm.Phase} pid={vm.Session.ProcessId}");
            await Prompt("TOOLCALL please read README.md and say zebrafish");
            await Prompt("Second message about zebrafish and README.md");
            Shot("00-conversation-dark");
        }

        private async Task Find()
        {
            FocusTranscript();
            KeyCmd("f", 0x03);
            Check("cmd-f-opens", await Until(() => vm.IsFindOpen && FocusedIs("FindBox"), 5), $"open={vm.IsFindOpen} focus={Focused()}");
            Type("zebrafish");
            Check("find-counts", await Until(() => vm.FindTotal >= 2, 5), $"query='{vm.FindQuery}' total={vm.FindTotal} current={vm.FindCurrent}");
            var first = vm.FindCurrent;
            KeyCmd("g", 0x05);
            var next = await Until(() => vm.FindCurrent != first, 5);
            Check("cmd-g-next", next, $"{first} -> {vm.FindCurrent}/{vm.FindTotal}");
            var second = vm.FindCurrent;
            Key("g", 0x05, Cmd | Shift, "G");
            Check("shift-cmd-g-previous", await Until(() => vm.FindCurrent != second, 5), $"{second} -> {vm.FindCurrent}");
            await Task.Delay(400);
            Shot("01-find-dark");
            vm.SettingsTheme = "light";
            await Task.Delay(600);
            Shot("01-find-light");
            // ⌘G in the light theme: the next match is selected there too
            var beforeStep = vm.FindCurrent;
            KeyCmd("g", 0x05);
            Check("cmd-g-light", await Until(() => vm.FindCurrent != beforeStep, 5), $"{beforeStep} -> {vm.FindCurrent}");
            await Task.Delay(400);
            Shot("01-find-light-next");
            Key("\u001b", 0x35);
            Check("esc-closes-find", await Until(() => !vm.IsFindOpen && FocusedIs("Composer"), 5), $"open={vm.IsFindOpen} focus={Focused()}");
        }

        private async Task TypeToFocus()
        {
            vm.ComposerText = "";
            FocusTranscript();
            await Task.Delay(300);
            Log($"focus before typing: {Focused()}");
            var seen = new List<string>();
            EventHandler<KeyEventArgs> down = (_, e) => seen.Add($"down {e.Key} mods={e.KeyModifiers} handled={e.Handled} src={(e.Source as Control)?.GetType().Name}");
            EventHandler<TextInputEventArgs> text = (_, e) => seen.Add($"text '{e.Text}' handled={e.Handled} src={(e.Source as Control)?.GetType().Name}");
            window.AddHandler(InputElement.KeyDownEvent, down, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            window.AddHandler(InputElement.TextInputEvent, text, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            Type("hey");
            // The first key reaches the conversation, which has no input client: AppKit maps it with the current
            // keyboard layout (a Russian layout makes 'h' 'р'); the rest go to the composer's own client as posted.
            var ok = await Until(() => FocusedIs("Composer") && vm.ComposerText.Length == 3 && vm.ComposerText.EndsWith("ey", StringComparison.Ordinal), 5);
            window.RemoveHandler(InputElement.KeyDownEvent, down);
            window.RemoveHandler(InputElement.TextInputEvent, text);
            Log("type-to-focus events: " + string.Join(" ; ", seen));
            Check("type-to-focus", ok, $"focus={Focused()} composer='{vm.ComposerText}'");
            Shot("02-type-to-focus-dark");
        }

        private async Task Palette()
        {
            FocusTranscript();
            KeyCmd("k", 0x28);
            Check("cmd-k-palette", await Until(() => vm.IsPaletteOpen && vm.PaletteMode == PaletteMode.All && vm.PaletteItems.Count > 0, 5),
                $"open={vm.IsPaletteOpen} mode={vm.PaletteMode} items={vm.PaletteItems.Count}");
            // The last word of "New chat" in the window's language ("chat" / "чат"): typed in that language, it finds commands
            var word = Localization.Loc.T("New chat").Split(' ')[^1];
            Type(word);
            await Until(() => vm.PaletteQuery == word, 3);
            await Task.Delay(400);
            Check("cmd-k-finds-command", vm.PaletteItems.Count > 0, $"items for '{word}': " + string.Join(" | ", vm.PaletteItems.Take(6).Select(i => i.Title)));
            Shot("03-palette-cmd-k-dark");
            Key("\u001b", 0x35);
            Check("esc-closes-palette", await Until(() => !vm.IsPaletteOpen, 3));
            KeyCmd("p", 0x23);
            Check("cmd-p-files", await Until(() => vm.IsPaletteOpen && vm.PaletteMode == PaletteMode.Files, 5), $"mode={vm.PaletteMode}");
            Type("notes-3");
            var found = await Until(() => vm.PaletteItems.Any(i => i.Title.Contains("notes-3", StringComparison.Ordinal)), 8);
            Check("cmd-p-finds-file", found, string.Join(" | ", vm.PaletteItems.Take(5).Select(i => i.Title)));
            vm.SettingsTheme = "light";
            await Task.Delay(600);
            Shot("03-palette-cmd-p-light");
            Key("\r", 0x24);
            Check("cmd-p-enter-opens-file", await Until(() => !vm.IsPaletteOpen && vm.ActivePane == SidePane.Files, 5), $"pane={vm.ActivePane}");
            await Task.Delay(600);
            Shot("03-palette-opened-file-light");
        }

        private async Task PathMenu()
        {
            foreach (var fold in window.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.Classes.Contains("turn-fold-head") && t.IsEffectivelyEnabled && t.IsChecked != true).Take(1).ToList())
                Click(fold);
            await Task.Delay(600);
            var link = window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Classes.Contains("tool-path") && b.IsEffectivelyVisible && ContextMenus.GetPathMenu(b) is { Length: > 0 });
            if (!Check("tool-path-present", link is not null) || link is null) return;
            link.BringIntoView();
            await Task.Delay(400);
            RightClick(link);
            var opened = await Until(() => link.ContextFlyout is { IsOpen: true }, 5);
            Check("path-menu-opens", opened, $"path={ContextMenus.GetPathMenu(link)}");
            await Task.Delay(300);
            var items = (link.ContextFlyout as Flyout)?.Content is Control c
                ? c.GetLogicalDescendants().OfType<Button>().Select(Label).ToList()
                : [];
            Log("path menu items: " + string.Join(" | ", items));
            ShotScreenRegion("04-path-menu-dark");
            vm.SettingsTheme = "light";
            await Task.Delay(500);
            ShotScreenRegion("04-path-menu-light");
            // Copy path, through the menu's own button
            var copy = (link.ContextFlyout as Flyout)?.Content is Control content
                ? content.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => Label(b) == Localization.Loc.T("Copy path"))
                : null;
            if (Check("path-menu-copy-item", copy is not null))
            {
                Click(copy!);
                var clip = window.Clipboard;
                string? text = null;
                await Until(() => { text = clip?.TryGetTextAsync().GetAwaiter().GetResult(); return text?.EndsWith("README.md", StringComparison.Ordinal) == true; }, 5);
                Check("path-menu-copy-path", text?.EndsWith("README.md", StringComparison.Ordinal) == true, $"clipboard='{text}' menuOpen={link.ContextFlyout?.IsOpen}");
            }
        }

        private async Task SessionSearch()
        {
            var work = vm.Session.CurrentLaunch.WorkingDirectory;
            await vm.OpenFolderInNewSessionAsync(work ?? Environment.CurrentDirectory);
            await Until(() => vm.Phase == SessionPhase.Ready, 60);
            await Prompt("A quokka visited the harbour");
            vm.ToggleSessionSearchCommand.Execute(null);
            await Task.Delay(300);
            vm.SessionFilter = "zebrafish";
            var hit = await Until(() => vm.SessionGroups.SelectMany(g => g.Items).Any(i => i.ContentMatch is not null), 20);
            Check("content-search-hit", hit, string.Join(" | ", vm.SessionGroups.SelectMany(g => g.Items).Select(i => $"{i.Title}:{i.ContentMatch?.Text}")));
            await Task.Delay(400);
            Shot("05-session-search-dark");
            vm.SettingsTheme = "light";
            await Task.Delay(500);
            Shot("05-session-search-light");
            vm.SessionFilter = "quokka";
            Check("content-search-second", await Until(() => vm.SessionGroups.SelectMany(g => g.Items).Any(i => i.ContentMatch?.Text.Contains("quokka", StringComparison.OrdinalIgnoreCase) == true), 20),
                string.Join(" | ", vm.SessionGroups.SelectMany(g => g.Items).Select(i => $"{i.Title}:{i.ContentMatch?.Text}")));
            vm.SessionFilter = "";
            vm.IsSessionSearchOpen = false;
        }

        private async Task SendNow()
        {
            // A run with steps (a read every few seconds): Send now hands the message to omp for its next step
            await Send("LONGTASK 0.4 id=sendnow");
            await Until(() => vm.IsRunning, 15);
            vm.ComposerText = "queued follow-up alpha";
            FocusComposer();
            Key("\r", 0x24);
            Check("queued-while-running", await Until(() => vm.QueuedMessages.Count == 1, 5), $"queued={vm.QueuedMessages.Count} running={vm.IsRunning}");
            await Task.Delay(400);
            Shot("06-queued-dark");
            var item = vm.QueuedMessages.FirstOrDefault();
            var button = item is null ? null : ButtonFor(item.SendNowCommand);
            if (Check("send-now-button", button is not null))
            {
                Click(button!);
                Check("send-now-sends", await Until(() => vm.QueuedMessages.Count == 0 && UserRowsContain("queued follow-up alpha"), 30),
                    $"queued={vm.QueuedMessages.Count} running={vm.IsRunning} notices=[{Notices()}]");
            }
            await Until(() => !vm.IsRunning, 90);
            await Send("SLOW streaming for interrupt");
            await Until(() => vm.IsRunning, 15);
            vm.ComposerText = "interrupt bravo";
            FocusComposer();
            Key("\r", 0x24, Alt | Shift);
            Check("interrupt-and-send", await Until(() => UserRowsContain("interrupt bravo") && vm.QueuedMessages.Count == 0, 20),
                $"running={vm.IsRunning} composer='{vm.ComposerText}' message='{vm.ComposerMessage}' notices=[{Notices()}]");
            if (vm.IsRunning && !UserRowsContain("interrupt bravo")) { vm.AbortCommand.Execute(null); vm.ComposerText = ""; }
            await Until(() => !vm.IsRunning, 60);
            Shot("06-after-interrupt-dark");
        }

        private string Notices() => string.Join(" | ", vm.Rows.OfType<NoticeRowViewModel>().Select(n => n.Text));

        /// <summary>The tool's approval card, answered with its own Allow button (Accept edits asks before task/bash).</summary>
        private async Task ApproveThroughCard(string shot)
        {
            if (!await Until(() => vm.CurrentDialog is not null, 20)) { Log("no approval asked"); return; }
            await Task.Delay(400);
            Shot(shot + "-dark");
            var allow = ButtonFor(vm.CurrentDialog!.AllowCommand);
            if (Check("approval-allow-button", allow is not null)) Click(allow!);
            Check("approval-answered", await Until(() => vm.CurrentDialog is null, 10));
        }

        /// <summary>Interrupt and send on a reply that starts right after a turn a Send now message caused.</summary>
        private async Task InterruptRace()
        {
            for (var round = 1; round <= 3; round++)
            {
                await Send($"SLOWSHORT race {round}");
                await Until(() => vm.IsRunning, 15);
                vm.ComposerText = $"steer race {round}";
                FocusComposer();
                Key("\r", 0x24);
                await Until(() => vm.QueuedMessages.Count == 1, 5);
                if (vm.QueuedMessages.FirstOrDefault() is { } item && ButtonFor(item.SendNowCommand) is { } now) Click(now);
                await Until(() => !vm.IsRunning && vm.Phase == SessionPhase.Ready, 90);
                await Send($"SLOW race {round}");
                await Until(() => vm.IsRunning, 15);
                vm.ComposerText = $"interrupt race {round}";
                FocusComposer();
                Key("\r", 0x24, Alt | Shift);
                var ok = await Until(() => UserRowsContain($"interrupt race {round}") && vm.ComposerText.Length == 0, 20);
                Check($"interrupt-race-{round}", ok, $"running={vm.IsRunning} composer='{vm.ComposerText}' message='{vm.ComposerMessage}' notices=[{Notices()}]");
                if (!ok)
                {
                    Shot($"13-interrupt-race-{round}-dark");
                    vm.AbortCommand.Execute(null);
                }
                await Until(() => !vm.IsRunning, 60);
                vm.ComposerText = "";
            }
        }

        private async Task Subagent()
        {
            await Send("SUBAGENT start a helper");
            await ApproveThroughCard("07-subagent-approval");
            vm.ActivePane = SidePane.Tasks;
            var row = (SubagentRowViewModel?)null;
            Check("subagent-listed", await Until(() => (row = vm.Tasks.Subagents.FirstOrDefault(s => s.IsRunning)) is not null, 60),
                string.Join(" | ", vm.Tasks.Subagents.Select(s => $"{s.Title}:{s.StatusText}")));
            if (row is null) return;
            await Task.Delay(1500);
            Shot("07-subagent-running-dark");
            row.MessageText = "STEER-MARKER please also check notes";
            await Task.Delay(300);
            var send = ButtonFor(row.SendMessageCommand);
            if (Check("subagent-message-button", send is not null)) Click(send!);
            var steered = await Until(() => row.MessageText.Length == 0 && !row.HasActionError, 20);
            var mockLog = Environment.GetEnvironmentVariable("OMBRA_FEATURE_SMOKE_MOCKLOG");
            var reached = await Until(() => mockLog is not null && File.Exists(mockLog) && ReadShared(mockLog).Contains("STEER-MARKER", StringComparison.Ordinal), 40);
            Check("subagent-message-delivered", steered && reached, $"cleared={steered} model saw it={reached} error='{row.ActionError}'");
            vm.SettingsTheme = "light";
            await Task.Delay(500);
            Shot("07-subagent-messaged-light");
            var stop = ButtonFor(row.StopCommand);
            if (Check("subagent-stop-button", stop is not null)) Click(stop!);
            Check("subagent-stopped", await Until(() => !row.IsRunning, 30), $"status={row.StatusText} error='{row.ActionError}'");
            await Task.Delay(500);
            Shot("07-subagent-stopped-light");
            await Until(() => !vm.IsRunning, 60);
        }

        private async Task Jobs()
        {
            await Send("BGJOB start a background job");
            await ApproveThroughCard("08-job-approval");
            vm.ActivePane = SidePane.Tasks;
            JobRowViewModel? job = null;
            Check("job-listed", await Until(() => (job = vm.Tasks.Jobs.FirstOrDefault(j => j.IsRunning)) is not null, 60),
                string.Join(" | ", vm.Tasks.Jobs.Select(j => $"{j.Title}:{j.StatusText}")));
            if (job is null) return;
            Check("job-waiting-for-input", await Until(() => job.IsWaitingForInput, 40), $"status={job.StatusText} detail={job.Detail}");
            await Task.Delay(400);
            Shot("08-job-waiting-dark");
            var show = ButtonFor(job.ToggleOutputCommand);
            if (Check("job-show-output-button", show is not null)) Click(show!);
            Check("job-output", await Until(() => job.IsOutputShown && job.OutputText.Contains("started", StringComparison.Ordinal), 15),
                $"shown={job.IsOutputShown} output='{job.OutputText}' note='{job.OutputNote}'");
            await Task.Delay(400);
            Shot("08-job-output-dark");
            vm.SettingsTheme = "light";
            await Task.Delay(500);
            Shot("08-job-output-light");
            var stop = ButtonFor(job.StopCommand);
            if (Check("job-stop-button", stop is not null)) Click(stop!);
            Check("job-stopped", await Until(() => !job.IsRunning, 20), $"status={job.StatusText} error='{job.ActionError}'");
            await Task.Delay(400);
            Shot("08-job-stopped-light");
        }

        private async Task Plan()
        {
            Check("plan-available", await Until(() => vm.IsPlanModeAvailable, 10));
            FocusComposer();
            for (var i = 0; i < 5 && !vm.IsPlanMode; i++)
            {
                Key("\t", 0x30, Shift, "\t");
                await Task.Delay(700);
                Log($"shift+tab -> approval={vm.ShownApprovalMode} plan={vm.IsPlanMode}");
            }
            Check("shift-tab-plan", await Until(() => vm.IsPlanMode && !vm.IsPlanActionRunning, 10), $"approval={vm.ShownApprovalMode}");
            Shot("09-plan-mode-dark");
            await Prompt("PLANCALL make a plan");
            Check("plan-card", await Until(() => vm.HasPlanReview, 30), $"title={vm.PlanReviewTitle} path={vm.PlanReviewPath}");
            await Until(() => !vm.IsRunning, 30);
            await Task.Delay(600);
            Shot("09-plan-card-dark");
            vm.SettingsTheme = "light";
            await Task.Delay(500);
            Shot("09-plan-card-light");
            // Reject with feedback, through the card's buttons
            var feedback = ButtonFor(vm.StartPlanFeedbackCommand);
            if (Check("plan-feedback-button", feedback is not null)) Click(feedback!);
            await Until(() => vm.IsWritingPlanFeedback, 3);
            vm.PlanFeedbackText = "add a test step";
            await Task.Delay(300);
            Shot("09-plan-feedback-light");
            var reject = ButtonFor(vm.RejectPlanCommand);
            if (Check("plan-reject-button", reject is not null)) Click(reject!);
            Check("plan-rejected", await Until(() => !vm.HasPlanReview, 15) && vm.IsPlanMode, $"plan={vm.IsPlanMode}");
            await Until(() => !vm.IsRunning, 30);
            await Prompt("PLANCALL make it again");
            Check("plan-card-again", await Until(() => vm.HasPlanReview, 30));
            await Until(() => !vm.IsRunning, 30);
            var approve = ButtonFor(vm.ApprovePlanCommand);
            if (Check("plan-approve-button", approve is not null)) Click(approve!);
            Check("plan-approved", await Until(() => !vm.HasPlanReview && !vm.IsPlanMode, 20), $"plan={vm.IsPlanMode} review={vm.HasPlanReview}");
            await Until(() => !vm.IsRunning, 60);
            await Task.Delay(500);
            Shot("09-plan-approved-light");
        }

        private async Task Connectors()
        {
            vm.OpenConnectorsPageCommand.Execute(null);
            var c = vm.Connectors;
            Shot("10-connectors-loading-dark");
            var loaded = await Until(() => c.Servers.Count >= 2 && !c.Servers.Any(s => s.IsWorking), 60);
            Check("connectors-listed", loaded, string.Join(" | ", c.Servers.Select(s => $"{s.Name}:{s.State}")));
            var probe = c.Servers.FirstOrDefault(s => s.Name == "probe");
            if (probe is { CanTest: true } && probe.State != ConnectorState.Connected)
            {
                probe.TestCommand.Execute(null);
                await Until(() => !probe.IsWorking && probe.State != ConnectorState.NotConnected, 40);
            }
            Check("connector-connected", probe?.State == ConnectorState.Connected, $"probe={probe?.State}");
            var broken = c.Servers.FirstOrDefault(s => s.Name == "broken");
            if (broken is { CanTest: true } && broken.State != ConnectorState.Failed)
            {
                broken.TestCommand.Execute(null);
                await Until(() => !broken.IsWorking && broken.State is ConnectorState.Failed or ConnectorState.NotConnected, 40);
            }
            Check("connector-failed", broken?.State == ConnectorState.Failed, $"broken={broken?.State}");
            await Task.Delay(500);
            Shot("10-connectors-dark");
            vm.SettingsTheme = "light";
            await Task.Delay(600);
            Shot("10-connectors-light");
            if (probe is not null)
            {
                probe.IsEnabled = false;
                await Until(() => probe.State == ConnectorState.Off, 20);
                Check("connector-off", probe.State == ConnectorState.Off, $"probe={probe.State}");
                await Task.Delay(500);
                Shot("10-connectors-off-light");
                probe.IsEnabled = true;
                await Until(() => probe.State != ConnectorState.Off, 20);
            }
        }

        private async Task Trust()
        {
            var folder = Environment.GetEnvironmentVariable("OMBRA_FEATURE_SMOKE_UNTRUSTED");
            var marker = Environment.GetEnvironmentVariable("OMBRA_FEATURE_SMOKE_MARKER");
            if (!Check("trust-fixture", folder is not null && marker is not null)) return;
            if (File.Exists(marker)) File.Delete(marker!);
            await vm.OpenFolderInNewSessionAsync(folder!);
            Check("trust-card", await Until(() => vm.SessionCard is { Kind: "trust" }, 90), $"card={vm.SessionCard?.Kind} phase={vm.Phase}");
            var card = vm.SessionCard;
            Log($"trust card: {card?.Title} | {card?.Message} | {card?.Detail}");
            await Until(() => vm.Phase == SessionPhase.Ready, 30);
            var skills = vm.Session.Snapshot().Commands.Select(x => x.Name).Where(n => n.Contains("probe", StringComparison.OrdinalIgnoreCase)).ToList();
            Check("untrusted-extension-skipped", !File.Exists(marker), $"marker exists={File.Exists(marker)}");
            // Skills are instructions, not code: the engine's trust scope (untrusted-project.ts) leaves them loaded
            Log($"INFO untrusted project skills still offered: [{string.Join(",", skills)}]");
            Check("trust-card-lists-extension", card?.Detail.Contains("probe.ts", StringComparison.Ordinal) == true, card?.Detail ?? "");
            await Task.Delay(500);
            Shot("11-trust-card-dark");
            vm.SettingsTheme = "light";
            await Task.Delay(500);
            Shot("11-trust-card-light");
            var trust = card?.Primary?.Command is { } cmd ? ButtonFor(cmd) : null;
            if (!Check("trust-button", trust is not null)) return;
            Click(trust!);
            var restarted = await Until(() => File.Exists(marker) && vm.Phase == SessionPhase.Ready && vm.SessionCard is not { Kind: "trust" }, 90);
            var after = vm.Session.Snapshot().Commands.Select(x => x.Name).Where(n => n.Contains("probe", StringComparison.OrdinalIgnoreCase)).ToList();
            Check("trusted-extension-loaded", restarted, $"marker={File.Exists(marker)} card={vm.SessionCard?.Kind}");
            Check("trusted-skill-loaded", await Until(() => vm.Session.Snapshot().Commands.Any(x => x.Name.Contains("probe-skill", StringComparison.Ordinal)), 20),
                $"probe commands=[{string.Join(",", after)}]");
            await Task.Delay(500);
            Shot("11-trusted-light");
        }

        private async Task AutoRecover()
        {
            await Until(() => vm.Phase == SessionPhase.Ready, 30);
            var pid = vm.Session.ProcessId;
            if (!Check("engine-pid", pid is > 0, $"pid={pid}")) return;
            Process.GetProcessById(pid!.Value).Kill();
            Log($"killed engine pid {pid}");
            var shown = await Until(() => vm.IsAutoRecovering, 20);
            Check("auto-recover-shown", shown, $"phase={vm.Phase} text='{vm.AutoRecoverText}'");
            await Task.Delay(300);
            Shot("12-auto-recover-dark");
            vm.SettingsTheme = "light";
            await Task.Delay(300);
            Shot("12-auto-recover-light");
            var back = await Until(() => !vm.IsAutoRecovering && vm.Phase == SessionPhase.Ready && vm.Session.ProcessId is { } p && p != pid, 90);
            Check("auto-recovered", back, $"phase={vm.Phase} pid={vm.Session.ProcessId}");
            var before = vm.Rows.OfType<AssistantRowViewModel>().Count();
            await Prompt("after recovery hello");
            Check("works-after-recover", vm.Rows.OfType<AssistantRowViewModel>().Count() > before && UserRowsContain("after recovery hello"),
                $"assistant rows {before} -> {vm.Rows.OfType<AssistantRowViewModel>().Count()}");
            Shot("12-recovered-light");
        }

        // ── helpers: the conversation ──

        private async Task Send(string text)
        {
            await Until(() => vm.Phase == SessionPhase.Ready, 60);
            vm.ComposerText = text;
            FocusComposer();
            await Task.Delay(150);
            Key("\r", 0x24);
            await Until(() => vm.ComposerText.Length == 0, 5);
        }

        private async Task Prompt(string text)
        {
            await Send(text);
            await Until(() => vm.IsRunning, 10);
            var done = await Until(() => !vm.IsRunning && vm.Phase == SessionPhase.Ready, 90);
            Log($"prompt '{text}' done={done} rows={vm.Rows.Count}");
        }

        private bool UserRowsContain(string text) => vm.Rows.OfType<UserRowViewModel>().Any(r => r.Text.Contains(text, StringComparison.Ordinal));

        private bool AssistantRowsContain(string text) => vm.Rows.OfType<AssistantRowViewModel>().Any(r => r.Text.Contains(text, StringComparison.Ordinal));

        private static string ReadShared(string path)
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new StreamReader(s).ReadToEnd();
        }

        // ── helpers: focus and controls ──

        private string Focused() => window.FocusManager?.GetFocusedElement() is Control c ? $"{c.GetType().Name}#{c.Name}" : "none";

        private bool FocusedIs(string name) =>
            window.FocusManager?.GetFocusedElement() is Control c && c.GetSelfAndVisualAncestors().OfType<Control>().Any(a => a.Name == name);

        private void FocusComposer() => window.FindControl<TextBox>("Composer")?.Focus();

        private void FocusTranscript()
        {
            var t = window.FindControl<ListBox>("Transcript");
            if (t is null) return;
            Click(t, 0.5, 0.3);
        }

        private Button? ButtonFor(System.Windows.Input.ICommand command) =>
            window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => ReferenceEquals(b.Command, command) && b.IsEffectivelyVisible && b.IsEffectivelyEnabled)
            ?? AllPopupButtons().FirstOrDefault(b => ReferenceEquals(b.Command, command) && b.IsEffectivelyVisible);

        private static string Label(Button b) =>
            Avalonia.Automation.AutomationProperties.GetName(b) is { Length: > 0 } name ? name
                : string.Join(" ", b.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)));

        private IEnumerable<Button> AllPopupButtons() =>
            window.GetVisualDescendants().OfType<Popup>().Where(p => p.IsOpen && p.Child is not null)
                .SelectMany(p => p.Child!.GetVisualDescendants().OfType<Button>());

        // ── helpers: NSEvents ──

        private static nint App => MacObjC.Send(MacObjC.Cls("NSApplication"), "sharedApplication");
        private nint NsWindow => NightlyCocoa.WindowOf(window.TryGetPlatformHandle()?.Handle ?? 0);
        private static double Uptime() => SendDouble(MacObjC.Send(MacObjC.Cls("NSProcessInfo"), "processInfo"), "systemUptime");

        private unsafe void Activate()
        {
            ((delegate* unmanaged<nint, nint, byte, void>)MacObjC.MsgSend)(App, MacObjC.Sel("activateIgnoringOtherApps:"), 1);
            MacObjC.SendVoid(NsWindow, "makeKeyAndOrderFront:", 0);
            window.Activate();
        }

        private unsafe void Post(nint ev) => ((delegate* unmanaged<nint, nint, nint, byte, void>)MacObjC.MsgSend)(App, MacObjC.Sel("postEvent:atStart:"), ev, 0);

        private unsafe void Key(string characters, ushort keyCode, ulong flags = 0, string? ignoringModifiers = null)
        {
            foreach (var type in new ulong[] { 10, 11 })
            {
                using var chars = new NSStr(characters);
                using var bare = new NSStr(ignoringModifiers ?? characters);
                var ev = ((delegate* unmanaged<nint, nint, ulong, CGPoint, ulong, double, long, nint, nint, nint, byte, ushort, nint>)MacObjC.MsgSend)(
                    MacObjC.Cls("NSEvent"), MacObjC.Sel("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:"),
                    type, default, flags, Uptime(), MacObjC.SendLong(NsWindow, "windowNumber"), 0, chars.Handle, bare.Handle, 0, keyCode);
                Post(ev);
            }
        }

        private void KeyCmd(string ch, ushort code) => Key(ch, code, Cmd);

        private static readonly Dictionary<char, ushort> Codes = new()
        {
            ['a'] = 0x00, ['s'] = 0x01, ['d'] = 0x02, ['f'] = 0x03, ['h'] = 0x04, ['g'] = 0x05, ['z'] = 0x06, ['x'] = 0x07, ['c'] = 0x08,
            ['v'] = 0x09, ['b'] = 0x0B, ['q'] = 0x0C, ['w'] = 0x0D, ['e'] = 0x0E, ['r'] = 0x0F, ['y'] = 0x10, ['t'] = 0x11, ['1'] = 0x12,
            ['2'] = 0x13, ['3'] = 0x14, ['4'] = 0x15, ['6'] = 0x16, ['5'] = 0x17, ['9'] = 0x19, ['7'] = 0x1A, ['-'] = 0x1B, ['8'] = 0x1C,
            ['0'] = 0x1D, ['o'] = 0x1F, ['u'] = 0x20, ['i'] = 0x22, ['p'] = 0x23, ['l'] = 0x25, ['j'] = 0x26, ['k'] = 0x28, ['n'] = 0x2D,
            ['m'] = 0x2E, ['.'] = 0x2F, [' '] = 0x31,
        };

        private void Type(string text)
        {
            foreach (var ch in text) Key(ch.ToString(), Codes.GetValueOrDefault(char.ToLowerInvariant(ch), (ushort)0x00));
        }

        private unsafe CGPoint WindowPoint(Control c, double fx, double fy)
        {
            var client = c.TranslatePoint(new Point(c.Bounds.Width * fx, c.Bounds.Height * fy), window) ?? default;
            var content = MacObjC.Send(NsWindow, "contentView");
            var bounds = ((delegate* unmanaged<nint, nint, CGRect>)MacObjC.MsgSend)(content, MacObjC.Sel("bounds"));
            var inView = new CGPoint(client.X, MacObjC.SendBool(content, "isFlipped") ? client.Y : bounds.Height - client.Y);
            return ((delegate* unmanaged<nint, nint, CGPoint, nint, CGPoint>)MacObjC.MsgSend)(content, MacObjC.Sel("convertPoint:toView:"), inView, 0);
        }

        private unsafe nint MouseEvent(ulong type, CGPoint at, long clicks, nint windowRef, ulong modifiers = 0) =>
            ((delegate* unmanaged<nint, nint, ulong, CGPoint, ulong, double, long, nint, long, long, float, nint>)MacObjC.MsgSend)(
                MacObjC.Cls("NSEvent"), MacObjC.Sel("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
                type, at, modifiers, Uptime(), MacObjC.SendLong(windowRef, "windowNumber"), 0, 0, clicks, type is 1 or 3 ? 1f : 0f);

        /// <summary>The native window a control is shown in (the main window, or a popup's own window).</summary>
        private nint NsWindowOf(Control c) =>
            TopLevel.GetTopLevel(c) is { } top && top != window && top.TryGetPlatformHandle()?.Handle is { } h and not 0 ? NightlyCocoa.WindowOf(h) : NsWindow;

        private void Click(Control c, double fx = 0.5, double fy = 0.5)
        {
            var w = NsWindowOf(c);
            var at = TopLevel.GetTopLevel(c) is { } top && top != window ? PopupPoint(c, top, w, fx, fy) : WindowPoint(c, fx, fy);
            Post(MouseEvent(5, at, 0, w));
            Post(MouseEvent(1, at, 1, w));
            Post(MouseEvent(2, at, 1, w));
            Log($"click {c.GetType().Name}#{c.Name} '{Avalonia.Automation.AutomationProperties.GetName(c)}' at ({at.X:0},{at.Y:0})");
        }

        private unsafe CGPoint PopupPoint(Control c, TopLevel top, nint w, double fx, double fy)
        {
            var client = c.TranslatePoint(new Point(c.Bounds.Width * fx, c.Bounds.Height * fy), top) ?? default;
            var content = MacObjC.Send(w, "contentView");
            var bounds = ((delegate* unmanaged<nint, nint, CGRect>)MacObjC.MsgSend)(content, MacObjC.Sel("bounds"));
            var inView = new CGPoint(client.X, MacObjC.SendBool(content, "isFlipped") ? client.Y : bounds.Height - client.Y);
            return ((delegate* unmanaged<nint, nint, CGPoint, nint, CGPoint>)MacObjC.MsgSend)(content, MacObjC.Sel("convertPoint:toView:"), inView, 0);
        }

        private void RightClick(Control c)
        {
            var at = WindowPoint(c, 0.5, 0.5);
            Post(MouseEvent(5, at, 0, NsWindow));
            Post(MouseEvent(3, at, 1, NsWindow));
            Post(MouseEvent(4, at, 1, NsWindow));
            Log($"right-click {c.GetType().Name} at ({at.X:0},{at.Y:0})");
        }

        // ── screenshots ──

        private void Shot(string name)
        {
            var file = Path.Combine(dir, name + ".png");
            var number = MacObjC.SendLong(NsWindow, "windowNumber");
            using var p = Process.Start(new ProcessStartInfo("/usr/sbin/screencapture", ["-x", "-o", "-l", number.ToString(), file]))!;
            p.WaitForExit();
            Log($"SHOT {file} exit={p.ExitCode}");
        }

        /// <summary>The window's area of the screen, popups over it included.</summary>
        private unsafe void ShotScreenRegion(string name)
        {
            var file = Path.Combine(dir, name + ".png");
            var frame = ((delegate* unmanaged<nint, nint, CGRect>)MacObjC.MsgSend)(NsWindow, MacObjC.Sel("frame"));
            var screens = MacObjC.Send(MacObjC.Cls("NSScreen"), "screens");
            var primaryHeight = ((delegate* unmanaged<nint, nint, CGRect>)MacObjC.MsgSend)(MacObjC.Send(screens, "firstObject"), MacObjC.Sel("frame")).Height;
            var rect = $"{frame.X:0},{primaryHeight - frame.Y - frame.Height:0},{frame.Width:0},{frame.Height:0}";
            using var p = Process.Start(new ProcessStartInfo("/usr/sbin/screencapture", ["-x", "-R", rect, file]))!;
            p.WaitForExit();
            Log($"SHOT {file} region={rect} exit={p.ExitCode}");
        }
    }
}
#endif
