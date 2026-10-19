#if OMBRA_FEATURE_SMOKE
using Avalonia.Controls;
using Avalonia.VisualTree;
using Ombra.App.Localization;
using Ombra.App.ViewModels;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] PluginsHubSections() => [("plugins-hub", PluginsHub)];

        /// <summary>
        /// The Plugins page: each tab in light and dark, the Add menu, the catalog, a plugin's and a server's page, and an
        /// MCP server that wants a sign-in (the stand-in model server's /mcp-sign-in refuses with 401).
        /// </summary>
        private async Task PluginsHub()
        {
            var hub = vm.PluginsHub;
            vm.SettingsTheme = "light";
            vm.OpenPluginsPageCommand.Execute(null);
            Check("plugins-loaded", await Until(() => vm.Plugins.HasLoaded && !vm.Plugins.IsLoading, 60));
            Check("mcp-loaded", await Until(() => vm.Connectors.Servers.Count >= 2 && !vm.Connectors.Servers.Any(s => s.IsWorking), 60),
                string.Join(" | ", vm.Connectors.Servers.Select(s => $"{s.Name}:{s.State}")));
            Check("one-rail-entry", vm.IsPluginsRailOn && hub.IsPluginsTab);
            Log($"counts plugins={hub.PluginsCount} mcp={hub.McpCount} skills={hub.SkillsCount}");

            foreach (var theme in new[] { "light", "dark" })
            {
                vm.SettingsTheme = theme;
                foreach (var tab in new[] { "plugins", "mcp", "skills" })
                {
                    hub.ShowTabCommand.Execute(tab);
                    await Task.Delay(700);
                    Shot($"51-{tab}-{theme}");
                }
            }

            // Add ▾
            vm.SettingsTheme = "light";
            hub.ShowTabCommand.Execute("plugins");
            await Task.Delay(500);
            var add = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "AddButton" && b.IsEffectivelyVisible);
            if (Check("add-button", add?.Flyout is not null))
            {
                add!.Flyout!.ShowAt(add);
                await Task.Delay(600);
                ShotScreenRegion("52-add-menu-light");
                add.Flyout.Hide();
                await Task.Delay(300);
            }

            // Search on the tab row
            hub.Search = "probe";
            hub.ShowTabCommand.Execute("mcp");
            await Task.Delay(600);
            Check("search-filters", hub.ShownServers.Count == 1, $"shown={hub.ShownServers.Count}");
            Shot("53-search-light");
            hub.Search = "";
            // Catalog: a local marketplace with categories and a featured plugin, one of its plugins installed
            vm.Plugins.MarketplaceSource = SmokeMarketplace();
            await vm.Plugins.AddMarketplaceCommand.ExecuteAsync(null);
            Check("marketplace-added", await Until(() => vm.Plugins.Marketplaces.Any(m => m.Name == "smoke-mkt"), 60), vm.Plugins.MarketplaceFormError);
            await hub.OpenCatalogCommand.ExecuteAsync(null);
            await Until(() => !vm.Plugins.IsCatalogBusy && vm.Plugins.CatalogSections.Count > 0, 60);
            var titles = vm.Plugins.CatalogSections.Select(s => s.Title).ToList();
            Check("catalog-by-category", titles.Count >= 3 && titles[0] == Loc.T("Popular") && titles[^1] == Loc.T("Other"), string.Join(",", titles));
            if (vm.Plugins.CatalogSections.SelectMany(s => s.Plugins).FirstOrDefault(p => p.Name == "lint-kit" && p.CanInstall) is { } lint)
            {
                await lint.InstallCommand.ExecuteAsync(null);
                Check("catalog-install", await Until(() => vm.Plugins.Plugins.Any(p => p.Name == "lint-kit"), 60), vm.Plugins.MarketplacesFeedback.Notice);
                await Until(() => !vm.Plugins.IsLoading, 30);
            }
            await Task.Delay(800);
            Check("catalog-shown", hub.ShowCatalog, $"sections={string.Join(",", vm.Plugins.CatalogSections.Select(s => $"{s.Title}:{s.Plugins.Count}"))}");
            Shot("54-catalog-light");
            vm.SettingsTheme = "dark";
            await Task.Delay(600);
            Shot("54-catalog-dark");
            vm.SettingsTheme = "light";
            hub.BackToListCommand.Execute(null);

            // A plugin's page (the first installed, if any) and a server's page
            if (vm.Plugins.Plugins.FirstOrDefault() is { } plugin)
            {
                await hub.OpenPluginCommand.ExecuteAsync(plugin);
                await Task.Delay(800);
                Check("plugin-details", hub.ShowDetails);
                Shot("55-plugin-details-light");
                hub.BackToListCommand.Execute(null);
            }
            else Log("INFO no installed plugin: no plugin details shot");
            if (vm.Connectors.Servers.FirstOrDefault(s => s.Name == "probe") is { } probe)
            {
                hub.OpenServerCommand.Execute(probe);
                await Task.Delay(800);
                Check("server-details", hub.ShowDetails);
                Shot("55-server-details-light");
                hub.BackToListCommand.Execute(null);
            }

            // An MCP server that wants a sign-in
            var port = Environment.GetEnvironmentVariable("OMBRA_FEATURE_SMOKE_MOCKPORT");
            if (Check("mock-port", port is { Length: > 0 }))
            {
                var c = vm.Connectors;
                hub.AddMcpServerCommand.Execute(null);
                c.SetTransportCommand.Execute("http");
                c.FormName = "needs-sign-in";
                c.FormUrl = $"http://127.0.0.1:{port}/mcp-sign-in";
                await Task.Delay(400);
                Shot("56-mcp-form-light");
                await c.SubmitFormCommand.ExecuteAsync(null);
                var added = await Until(() => c.Servers.Any(s => s.Name == "needs-sign-in"), 30);
                Check("sign-in-server-added", added, c.FormError);
                if (c.Servers.FirstOrDefault(s => s.Name == "needs-sign-in") is { } signIn)
                {
                    if (signIn.CanTest) await signIn.TestCommand.ExecuteAsync(null);
                    await Until(() => !signIn.IsWorking, 40);
                    Check("sign-in-offered", signIn.SuggestSignIn, $"state={signIn.State} auth={signIn.Auth} failure={signIn.Failure}");
                    hub.ShowTabCommand.Execute("mcp");
                    await Task.Delay(800);
                    Shot("57-mcp-sign-in-light");
                    vm.SettingsTheme = "dark";
                    await Task.Delay(600);
                    Shot("57-mcp-sign-in-dark");
                    vm.SettingsTheme = "light";
                    hub.OpenServerCommand.Execute(signIn);
                    await Task.Delay(800);
                    Shot("57-mcp-sign-in-details-light");
                    hub.BackToListCommand.Execute(null);
                    await signIn.RemoveCommand.ExecuteAsync(null);
                }
            }
        }

        /// <summary>A Claude Code style marketplace folder: four plugins, two categories, one featured, one with no category.</summary>
        private string SmokeMarketplace()
        {
            var root = Path.Combine(dir, "smoke-mkt");
            (string Name, string? Category, bool Featured, string Description)[] plugins =
            [
                ("pr-review", "development", true, "Reviews pull requests before you merge them"),
                ("lint-kit", "development", false, "Lint commands for any project"),
                ("notes", "productivity", false, "Keeps meeting notes in the project"),
                ("hello-skill", null, false, "Adds a skill that says hello"),
            ];
            foreach (var p in plugins)
            {
                Directory.CreateDirectory(Path.Combine(root, p.Name, ".claude-plugin"));
                Directory.CreateDirectory(Path.Combine(root, p.Name, "commands"));
                File.WriteAllText(Path.Combine(root, p.Name, ".claude-plugin", "plugin.json"),
                    System.Text.Json.JsonSerializer.Serialize(new { name = p.Name, version = "1.0.0", description = p.Description }));
                File.WriteAllText(Path.Combine(root, p.Name, "commands", p.Name + ".md"), $"---\ndescription: {p.Description}\n---\nSay hello.\n");
            }
            Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
            var entries = plugins.Select(p => new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = p.Name, ["source"] = "./" + p.Name, ["description"] = p.Description, ["version"] = "1.0.0",
                ["category"] = p.Category, ["featured"] = p.Featured ? true : null,
            });
            var json = new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = "smoke-mkt", ["owner"] = new System.Text.Json.Nodes.JsonObject { ["name"] = "smoke" },
                ["plugins"] = new System.Text.Json.Nodes.JsonArray([.. entries]),
            };
            File.WriteAllText(Path.Combine(root, ".claude-plugin", "marketplace.json"), json.ToJsonString());
            return root;
        }
    }
}
#endif
