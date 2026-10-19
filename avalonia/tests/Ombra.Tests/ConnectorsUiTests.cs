using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>
/// The Plugins page (plugins, MCP servers and skills on tabs) in the real window: the rail and the composer's "+" menu
/// open it, and every state (each tab's list, the Add menu, the forms, the catalog, the details pages, empty, error, omp
/// not running) is measured by <see cref="LayoutAudit"/> in both themes, wide and narrow, in English and Russian. With
/// OMBRA_REVIEW_DIR set the screenshots go to &lt;dir&gt;/plugins.
/// </summary>
public sealed class ConnectorsUiTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("OMBRA_REVIEW_DIR");

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
                Directory.CreateDirectory(Path.Combine(Dir, "plugins"));
                using var f = File.Create(Path.Combine(Dir, "plugins", screen + ".png"));
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
            var target = w.GetVisualDescendants().OfType<Control>().First(c => c.Name == control && c.IsEffectivelyVisible);
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

    private static async Task ShowMcp(MainViewModel vm)
    {
        await vm.OpenConnectorsPageCommand.ExecuteAsync(null);
        await Until(() => vm.Connectors.HasLoaded && !vm.Connectors.IsLoading, "MCP servers loaded");
        await Settle(300);
    }

    private static async Task ShowPlugins(MainViewModel vm)
    {
        await vm.OpenPluginsPageCommand.ExecuteAsync(null);
        await Until(() => vm.Plugins.HasLoaded && !vm.Plugins.IsLoading, "plugins loaded");
        await Settle(300);
    }

    private static async Task ShowSkills(MainViewModel vm)
    {
        await vm.OpenSkillsPageCommand.ExecuteAsync(null);
        await Until(() => vm.Plugins.HasLoaded && !vm.Plugins.IsLoading, "skills loaded");
        await Settle(300);
    }

    private static Button MenuItem(Button owner, string name) =>
        ((Flyout)owner.Flyout!).Content is Control c ? c.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == name) : throw new InvalidOperationException(name);

    /// <summary>Runs <paramref name="body"/> with the app in <paramref name="language"/> (en or ru), then back in the suite's language.</summary>
    private static async Task InLanguage(string language, Func<Task> body)
    {
        var settingsPath = Path.Combine(TestProcesses.TempDir("plugins-ui-" + language), "settings.json");
        var previous = Loc.Current;
        Loc.Load(language, settingsPath);
        try { await body(); }
        finally { Loc.Load(previous, settingsPath); }
    }

    [AvaloniaFact]
    public async Task One_rail_entry_and_the_plus_menu_open_the_plugins_page_on_its_tabs()
    {
        var (w, t) = await Open(1180, 760, cli: PluginsFlowTests.Cli());
        // Plugins, MCP servers and skills: one rail entry, one settings page
        Assert.NotNull(w.FindControl<Button>("RailPluginsButton"));
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<Button>(), b => b.Name is "RailConnectorsButton" or "RailSkillsButton");
        Assert.DoesNotContain(t.Vm.SettingsCategories, c => c.Key == "connectors");

        w.FindControl<Button>("RailPluginsButton")!.Command!.Execute(null);
        await Until(() => t.Vm.IsSettingsOpen && t.Vm.SettingsCategory == "plugins" && t.Vm.PluginsHub.IsPluginsTab, "plugins tab");
        Assert.True(t.Vm.IsPluginsRailOn);
        await Until(() => t.Vm.Plugins.Plugins.Count == 2 && t.Vm.PluginsHub.ShownPlugins.Count == 2, "plugins listed");
        t.Vm.CloseSettingsCommand.Execute(null);

        var attach = w.FindControl<Button>("AttachButton")!;
        attach.Flyout!.ShowAt(attach);
        await Settle();
        MenuItem(attach, "ConnectorsItem").Command!.Execute(null);
        await Until(() => t.Vm.IsSettingsOpen && t.Vm.SettingsCategory == "plugins" && t.Vm.PluginsHub.IsMcpTab, "MCP tab");
        await Until(() => t.Vm.Connectors.Servers.Count == 5 && t.Vm.PluginsHub.McpCount == 5, "servers listed");
        Assert.True(w.FindControl<Border>("SettingsPage")!.IsVisible);

        // The tabs count what they hold; the search narrows the shown tab
        await Until(() => t.Vm.PluginsHub.SkillsCount == t.Vm.Plugins.Skills.Count && t.Vm.PluginsHub.PluginsCount == 2, "counts");
        t.Vm.PluginsHub.Search = "ech";
        Assert.Equal(["echo"], t.Vm.PluginsHub.ShownServers.Select(s => s.Name));
        t.Vm.PluginsHub.Search = "nothing-like-it";
        Assert.True(t.Vm.PluginsHub.ShowNoMatches);
        t.Vm.PluginsHub.Search = "";
        await Close(w, t);
    }

    [AvaloniaFact]
    public async Task Add_opens_the_right_form_and_the_catalog_lists_marketplace_plugins()
    {
        var (w, t) = await Open(1180, 760, cli: PluginsFlowTests.Cli());
        var hub = t.Vm.PluginsHub;
        await ShowPlugins(t.Vm);

        hub.AddMcpServerCommand.Execute(null);
        Assert.True(hub.IsMcpTab && t.Vm.Connectors.IsFormOpen);
        t.Vm.Connectors.CancelFormCommand.Execute(null);
        hub.AddSkillCommand.Execute(null);
        Assert.True(hub.IsSkillsTab && hub.IsSkillSourcesOpen);
        hub.AddPluginCommand.Execute(null);
        Assert.True(hub.IsPluginsTab && hub.IsInstallOpen);

        await hub.OpenCatalogCommand.ExecuteAsync(null);
        Assert.True(hub.ShowCatalog);
        var market = t.Vm.Plugins.Marketplaces.Single();
        await Until(() => market.Plugins.Count == 2, "catalog read");
        // The fixture's catalog gives no categories: one "Other" section with both
        Assert.Equal([Loc.T("Other")], t.Vm.Plugins.CatalogSections.Select(s => s.Title));
        Assert.Equal(2, t.Vm.Plugins.CatalogSections[0].Plugins.Count);
        Assert.True(market.Plugins.Single(p => p.Name == "hello-skill").IsInstalled); // "⋯": its page
        await hub.OpenInstalledCommand.ExecuteAsync(market.Plugins.Single(p => p.Name == "hello-skill"));
        Assert.True(hub.ShowDetails && hub.SelectedPlugin?.Name == "hello-skill");
        hub.BackToListCommand.Execute(null);
        Assert.True(hub.ShowList && hub.SelectedPlugin is null);
        await Close(w, t);
    }

    [AvaloniaFact]
    public async Task Untrusted_project_items_say_they_need_folder_trust()
    {
        var (w, t) = await Open(1180, 760, cli: PluginsFlowTests.Cli());
        await ShowMcp(t.Vm);
        // A project server in a folder omp started restricted (the session's launch says so)
        var launch = new ProjectLaunch(t.Project, restricted: true);
        t.Vm.PluginsHub.MarkTrust(launch);
        var broken = t.Vm.Connectors.Servers.Single(s => s.Name == "broken");
        var echo = t.Vm.Connectors.Servers.Single(s => s.Name == "echo");
        Assert.True(broken.NeedsTrust);
        Assert.False(echo.NeedsTrust);
        await Settle(200);
        Assert.Contains(w.GetVisualDescendants().OfType<TextBlock>(), x => x.IsEffectivelyVisible && x.Text == Loc.T("Requires folder trust"));
        await Close(w, t);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public Task Controls_are_named_and_labelled_in_the_apps_language(string language) => InLanguage(language, async () =>
    {
        var (w, t) = await Open(1180, 760, cli: PluginsFlowTests.Cli());
        List<string?> Names() => [.. w.GetVisualDescendants().Where(c => c.IsEffectivelyVisible).OfType<Control>().Select(Avalonia.Automation.AutomationProperties.GetName)];
        await ShowMcp(t.Vm);
        var server = t.Vm.Connectors.Servers.First(s => s.HasMenu);
        var names = Names();
        Assert.Contains(Loc.F("Use {0}", server.Name), names);
        Assert.Contains(Loc.F("Details for {0}", server.Name), names);
        Assert.Contains(Loc.T("Browse catalog"), names);
        Assert.Contains(Loc.T("Add"), names);

        await ShowPlugins(t.Vm);
        var p = t.Vm.Plugins;
        names = Names();
        Assert.Contains(Loc.F("Use {0}", p.Plugins[0].Name), names);
        Assert.Contains(Loc.F("Details for {0}", p.Plugins[0].Name), names);

        await ShowSkills(t.Vm);
        names = Names();
        Assert.Contains(Loc.F("Skill {0}", p.Skills[0].Name), names);

        await t.Vm.PluginsHub.OpenCatalogCommand.ExecuteAsync(null);
        await Until(() => p.Marketplaces[0].Plugins.Count == 2, "catalog");
        await Settle(200);
        names = Names();
        Assert.Contains(Loc.F("Install {0}", "lint-kit@local-mkt"), names);
        Assert.Contains(Loc.F("More for {0}", p.Marketplaces[0].Name), names);
        if (language == "ru")
            foreach (var english in new[] { "Browse catalog", "Manage plugins, skills and MCP", "Requires folder trust", "What it adds", "Plugin sources", "Personal", "Project" })
                Assert.NotEqual(english, Loc.T(english));
        await Close(w, t);
    });

    [AvaloniaTheory]
    [MemberData(nameof(DesignMatrix.Standard), MemberType = typeof(DesignMatrix))]
    public Task Plugins_page_screens_are_aligned(string theme, double width, double height) => Screens(theme, width, height, "en");

    [AvaloniaTheory]
    [InlineData("light", 1180, 760)]
    [InlineData("dark", 640, 600)]
    public Task Plugins_page_screens_are_aligned_in_russian(string theme, double width, double height) =>
        InLanguage("ru", () => Screens(theme, width, height, "ru"));

    private static async Task Screens(string theme, double width, double height, string language)
    {
        MainViewModel.ApplyTheme(theme);
        var a = new Audit();
        var tag = $"{language}-{theme}-{width}";
        try
        {
            // ── Populated ──
            var (w, t) = await Open(width, height, new()
            {
                ["/mcp disable web"] = "Server \"web\" disabled (project config).",
                ["/mcp smithery-search"] = "GitHub (@smithery-ai/github) — Access the GitHub API: repositories, issues, pull requests and more\nMemory (@modelcontextprotocol/memory)",
            }, PluginsFlowTests.Cli());
            var hub = t.Vm.PluginsHub;
            var attach = w.FindControl<Button>("AttachButton")!;
            attach.Flyout!.ShowAt(attach);
            await Settle();
            a.Take(w, $"{tag}-plus-menu");
            attach.Flyout.Hide();
            await Settle(100);

            // Plugins tab, the Add menu, the install form
            await ShowPlugins(t.Vm);
            await a.TakePage(w, $"{tag}-plugins");
            var add = w.FindControl<Button>("AddButton") ?? w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AddButton");
            add.Flyout!.ShowAt(add);
            await Settle();
            a.Take(w, $"{tag}-add-menu");
            add.Flyout.Hide();
            await Settle(100);
            var p = t.Vm.Plugins;
            p.Plugins[0].IsEnabled = false;
            await Until(() => p.PluginsFeedback.ShowRestart, "restart offer");
            hub.AddPluginCommand.Execute(null);
            p.InstallSpec = "left-pad-nope";
            await p.InstallCommand.ExecuteAsync(null);
            await Settle(300);
            await a.TakePage(w, $"{tag}-plugins-states");
            hub.CloseInstallCommand.Execute(null);

            // A plugin's page: what it brings, details, uninstall
            await hub.OpenPluginCommand.ExecuteAsync(p.Plugins[0]);
            await Settle(300);
            await a.TakePage(w, $"{tag}-plugin-details");
            p.Plugins[0].AskUninstallCommand.Execute(null);
            await Settle(200);
            await a.TakeAt(w, "PluginActions", $"{tag}-plugin-uninstall");
            p.Plugins[0].CancelUninstallCommand.Execute(null);
            await hub.OpenPluginCommand.ExecuteAsync(p.Plugins[1]); // a marketplace plugin with an update
            await Settle(300);
            await a.TakePage(w, $"{tag}-plugin-details-update");

            // Catalog
            await hub.OpenCatalogCommand.ExecuteAsync(null);
            await Until(() => p.Marketplaces[0].Plugins.Count == 2, "catalog");
            t.Vm.Connectors.SearchQuery = "github";
            await t.Vm.Connectors.SearchCommand.ExecuteAsync(null);
            await Settle(300);
            await a.TakePage(w, $"{tag}-catalog");
            t.Vm.Connectors.SearchResults[0].ToggleAddCommand.Execute(null);
            p.Marketplaces[0].AskRemoveCommand.Execute(null);
            await Settle(200);
            await a.TakeAt(w, "SmitheryResults", $"{tag}-catalog-add-mcp");
            p.Marketplaces[0].CancelRemoveCommand.Execute(null);

            // MCP tab: rows, the form, a server's page, the more section
            var c = t.Vm.Connectors;
            await ShowMcp(t.Vm);
            await a.TakePage(w, $"{tag}-mcp");
            await c.Servers[0].TestCommand.ExecuteAsync(null); // passes, with its tools
            await c.Servers[1].TestCommand.ExecuteAsync(null); // fails, with omp's reason
            c.Servers[2].IsEnabled = false; // → feedback and the restart offer
            await Until(() => c.Feedback.ShowRestart, "restart offer");
            await Settle(300);
            await a.TakePage(w, $"{tag}-mcp-rows");
            hub.OpenServerCommand.Execute(c.Servers[0]);
            await Settle(300);
            await a.TakePage(w, $"{tag}-mcp-details");
            hub.OpenServerCommand.Execute(c.Servers[1]);
            c.Servers[1].AskRemoveCommand.Execute(null);
            await Settle(300);
            await a.TakePage(w, $"{tag}-mcp-details-failed");
            c.Servers[1].CancelRemoveCommand.Execute(null);
            hub.BackToListCommand.Execute(null);

            c.OpenAddFormCommand.Execute(null);
            c.FormName = "github";
            await c.SubmitFormCommand.ExecuteAsync(null); // no command: the form says what is missing
            await Settle(300);
            a.Take(w, $"{tag}-mcp-form");
            await a.TakeAt(w, "SubmitConnectorButton", $"{tag}-mcp-form-end");
            c.SetTransportCommand.Execute("sse");
            c.SetScopeCommand.Execute("project");
            c.FormUrl = "https://mcp.example.com/sse";
            c.FormHeaders = "X-Team: core";
            c.FormError = "";
            await Settle(300);
            a.Take(w, $"{tag}-mcp-form-remote");
            c.CancelFormCommand.Execute(null);
            hub.ToggleMcpMoreCommand.Execute(null);
            await c.LoadItemsCommand.ExecuteAsync(null);
            await Settle(300);
            await a.TakePage(w, $"{tag}-mcp-more");

            // Skills tab, a skill's page, the sources
            await ShowSkills(t.Vm);
            p.Skills[0].IsEnabled = false;
            await Until(() => p.SkillsFeedback.ShowRestart, "skills restart offer");
            await Settle(300);
            await a.TakePage(w, $"{tag}-skills");
            await hub.OpenSkillCommand.ExecuteAsync(p.Skills[0]);
            await Settle(300);
            await a.TakePage(w, $"{tag}-skill-details");
            hub.BackToListCommand.Execute(null);
            hub.ToggleSkillSourcesCommand.Execute(null);
            await Settle(300);
            await a.TakePage(w, $"{tag}-skill-sources");
            await Close(w, t);

            // ── Empty ──
            (w, t) = await Open(width, height, new() { ["/mcp list"] = "No MCP servers configured.", ["/tools"] = "* read" },
                new()
                {
                    ["plugin list --json"] = new { stdout = "{\n  \"npm\": [],\n  \"marketplace\": []\n}\n" },
                    ["plugin marketplace list"] = new { stdout = "No marketplaces configured\n\nAdd one with: omp plugin marketplace add <source>\n" },
                    ["config list --json"] = new { stdout = "{}" },
                }, projectFiles: false);
            await ShowMcp(t.Vm);
            Assert.True(t.Vm.Connectors.ShowEmpty);
            await a.TakePage(w, $"{tag}-mcp-empty");
            await ShowPlugins(t.Vm);
            Assert.True(t.Vm.Plugins.ShowPluginsEmpty);
            await a.TakePage(w, $"{tag}-plugins-empty");
            await t.Vm.PluginsHub.OpenCatalogCommand.ExecuteAsync(null);
            await Settle(300);
            Assert.True(t.Vm.Plugins.ShowMarketplacesEmpty);
            await a.TakePage(w, $"{tag}-catalog-empty");
            await Close(w, t);

            // ── Errors in omp's words ──
            (w, t) = await Open(width, height, new() { ["/mcp list"] = "Failed to list MCP servers: EACCES: permission denied, open '/home/u/.omp/agent/mcp.json'" },
                new()
                {
                    ["plugin list --json"] = new { stderr = "✘ Failed to read plugins: EACCES: permission denied, open '/home/u/.omp/plugins/package.json'\n", exit = 1 },
                    ["plugin marketplace list"] = new { stderr = "✘ Failed to list marketplaces: EACCES\n", exit = 1 },
                    ["config list --json"] = new { stderr = "✘ config.yml: bad indentation at line 3\n", exit = 1 },
                });
            await ShowMcp(t.Vm);
            Assert.True(t.Vm.Connectors.HasLoadError);
            await a.TakePage(w, $"{tag}-mcp-error");
            await ShowPlugins(t.Vm);
            Assert.True(t.Vm.Plugins.HasPluginsError);
            await a.TakePage(w, $"{tag}-plugins-error");

            // ── omp stopped ──
            await t.Session.StopAsync();
            await Until(() => t.Vm.Phase is SessionPhase.Stopped or SessionPhase.Faulted, "omp stopped");
            t.Vm.ShowSettingsCategoryCommand.Execute("general");
            await t.Vm.OpenConnectorsPageCommand.ExecuteAsync(null);
            await Until(() => t.Vm.Connectors.ShowNotRunning, "not running");
            await Settle(300);
            a.Take(w, $"{tag}-mcp-not-running");
            await Close(w, t);
        }
        finally
        {
            MainViewModel.ApplyTheme("light");
        }
        if (Dir is not null) File.AppendAllText(Path.Combine(Dir, "plugins-audit.tsv"), a.Report.ToString());
        Assert.True(a.Count == 0, $"{a.Count} layout findings:\n{a.Report}");
    }
}
