using Avalonia.Headless.XUnit;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>
/// MainViewModel.OpenLink, the one way Ombra opens a link the user asked for: web links go to the right panel's Browser
/// tool (a tab of their own, next to the chat), the Browser page or the system's browser only when asked; file:, folders
/// and mailto: go to the system as before; the Nightly consent card and install banner keep the panel, a pane without an
/// engine hands links to the system. The clicks and the ⌘-click menu in a chat: ContextMenuUiTests.
/// </summary>
public sealed class LinkRoutingTests
{
    private static async Task InLanguage(string language, Func<Task> body)
    {
        var settingsPath = Path.Combine(TestProcesses.TempDir("link-routing-" + language), "ombra.local.json");
        var previous = Loc.Current;
        Loc.Load(language, settingsPath);
        try
        {
            if (language == "ru")
                foreach (var english in new[] { "Open in Browser page", "Open in default browser", "Open in preview" })
                    Assert.NotEqual(english, Loc.T(english));
            await body();
        }
        finally { Loc.Load(previous, settingsPath); }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public Task Web_links_open_in_the_right_panel_each_in_a_tab_and_the_rest_go_to_the_system(string language) => InLanguage(language, async () =>
    {
        await using var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var system = new List<string>();
        vm.OpenUrlRequested += system.Add;

        vm.OpenLink("https://example.com/a");
        Assert.Equal(SidePane.Browser, vm.ActivePane);
        Assert.Equal("https://example.com/a", vm.Preview.CurrentUrl!.AbsoluteUri);
        var first = vm.Preview.ActiveTab;
        vm.OpenLink("https://example.com/b");
        Assert.NotSame(first, vm.Preview.ActiveTab);
        Assert.True(vm.Preview.ActiveTab.IsOpenedByUser);
        Assert.Equal("https://example.com/b", vm.Preview.CurrentUrl!.AbsoluteUri);
        Assert.Equal("https://example.com/a", first.Url!.AbsoluteUri);
        Assert.Equal(2, vm.Preview.Tabs.Count);
        Assert.False(vm.Preview.FollowsAgent);
        Assert.False(vm.ShowBrowserPage);
        Assert.Empty(system);

        // Not the web: the system, as before (Finder, the mail app); the panel is left alone
        var folder = new Uri(TestProcesses.TempDir("link-routing-folder"));
        vm.OpenLink(folder);
        vm.OpenLink("mailto:someone@example.com");
        Assert.Equal([folder.AbsoluteUri, "mailto:someone@example.com"], system);
        Assert.Equal(2, vm.Preview.Tabs.Count);

        // Asked for: the default browser, the Browser page (its built-in tabs without Nightly)
        vm.OpenLink("https://example.com/c", LinkTarget.SystemBrowser);
        Assert.Equal("https://example.com/c", system[^1]);
        vm.OpenLink("https://example.com/d", LinkTarget.BrowserPage);
        Assert.True(vm.ShowBrowserPage);
        Assert.Equal("https://example.com/d", vm.Browser.CurrentUrl!.AbsoluteUri);
        Assert.Equal(3, system.Count);

        // A click from the Browser page or settings (a settings link) comes back to the chat, with the panel
        vm.OpenLink("https://example.com/e");
        Assert.False(vm.ShowBrowserPage);
        Assert.Equal("https://example.com/e", vm.Preview.CurrentUrl!.AbsoluteUri);
        vm.IsSettingsOpen = true;
        vm.OpenLink("https://cli.github.com/");
        Assert.False(vm.IsSettingsOpen);
        Assert.Equal(SidePane.Browser, vm.ActivePane);
        Assert.Equal("https://cli.github.com/", vm.Preview.CurrentUrl!.AbsoluteUri);
        Assert.Equal(3, system.Count);
    });

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public Task Links_keep_the_panel_while_it_asks_about_Nightly_or_offers_its_install_and_go_to_the_system_without_an_engine(string language) => InLanguage(language, async () =>
    {
        await using var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var system = new List<string>();
        vm.OpenUrlRequested += system.Add;
        var p = vm.Preview;

        // The first-run question: the page shows in the built-in preview under the card, which stays
        p.NeedsNightlyConsent = true;
        vm.OpenLink("https://example.com/consent");
        Assert.Equal("https://example.com/consent", p.CurrentUrl!.AbsoluteUri);
        Assert.True(p.NeedsNightlyConsent);

        // Nightly chosen but not installed: the banner offers the install, the page shows in the built-in preview
        p.DeclineNightlyCommand.Execute(null);
        p.CheckNightly = () => (false, Loc.T("Nightly is not installed."), true);
        p.WantsNightly = true;
        Assert.True(p.ShowNightlyFallback);
        Assert.True(p.InstallNightlyCommand.CanExecute(null));
        Assert.False(p.UseNightly);
        vm.OpenLink("https://example.com/install");
        Assert.Equal("https://example.com/install", p.CurrentUrl!.AbsoluteUri);
        Assert.Empty(system);

        // No engine at all (the platform's is missing): the system's browser
        var tabs = p.Tabs.Count;
        p.ReportEngineUnavailable("WebKit", "missing");
        Assert.Equal(Loc.F("The embedded browser needs {0} on this system", "WebKit"), p.FallbackText);
        vm.OpenLink("https://example.com/none");
        Assert.Equal(["https://example.com/none"], system);
        Assert.Equal(tabs, p.Tabs.Count);
    });
}
