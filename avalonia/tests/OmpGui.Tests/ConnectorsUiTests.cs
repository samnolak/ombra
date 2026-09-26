using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// The Connectors and Plugins and skills pages in the real window: the composer's "+" menu opens them, and every state
/// (list, form, row panels, empty, error, omp not running, the "+" menu) is measured by <see cref="LayoutAudit"/> in both
/// themes, wide and narrow. With OMPGUI_REVIEW_DIR set the screenshots go to &lt;dir&gt;/connectors.
/// </summary>
public sealed class ConnectorsUiTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("OMPGUI_REVIEW_DIR");

    private static async Task Settle(int ms = 250)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    private static async Task Until(Func<bool> c, string what, int seconds = 20)
    {
        var d = DateTime.UtcNow.AddSeconds(seconds);
        while (!c())
        {
            if (DateTime.UtcNow > d) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class Audit
    {
        public readonly StringBuilder Report = new();
        public int Count;

        public void Take(Window w, string screen)
        {
            Dispatcher.UIThread.RunJobs();
            if (Dir is not null)
            {
                using var frame = w.CaptureRenderedFrame();
                Directory.CreateDirectory(Path.Combine(Dir, "connectors"));
                using var f = File.Create(Path.Combine(Dir, "connectors", screen + ".png"));
                frame?.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            var found = LayoutAudit.Run(w);
            Count += found.Count;
            Report.Append(LayoutAudit.Report(screen, found));
        }

        /// <summary>The page at its top and, when it scrolls, at its end.</summary>
        public async Task TakePage(Window w, string screen)
        {
            var page = w.FindControl<ScrollViewer>("SettingsScroll")!;
            page.ScrollToHome();
            await Settle(150);
            Take(w, screen);
            if (page.Extent.Height <= page.Viewport.Height + 1) return;
            page.ScrollToEnd();
            await Settle(150);
            Take(w, screen + "-end");
            page.ScrollToHome();
        }

        /// <summary>The page scrolled so that the named control is in view.</summary>
        public async Task TakeAt(Window w, string control, string screen)
        {
            var target = w.GetVisualDescendants().OfType<Control>().First(c => c.Name == control);
            target.BringIntoView();
            await Settle(200);
            Take(w, screen);
            w.FindControl<ScrollViewer>("SettingsScroll")!.ScrollToHome();
        }
    }

    private static async Task<(MainWindow w, ConnectorsFlowTests.Setup t)> Open(double width, double height,
        Dictionary<string, string>? commands = null, Dictionary<string, object>? cli = null, bool projectFiles = true)
    {
        var t = await ConnectorsFlowTests.StartAsync(commands, cli, start: false, projectFiles: projectFiles);
        var w = new MainWindow { DataContext = t.Vm, Width = width, Height = height };
        w.Show();
        t.Vm.OnWindowOpened();
        await Until(() => t.Vm.Phase is SessionPhase.Ready or SessionPhase.Faulted, "omp ready");
        await Settle();
        return (w, t);
    }

    private static async Task Close(MainWindow w, ConnectorsFlowTests.Setup t)
    {
        await t.Vm.DisposeAsync();
        w.Close();
    }

    private static async Task ShowConnectors(MainViewModel vm)
    {
        await vm.OpenSettingsAtAsync("connectors");
        await Until(() => vm.Connectors.HasLoaded && !vm.Connectors.IsLoading, "connectors loaded");
        await Settle(300);
    }

    private static async Task ShowPlugins(MainViewModel vm)
    {
        await vm.OpenSettingsAtAsync("plugins");
        await Until(() => vm.Plugins.HasLoaded && !vm.Plugins.IsLoading, "plugins loaded");
        await Settle(300);
    }

    [AvaloniaFact]
    public async Task The_plus_menu_opens_connectors_and_plugins()
    {
        var (w, t) = await Open(1180, 760, cli: PluginsFlowTests.Cli());
        var attach = w.FindControl<Button>("AttachButton")!;
        attach.Flyout!.ShowAt(attach);
        await Settle();
        var item = attach.Flyout is Flyout { Content: Control content } ? content.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "ConnectorsItem") : null;
        Assert.NotNull(item);
        item!.Command!.Execute(null);
        await Until(() => t.Vm.IsSettingsOpen && t.Vm.SettingsCategory == "connectors", "connectors page");
        await Until(() => t.Vm.Connectors.Servers.Count == 5, "servers listed");
        Assert.True(w.FindControl<Border>("SettingsPage")!.IsVisible);

        t.Vm.CloseSettingsCommand.Execute(null);
        var plugins = ((Flyout)attach.Flyout!).Content is Control c ? c.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "PluginsItem") : null;
        plugins!.Command!.Execute(null);
        await Until(() => t.Vm.IsSettingsOpen && t.Vm.SettingsCategory == "plugins", "plugins page");
        await Until(() => t.Vm.Plugins.Plugins.Count == 2, "plugins listed");
        await Close(w, t);
    }

    [AvaloniaTheory]
    [MemberData(nameof(DesignMatrix.Standard), MemberType = typeof(DesignMatrix))]
    public async Task Connectors_and_plugins_screens_are_aligned(string theme, double width, double height)
    {
        MainViewModel.ApplyTheme(theme);
        var a = new Audit();
        var tag = $"{theme}-{width}";
        try
        {
            // ── Connectors, populated ──
            var (w, t) = await Open(width, height, new()
            {
                ["/mcp disable web"] = "Server \"web\" disabled (project config).",
                ["/mcp smithery-search"] = "GitHub (@smithery-ai/github) — Access the GitHub API: repositories, issues, pull requests and more\nMemory (@modelcontextprotocol/memory)",
            }, PluginsFlowTests.Cli());
            var attach = w.FindControl<Button>("AttachButton")!;
            attach.Flyout!.ShowAt(attach);
            await Settle();
            a.Take(w, $"{tag}-plus-menu");
            attach.Flyout.Hide();
            await Settle(100);

            var c = t.Vm.Connectors;
            await ShowConnectors(t.Vm);
            await a.TakePage(w, $"{tag}-connectors");

            var more = w.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("conn-more") && b.IsEffectivelyVisible);
            more.Flyout!.ShowAt(more);
            await Settle();
            a.Take(w, $"{tag}-connectors-menu");
            more.Flyout.Hide();
            await Settle(100);

            await c.Servers[0].TestCommand.ExecuteAsync(null); // passes, with its tools
            await c.Servers[1].TestCommand.ExecuteAsync(null); // fails, with omp's reason
            c.Servers[2].IsEnabled = false; // → feedback and the restart offer
            await Until(() => c.Feedback.ShowRestart, "restart offer");
            c.Servers[2].ShowSignInCommand.Execute(null);
            await Settle(300);
            await a.TakePage(w, $"{tag}-connectors-rows");
            c.Servers[1].AskRemoveCommand.Execute(null);
            await Settle(200);
            a.Take(w, $"{tag}-connectors-remove");
            c.Servers[1].CancelRemoveCommand.Execute(null);

            c.OpenAddFormCommand.Execute(null);
            c.FormName = "github";
            await c.SubmitFormCommand.ExecuteAsync(null); // no command: the form says what is missing
            await Settle(300);
            a.Take(w, $"{tag}-connectors-form");
            await a.TakeAt(w, "SubmitConnectorButton", $"{tag}-connectors-form-end");
            c.SetTransportCommand.Execute("sse");
            c.SetScopeCommand.Execute("project");
            c.FormUrl = "https://mcp.example.com/sse";
            c.FormHeaders = "X-Team: core";
            c.FormError = "";
            await Settle(300);
            a.Take(w, $"{tag}-connectors-form-remote");
            c.CancelFormCommand.Execute(null);

            await c.LoadItemsCommand.ExecuteAsync(null);
            c.SearchQuery = "github";
            await c.SearchCommand.ExecuteAsync(null);
            c.SearchResults[0].ToggleAddCommand.Execute(null);
            await Settle(300);
            await a.TakePage(w, $"{tag}-connectors-more");

            // ── Plugins, populated ──
            var p = t.Vm.Plugins;
            await ShowPlugins(t.Vm);
            await a.TakePage(w, $"{tag}-plugins");
            p.Plugins[0].IsEnabled = false;
            await Until(() => p.PluginsFeedback.ShowRestart, "restart offer");
            p.InstallSpec = "left-pad-nope";
            await p.InstallCommand.ExecuteAsync(null);
            p.Plugins[1].AskUninstallCommand.Execute(null);
            await p.Marketplaces[0].BrowseCommand.ExecuteAsync(null);
            await Settle(300);
            await a.TakePage(w, $"{tag}-plugins-states");
            await a.TakeAt(w, "MarketplaceList", $"{tag}-plugins-marketplace");
            p.Skills[0].IsEnabled = false;
            await Until(() => p.SkillsFeedback.ShowRestart, "skills restart offer");
            await Settle(300);
            await a.TakeAt(w, "SkillList", $"{tag}-plugins-skills");
            await Close(w, t);

            // ── Empty ──
            (w, t) = await Open(width, height, new() { ["/mcp list"] = "No MCP servers configured.", ["/tools"] = "* read" },
                new()
                {
                    ["plugin list --json"] = new { stdout = "{\n  \"npm\": [],\n  \"marketplace\": []\n}\n" },
                    ["plugin marketplace list"] = new { stdout = "No marketplaces configured\n\nAdd one with: omp plugin marketplace add <source>\n" },
                    ["config list --json"] = new { stdout = "{}" },
                }, projectFiles: false);
            await ShowConnectors(t.Vm);
            Assert.True(t.Vm.Connectors.ShowEmpty);
            await a.TakePage(w, $"{tag}-connectors-empty");
            await ShowPlugins(t.Vm);
            Assert.True(t.Vm.Plugins.ShowPluginsEmpty);
            await a.TakePage(w, $"{tag}-plugins-empty");
            await Close(w, t);

            // ── Errors in omp's words ──
            (w, t) = await Open(width, height, new() { ["/mcp list"] = "Failed to list MCP servers: EACCES: permission denied, open '/home/u/.omp/agent/mcp.json'" },
                new()
                {
                    ["plugin list --json"] = new { stderr = "✘ Failed to read plugins: EACCES: permission denied, open '/home/u/.omp/plugins/package.json'\n", exit = 1 },
                    ["plugin marketplace list"] = new { stderr = "✘ Failed to list marketplaces: EACCES\n", exit = 1 },
                    ["config list --json"] = new { stderr = "✘ config.yml: bad indentation at line 3\n", exit = 1 },
                });
            await ShowConnectors(t.Vm);
            Assert.True(t.Vm.Connectors.HasLoadError);
            await a.TakePage(w, $"{tag}-connectors-error");
            await ShowPlugins(t.Vm);
            Assert.True(t.Vm.Plugins.HasPluginsError);
            await a.TakePage(w, $"{tag}-plugins-error");

            // ── omp stopped ──
            await t.Session.StopAsync();
            await Until(() => t.Vm.Phase is SessionPhase.Stopped or SessionPhase.Faulted, "omp stopped");
            t.Vm.ShowSettingsCategoryCommand.Execute("general");
            t.Vm.ShowSettingsCategoryCommand.Execute("connectors");
            await Until(() => t.Vm.Connectors.ShowNotRunning, "not running");
            await Settle(300);
            a.Take(w, $"{tag}-connectors-not-running");
            await Close(w, t);
        }
        finally
        {
            MainViewModel.ApplyTheme("light");
        }
        if (Dir is not null) File.AppendAllText(Path.Combine(Dir, "connectors-audit.tsv"), a.Report.ToString());
        Assert.True(a.Count == 0, $"{a.Count} layout findings:\n{a.Report}");
    }
}
