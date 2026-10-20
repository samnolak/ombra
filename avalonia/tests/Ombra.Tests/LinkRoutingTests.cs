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
                foreach (var english in new[] { "Open in Browser page", "Open in default browser", "Open in preview", "Sign in" })
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

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public Task Sign_in_pages_open_in_Ombra_the_chats_browser_or_the_sign_in_window_over_settings(string language) => InLanguage(language, async () =>
    {
        await using var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var system = new List<string>();
        vm.OpenUrlRequested += system.Add;
        var windows = 0;
        vm.SignInWindowRequested += () => windows++;

        // In a chat (the Browser page gives way): a tab of the right panel's Browser tool, then its local callback
        vm.OpenBrowserPageCommand.Execute(null);
        vm.OpenLink("https://auth.example.com/authorize?state=s", LinkTarget.SignIn);
        Assert.False(vm.ShowBrowserPage);
        Assert.Equal(SidePane.Browser, vm.ActivePane);
        Assert.Equal("https://auth.example.com/authorize?state=s", vm.Preview.CurrentUrl!.AbsoluteUri);
        vm.OpenLink("http://localhost:8765/callback?code=x", LinkTarget.SignIn);
        Assert.Equal("http://localhost:8765/callback?code=x", vm.Preview.CurrentUrl!.AbsoluteUri);
        Assert.False(vm.Preview.HasMessage);
        Assert.Equal((0, null), (windows, vm.SignInBrowser));

        // Over Settings: Ombra's sign-in window (settings stay), its own browser with the agent pane's engine choice
        vm.IsSettingsOpen = true;
        var tabs = vm.Preview.Tabs.Count;
        vm.OpenLink("https://auth.example.com/settings", LinkTarget.SignIn);
        Assert.True(vm.IsSettingsOpen);
        Assert.Equal(1, windows);
        Assert.NotNull(vm.SignInBrowser);
        Assert.NotSame(vm.Preview, vm.SignInBrowser);
        Assert.NotSame(vm.Browser, vm.SignInBrowser);
        Assert.Equal("https://auth.example.com/settings", vm.SignInBrowser!.CurrentUrl!.AbsoluteUri);
        vm.OpenLink("http://127.0.0.1:8765/callback?code=y", LinkTarget.SignIn);
        Assert.Equal(2, windows); // the same window, in front
        Assert.Equal(2, vm.SignInBrowser.Tabs.Count);
        Assert.Equal("http://127.0.0.1:8765/callback?code=y", vm.SignInBrowser.CurrentUrl!.AbsoluteUri);
        Assert.Equal(tabs, vm.Preview.Tabs.Count);
        Assert.Empty(system);

        // Asked for (passkeys, SSO): the default browser; a window without an engine hands it there too
        vm.OpenLink("https://auth.example.com/sso", LinkTarget.SystemBrowser);
        Assert.Equal(["https://auth.example.com/sso"], system);
        vm.SignInBrowser.ReportEngineUnavailable("WebKit", "missing");
        vm.OpenLink("https://auth.example.com/none", LinkTarget.SignIn);
        Assert.Equal(["https://auth.example.com/sso", "https://auth.example.com/none"], system);
        Assert.Equal(2, windows);
    });
}
