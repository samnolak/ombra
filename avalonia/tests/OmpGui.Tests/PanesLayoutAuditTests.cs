using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;

namespace OmpGui.Tests;

/// <summary>
/// The Views menu and each side pane (Plan, Background tasks) in both themes, wide and narrow, measured by
/// <see cref="LayoutAudit"/>: zero findings. With OMPGUI_REVIEW_DIR set, screenshots go to its "panes" folder.
/// </summary>
public sealed class PanesLayoutAuditTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("OMPGUI_REVIEW_DIR");

    private static async Task Settle(int ms = 300)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    private sealed class Audit
    {
        public readonly StringBuilder Report = new();
        public int Count;

        public void Take(Window w, string screen)
        {
            using (var frame = w.CaptureRenderedFrame())
            {
                if (Dir is not null)
                {
                    Directory.CreateDirectory(Path.Combine(Dir, "panes"));
                    using var f = File.Create(Path.Combine(Dir, "panes", screen + ".png"));
                    frame?.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
            }
            var found = LayoutAudit.Run(w);
            Count += found.Count;
            Report.Append(LayoutAudit.Report(screen, found));
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(DesignMatrix.Panes), MemberType = typeof(DesignMatrix))]
    public async Task The_views_menu_and_the_panes_are_aligned(string theme, double width, double height)
    {
        MainViewModel.ApplyTheme(theme);
        var a = new Audit();
        var tag = $"{theme}-{width}";
        try
        {
            // Plan: empty, then a plan with every status, then a replaced plan with its history open
            var (w, vm) = await PanesUiTests.OpenAsync("plan", width, height);
            vm.ShowPaneCommand.Execute(SidePane.Plan);
            await Settle();
            a.Take(w, $"{tag}-plan-empty");
            await PanesUiTests.SendAsync(vm, "plan it", () => vm.Plan.HasPlan);
            a.Take(w, $"{tag}-plan");
            // While a run goes on, the current step carries the activity line (what omp does now)
            vm.Plan.Phases.SelectMany(p => p.Tasks).Single(t => t.IsCurrent).Activity = "Running bash 00:12";
            await Settle();
            a.Take(w, $"{tag}-plan-running");
            vm.Plan.Phases.SelectMany(p => p.Tasks).Single(t => t.IsCurrent).Activity = "";
            if (width >= 1180)
            {
                // The pane beside the conversation leaves the header's ⋮ in reach: its menu ticks the pane shown
                PanesUiTests.OpenViewsMenu(w);
                await Settle();
                a.Take(w, $"{tag}-views-menu-ticked");
                PanesUiTests.ViewsButton(w).Flyout!.Hide();
                await Settle(100);
            }
            vm.ClosePaneCommand.Execute(null);
            await Settle(); // the ⋮ moves back to the window's edge before its menu opens
            PanesUiTests.OpenViewsMenu(w);
            await Settle();
            Assert.Same(w, PanesUiTests.ViewsButton(w).Flyout is Flyout { Content: Control c } ? TopLevel.GetTopLevel(c) : null);
            a.Take(w, $"{tag}-views-menu");
            PanesUiTests.ViewsButton(w).Flyout!.Hide();
            vm.ShowPaneCommand.Execute(SidePane.Plan);
            await PanesUiTests.SendAsync(vm, "go on", () => vm.Plan.Changes.Count > 1);
            await PanesUiTests.SendAsync(vm, "new plan", () => vm.Plan.HasEarlierPlans);
            vm.Plan.IsHistoryOpen = true;
            vm.Plan.EarlierPlans[0].IsExpanded = true;
            await Settle();
            a.Take(w, $"{tag}-plan-history");
            w.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "PlanScroll").ScrollToEnd();
            await Settle();
            a.Take(w, $"{tag}-plan-history-end");
            vm.ShowPaneCommand.Execute(SidePane.Tasks);
            await Settle();
            a.Take(w, $"{tag}-tasks-empty");
            await vm.DisposeAsync();
            w.Close();

            // Background tasks: subagents that finished, failed and still run; jobs; one card open with its conversation
            (w, vm) = await PanesUiTests.OpenAsync("subagents", width, height);
            await PanesUiTests.SendAsync(vm, "look into it", () => vm.Tasks.Subagents.Count == 3);
            vm.ShowPaneCommand.Execute(SidePane.Tasks);
            await Settle(600);
            a.Take(w, $"{tag}-tasks");
            var done = vm.Tasks.Subagents.Single(r => r.Id == "sa-1");
            done.IsExpanded = true;
            await done.LoadMessagesCommand.ExecuteAsync(null);
            await Settle();
            a.Take(w, $"{tag}-tasks-open");
            w.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "TasksScroll").ScrollToEnd();
            await Settle();
            a.Take(w, $"{tag}-tasks-end");
            vm.ClosePaneCommand.Execute(null);
            await Settle();
            PanesUiTests.OpenViewsMenu(w);
            await Settle();
            a.Take(w, $"{tag}-views-menu-running");
            PanesUiTests.ViewsButton(w).Flyout!.Hide();
            await vm.DisposeAsync();
            w.Close();
        }
        finally
        {
            MainViewModel.ApplyTheme("light");
        }
        if (Dir is not null) File.AppendAllText(Path.Combine(Dir, "panes-audit.tsv"), a.Report.ToString());
        Assert.True(a.Count == 0, $"{a.Count} layout findings:\n{a.Report}");
    }
}
