using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>
/// Connector sign-in, reconnect and forms against the fake omp's MCP commands (FakeMcp.cs: get_mcp_servers,
/// mcp_login, mcp_login_cancel, mcp_reconnect, test_mcp_control), as the owned engine answers them.
/// </summary>
public class McpAuthTests
{
    // echo, broken and web are the servers /mcp list prints (ConnectorsFlowTests.StartAsync)
    private const string Servers = """
        { "servers": [
          { "name": "echo", "transport": "stdio", "scope": "user", "status": "connected", "toolCount": 2, "auth": "none" },
          { "name": "broken", "transport": "stdio", "scope": "project", "status": "connected", "toolCount": 0, "auth": "none" },
          { "name": "web", "transport": "sse", "scope": "project", "status": "disconnected", "toolCount": 0, "auth": "expired",
            "error": "token expired", "authUrl": "https://auth.example.com/authorize?client_id=c&state=s1", "loginTools": 3 }
        ] }
        """;

    private static Task Control(Ui ui, string json) => Control(ui.T, json);
    private static Task Emit(Ui ui, string frame) => Emit(ui.T, frame);

    private static async Task Control(ConnectorsFlowTests.Setup t, string json)
    {
        using var doc = JsonDocument.Parse(json);
        await t.Session.Connection!.RequestAsync("test_mcp_control", w =>
        {
            foreach (var p in doc.RootElement.EnumerateObject()) p.WriteTo(w);
        });
    }

    private static Task Emit(ConnectorsFlowTests.Setup t, string frame) => Control(t, $$"""{ "emit": {{frame}} }""");

    /// <summary>Where the review shots go (OMBRA_MCP_SHOTS); none without it.</summary>
    private static readonly string? ShotDir = Environment.GetEnvironmentVariable("OMBRA_MCP_SHOTS");

    internal sealed class Ui(MainWindow w, ConnectorsFlowTests.Setup t, List<string> opened) : IAsyncDisposable
    {
        public MainWindow W => w;
        public ConnectorsFlowTests.Setup T => t;
        public MainViewModel Vm => t.Vm;
        public ConnectorsViewModel C => t.Vm.Connectors;
        public List<string> Opened => opened;
        public string[] Sent => t.Sent;

        public async ValueTask DisposeAsync()
        {
            await t.Vm.DisposeAsync();
            w.Close();
        }
    }

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
            if (DateTime.UtcNow > d) throw new TimeoutException("never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The window with the fake omp (and its MCP servers) running; the Connectors page loaded when asked.</summary>
    private static async Task<Ui> Open(string servers = Servers, bool connectors = true)
    {
        var t = await ConnectorsFlowTests.StartAsync(start: false, mcp: servers);
        var opened = new List<string>();
        t.Vm.OpenUrlRequested += opened.Add;
        var w = new MainWindow { DataContext = t.Vm, Width = 1180, Height = 760 };
        w.Show();
        t.Vm.OnWindowOpened();
        await Until(() => t.Vm.Phase is SessionPhase.Ready or SessionPhase.Faulted, "omp ready");
        Assert.Equal(SessionPhase.Ready, t.Vm.Phase);
        if (connectors)
        {
            await t.Vm.OpenConnectorsPageCommand.ExecuteAsync(null); // the Plugins page on its MCP tab
            Assert.True(t.Vm.PluginsHub.IsMcpTab);
            await Until(() => t.Vm.Connectors.HasLoaded && !t.Vm.Connectors.IsLoading && t.Vm.Connectors.Servers.Count > 0, "connectors loaded");
        }
        await Settle();
        return new Ui(w, t, opened);
    }

    /// <summary>A review shot (with OMBRA_MCP_SHOTS) and the layout audit: nothing clipped or overlapping.</summary>
    private static async Task Shot(Ui ui, string name, string? scrollTo = null)
    {
        if (scrollTo is not null) ui.W.GetVisualDescendants().OfType<Control>().First(c => c.Name == scrollTo).BringIntoView();
        await Settle(300);
        if (ShotDir is not null)
        {
            Directory.CreateDirectory(ShotDir);
            using var frame = ui.W.CaptureRenderedFrame();
            using var f = File.Create(Path.Combine(ShotDir, name + ".png"));
            frame?.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        var found = LayoutAudit.Run(ui.W);
        Assert.True(found.Count == 0, LayoutAudit.Report(name, found));
    }

    [AvaloniaFact]
    public async Task Rows_show_omps_sign_in_state_and_a_server_without_tools()
    {
        await using var ui = await Open();
        var c = ui.C;

        var echo = c.Servers.Single(s => s.Name == "echo");
        Assert.Equal((ConnectorState.Connected, Loc.Plural(2, "Connected · {0} tool", "Connected · {0} tools"), false), (echo.State, echo.StateText, echo.HasAuth));
        var broken = c.Servers.Single(s => s.Name == "broken");
        Assert.Equal((ConnectorState.NoTools, Loc.T("Connected · 0 tools")), (broken.State, broken.StateText));
        Assert.True(broken.HasNoTools);
        Assert.False(broken.SuggestSignIn);
        var web = c.Servers.Single(s => s.Name == "web");
        Assert.Equal((Loc.T("Sign-in expired"), Loc.T("Sign in again"), Loc.T("Not connected: sign-in needed")), (web.AuthText, web.SignInLabel, web.StateText));
        Assert.True(web.NeedsSignIn);
        Assert.True(web.SuggestSignIn);
        Assert.Contains("get_mcp_servers", ui.Sent);
        await Shot(ui, "connectors-states", scrollTo: "ConnectorList");

        // The details page: the 0-tools warning, and the sign-in for the expired server
        ui.Vm.PluginsHub.OpenServerCommand.Execute(broken);
        await Settle(200);
        Assert.True(ui.W.GetVisualDescendants().OfType<Control>().Single(x => x.Name == "NoToolsWarning").IsEffectivelyVisible);
        await Shot(ui, "connectors-details-no-tools");
        ui.Vm.PluginsHub.OpenServerCommand.Execute(web);
        await Settle(200);
        Assert.Contains(ui.W.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(b) == Loc.F("Sign in to {0}", "web"));
        await Shot(ui, "connectors-details-sign-in");
        ui.Vm.PluginsHub.BackToListCommand.Execute(null);

        // signed_out: "Sign in"
        await Control(ui, """{ "servers": [ { "name": "web", "transport": "sse", "status": "disconnected", "toolCount": 0, "auth": "signed_out" } ] }""");
        await c.RefreshAsync();
        web = c.Servers.Single(s => s.Name == "web");
        Assert.Equal((Loc.T("Signed out"), Loc.T("Sign in")), (web.AuthText, web.SignInLabel));
    }

    [AvaloniaFact]
    public async Task Sign_in_opens_only_its_own_https_page_and_connects()
    {
        await using var ui = await Open();
        var (c, vm) = (ui.C, ui.Vm);
        var web = c.Servers.Single(s => s.Name == "web");

        // From Settings (no chat on screen): Ombra's sign-in window shows the page, nothing goes to the default browser;
        // the window closes once omp answers
        string? shown = null;
        vm.SignInWindowRequested += () => shown ??= vm.SignInBrowser?.CurrentUrl?.AbsoluteUri;
        await web.SignInCommand.ExecuteAsync(null);
        Assert.Equal("https://auth.example.com/authorize?client_id=c&state=s1", shown);
        Assert.Empty(ui.Opened);
        Assert.Null(vm.SignInBrowser);
        await Settle(100);
        Assert.Empty(ui.W.OwnedWindows);
        Assert.True(vm.IsSettingsOpen);
        Assert.Null(vm.PendingUrl); // opened, not left on the link card
        Assert.Contains("mcp_login web", ui.Sent);
        Assert.Equal(("signed_in", ConnectorState.Connected, Loc.Plural(3, "Connected · {0} tool", "Connected · {0} tools")), (web.Auth, web.State, web.StateText));
        Assert.False(web.IsSigningIn);
        Assert.Equal(Loc.Plural(3, "Signed in to “{1}”: {0} tool ready.", "Signed in to “{1}”: {0} tools ready.", "web"), c.Feedback.Notice);
        Assert.True(web.CanSignInAgain);
        Assert.False(web.SuggestSignIn);
        await Shot(ui, "connectors-signed-in", scrollTo: "ConnectorList");

        // A sign-in page the user didn't ask for here: the link card, never the browser by itself
        await Emit(ui, """{ "type": "extension_ui_request", "id": "o1", "method": "open_url", "url": "https://auth.example.com/other", "mcpServer": "web" }""");
        await Until(() => vm.PendingUrl is not null, "link card");
        Assert.Equal("https://auth.example.com/other", vm.PendingUrl);
        Assert.Empty(ui.Opened);

        // The card (in the chat): its Open, the chat's Browser tool; its "Open in default browser", the system's browser
        vm.ShowChatsCommand.Execute(null);
        vm.OpenPendingUrlCommand.Execute(null);
        Assert.Null(vm.PendingUrl);
        Assert.False(vm.IsSettingsOpen);
        Assert.Equal(SidePane.Browser, vm.ActivePane);
        Assert.Equal("https://auth.example.com/other", vm.Preview.CurrentUrl!.AbsoluteUri);
        Assert.Empty(ui.Opened);
        await Emit(ui, """{ "type": "extension_ui_request", "id": "o2", "method": "open_url", "url": "https://auth.example.com/sso", "mcpServer": "web" }""");
        await Until(() => vm.PendingUrl is not null, "link card");
        vm.OpenPendingUrlInDefaultBrowserCommand.Execute(null);
        Assert.Equal(["https://auth.example.com/sso"], ui.Opened);
        Assert.Null(vm.PendingUrl);
    }

    [AvaloniaFact]
    public async Task Sign_in_in_the_default_browser_is_the_sign_in_buttons_menu()
    {
        await using var ui = await Open();
        var (c, vm) = (ui.C, ui.Vm);
        var web = c.Servers.Single(s => s.Name == "web");
        var requested = 0;
        vm.SignInWindowRequested += () => requested++;

        await web.SignInInDefaultBrowserCommand.ExecuteAsync(null);
        Assert.Equal(["https://auth.example.com/authorize?client_id=c&state=s1"], ui.Opened);
        Assert.Equal((0, null), (requested, vm.SignInBrowser));
        Assert.Equal("signed_in", web.Auth);

        // The sign-in buttons carry it as their right-click menu
        ui.Vm.PluginsHub.OpenServerCommand.Execute(web);
        await Settle(200);
        var again = ui.W.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SignInAgainButton");
        Assert.NotNull(Ombra.App.Controls.ContextMenus.GetDefaultBrowserSignIn(again));
        Assert.NotNull(again.ContextFlyout);
    }

    [AvaloniaFact]
    public async Task A_sign_in_page_that_is_not_https_or_local_never_opens()
    {
        await using var ui = await Open(Servers.Replace("https://auth.example.com/authorize?client_id=c&state=s1", "http://auth.example.com/authorize"));
        var vm = ui.Vm;

        await ui.C.Servers.Single(s => s.Name == "web").SignInCommand.ExecuteAsync(null);
        await Until(() => vm.PendingUrl is not null, "link card");
        Assert.Empty(ui.Opened);
        Assert.Equal("http://auth.example.com/authorize", vm.PendingUrl);
        vm.OpenPendingUrlCommand.Execute(null); // the card's button refuses plain http to another computer too
        Assert.Empty(ui.Opened);
        Assert.Null(vm.PendingUrl);

        Assert.True(MainViewModel.IsSafeBrowserUrl("http://127.0.0.1:5173/callback", out Uri? _));
        Assert.True(MainViewModel.IsSafeBrowserUrl("http://localhost:8080/cb", out Uri? _));
        Assert.True(MainViewModel.IsSafeBrowserUrl("https://example.com/a", out Uri? _));
        Assert.False(MainViewModel.IsSafeBrowserUrl("https://user:pw@example.com/a", out Uri? _));
        Assert.False(MainViewModel.IsSafeBrowserUrl("file:///etc/passwd", out Uri? _));
        Assert.False(MainViewModel.IsSafeBrowserUrl("javascript:alert(1)", out Uri? _));
    }

    [AvaloniaFact]
    public async Task Cancel_stops_a_sign_in_waiting_on_the_browser()
    {
        await using var ui = await Open(Servers.Replace("\"loginTools\": 3", "\"loginTools\": 3, \"holdLogin\": true"));
        var c = ui.C;
        var web = c.Servers.Single(s => s.Name == "web");

        var signIn = web.SignInCommand.ExecuteAsync(null);
        await Until(() => ui.Vm.SignInBrowser?.CurrentUrl is not null && web.IsSigningIn, "sign-in page");
        Assert.Equal("https://auth.example.com/authorize?client_id=c&state=s1", ui.Vm.SignInBrowser!.CurrentUrl!.AbsoluteUri);
        await Settle(100);
        var window = Assert.Single(ui.W.OwnedWindows);
        Assert.Equal(Loc.T("Sign in"), window.Title);
        Assert.Empty(ui.Opened);
        Assert.False(web.SuggestSignIn); // Cancel instead
        await Shot(ui, "connectors-signing-in", scrollTo: "ConnectorList");
        await web.CancelSignInCommand.ExecuteAsync(null);
        await signIn;
        Assert.Null(ui.Vm.SignInBrowser); // the window closes with the sign-in
        await Settle(100);
        Assert.Empty(ui.W.OwnedWindows);
        Assert.False(web.IsSigningIn);
        Assert.Equal(Loc.F("Sign-in to “{0}” cancelled.", "web"), c.Feedback.Notice);
        Assert.Equal("", web.SignInError);
        Assert.Contains("mcp_login_cancel web", ui.Sent);
        Assert.Equal("expired", web.Auth);
    }

    [AvaloniaFact]
    public async Task Reload_config_asks_omp_once_and_shows_the_new_states()
    {
        await using var ui = await Open();
        var c = ui.C;

        await c.ReloadConfigCommand.ExecuteAsync(null);
        Assert.Single(ui.Sent, "mcp_reconnect *");
        Assert.Equal(Loc.F("omp read its MCP config again: {0} of {1} servers connected.", 2, 3), c.Feedback.Notice);
        Assert.Equal(ConnectorState.NoTools, c.Servers.Single(s => s.Name == "broken").State);
    }

    [AvaloniaFact]
    public async Task Omps_sign_in_prompt_shows_on_the_page_until_signed_in_or_dismissed()
    {
        await using var ui = await Open();
        var c = ui.C;

        await Emit(ui, """{ "type": "mcp_auth_required", "serverName": "web", "reason": "expired" }""");
        await Until(() => c.HasAuthPrompts, "prompt");
        var prompt = Assert.Single(c.AuthPrompts);
        Assert.Equal(("web", true, Loc.T("Sign in again")), (prompt.Server, prompt.Expired, prompt.SignInLabel));
        await Shot(ui, "connectors-prompt");
        var windows = 0;
        ui.Vm.SignInWindowRequested += () => windows++;
        await prompt.SignInCommand.ExecuteAsync(null);
        await Until(() => !c.HasAuthPrompts, "prompt gone after sign-in");
        Assert.Equal((1, null), (windows, ui.Vm.SignInBrowser)); // its page in the sign-in window, closed once done
        Assert.Empty(ui.Opened);

        await Emit(ui, """{ "type": "mcp_auth_required", "serverName": "echo", "reason": "signed_out" }""");
        await Until(() => c.HasAuthPrompts, "second prompt");
        Assert.Equal(Loc.T("Sign in"), c.AuthPrompts[0].SignInLabel);
        c.AuthPrompts[0].DismissCommand.Execute(null);
        await Until(() => !c.HasAuthPrompts, "dismissed");
    }

    private const string Refusal = "Figma refused client registration (Forbidden): this client is not on the server allowlist; set oauth.clientName or a pre-registered clientId";

    // A server omp could not start (its own error), one that needs a sign-in: the states the lists show besides connected
    private const string Failing = """
        { "servers": [
          { "name": "echo", "transport": "stdio", "scope": "user", "status": "connected", "toolCount": 2, "auth": "none" },
          { "name": "broken", "transport": "stdio", "scope": "project", "status": "failed", "toolCount": 0, "auth": "none",
            "error": "spawn /nonexistent/cmd ENOENT" },
          { "name": "web", "transport": "sse", "scope": "project", "status": "disconnected", "toolCount": 0, "auth": "signed_out",
            "authUrl": "https://auth.example.com/authorize?client_id=c&state=s2", "loginTools": 3 }
        ] }
        """;

    [AvaloniaFact]
    public async Task The_list_shows_connected_sign_in_needed_error_and_off_with_omps_reason()
    {
        await using var ui = await Open(Failing.Replace("\"loginTools\": 3", $"\"loginTools\": 3, \"loginError\": \"{Refusal}\""));
        var c = ui.C;

        var echo = c.Servers.Single(s => s.Name == "echo");
        Assert.Equal((ConnectorState.Connected, false), (echo.State, echo.SuggestReconnect));
        var broken = c.Servers.Single(s => s.Name == "broken");
        Assert.Equal((ConnectorState.Failed, Loc.T("Couldn't connect"), "spawn /nonexistent/cmd ENOENT"), (broken.State, broken.StateText, broken.Failure));
        Assert.True(broken.SuggestReconnect);
        Assert.False(broken.SuggestSignIn);
        var web = c.Servers.Single(s => s.Name == "web");
        Assert.Equal((Loc.T("Not connected: sign-in needed"), Loc.T("Signed out")), (web.StateText, web.AuthText));
        Assert.True(web.SuggestSignIn);
        Assert.False(web.SuggestReconnect);
        // Off: the project folder's server switched off in the user file (disabledServers)
        var docs = c.Servers.Single(s => s.Name == "docs");
        await Until(() => docs.State == ConnectorState.Off, "docs off");
        Assert.Equal((Loc.T("Off"), false), (docs.StateText, docs.SuggestReconnect));
        await Shot(ui, "connectors-list-states", scrollTo: "ConnectorList");

        // A refused client registration: omp's words on the row, and no page opens
        await web.SignInCommand.ExecuteAsync(null);
        Assert.Equal(Refusal, web.SignInError);
        Assert.Empty(ui.Opened);
        await Shot(ui, "connectors-sign-in-refused", scrollTo: "ConnectorList");

        // Reconnect: omp connects the failed server again from its config as it is now
        await broken.ReconnectCommand.ExecuteAsync(null);
        Assert.Contains("mcp_reconnect broken", ui.Sent);
        Assert.Equal("", broken.Failure);
        Assert.NotEqual(ConnectorState.Failed, broken.State);
        Assert.Equal(Loc.F("Reconnected “{0}”.", "broken"), c.Feedback.Notice);
    }

    [AvaloniaFact]
    public async Task The_chats_connectors_list_signs_in_reconnects_and_reloads_the_config()
    {
        await using var ui = await Open(Failing, connectors: false);
        var (c, vm) = (ui.C, ui.Vm);
        var chip = ui.W.GetVisualDescendants().OfType<Ombra.App.Views.Session.ConnectorsIndicator>().Single();
        await Until(() => c.IsChatAvailable, "chip");
        await Settle(100);
        Assert.True(chip.Button.IsEffectivelyVisible);
        Assert.DoesNotContain("get_mcp_servers", ui.Sent); // omp is asked only when the list opens

        chip.Button.Command!.Execute(null);
        await Until(() => c.ChatServers.Count == 3 && !c.IsChatLoading, "the chat's list");
        var echo = c.ChatServers.Single(s => s.Name == "echo");
        var broken = c.ChatServers.Single(s => s.Name == "broken");
        var web = c.ChatServers.Single(s => s.Name == "web");
        Assert.Equal((ConnectorState.Connected, Loc.Plural(2, "Connected · {0} tool", "Connected · {0} tools")), (echo.State, echo.StatusText));
        Assert.Equal((ConnectorState.Failed, Loc.T("Couldn't connect"), "spawn /nonexistent/cmd ENOENT", true), (broken.State, broken.StatusText, broken.Error, broken.CanReconnect));
        Assert.Equal((Loc.T("Sign-in needed"), true, false, ""), (web.StatusText, web.CanSignIn, web.CanReconnect, web.Error));
        Assert.Equal(2, c.ChatAttention);
        Assert.Equal(Loc.Plural(2, "{0} connector needs attention", "{0} connectors need attention"), c.ChipLabel);
        await Settle(200);
        Assert.True(chip.FindControl<TextBlock>("ConnectorsChipLabel")!.IsEffectivelyVisible);
        var names = chip.Popup.Child!.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible)
            .Select(b => Avalonia.Automation.AutomationProperties.GetName(b)).ToList();
        Assert.Contains(Loc.F("Sign in to {0}", "web"), names);
        Assert.Contains(Loc.F("Reconnect {0}", "broken"), names);
        Assert.Contains(Loc.T("Reload config"), names);
        await Shot(ui, "chat-connectors");

        // Sign in from the chat: the page opens in a tab of the chat's Browser tool, which closes once the row connects
        // (a page already in the pane, so the sign-in's tab is not its first, which stays; the headless pane finds no
        // engine once shown, after the page opened)
        vm.Preview.OpenUserTab(new Uri("https://example.com/before"));
        var tabs = vm.Preview.Tabs.Count;
        PreviewTab? signInTab = null;
        void Watch(object? _, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
            signInTab ??= e.NewItems?.OfType<PreviewTab>().FirstOrDefault();
        vm.Preview.Tabs.CollectionChanged += Watch;
        await web.SignInCommand.ExecuteAsync(null);
        vm.Preview.Tabs.CollectionChanged -= Watch;
        Assert.Empty(ui.Opened);
        Assert.NotNull(signInTab);
        Assert.Equal("https://auth.example.com/authorize?client_id=c&state=s2", signInTab!.Url!.AbsoluteUri);
        Assert.True(signInTab.IsClosed);
        Assert.Equal(tabs, vm.Preview.Tabs.Count);
        Assert.Equal(SidePane.Browser, vm.ActivePane);
        Assert.Equal((ConnectorState.Connected, false), (web.State, web.NeedsAttention));
        Assert.Equal(Loc.Plural(3, "Signed in to “{1}”: {0} tool ready.", "Signed in to “{1}”: {0} tools ready.", "web"), c.ChatNotice);
        Assert.Equal(Loc.F("{0} isn't connected", "broken"), c.ChipLabel);

        await broken.ReconnectCommand.ExecuteAsync(null);
        Assert.Contains("mcp_reconnect broken", ui.Sent);
        Assert.False(c.HasChatAttention);
        Assert.Equal(Loc.F("Reconnected “{0}”.", "broken"), c.ChatNotice);

        await c.ReloadConfigCommand.ExecuteAsync(null);
        Assert.Contains("mcp_reconnect *", ui.Sent);
        Assert.Equal(Loc.F("omp read its MCP config again: {0} of {1} servers connected.", 3, 3), c.ChatNotice);

        await c.ManageConnectorsCommand.ExecuteAsync(null);
        Assert.False(c.IsChatOpen);
        Assert.True(vm.IsSettingsOpen && vm.PluginsHub.IsMcpTab);
    }

    [AvaloniaFact]
    public async Task Omps_sign_in_prompt_marks_the_chip_after_it_is_dismissed_and_the_notice_links_to_the_list()
    {
        await using var ui = await Open(connectors: false);
        var (c, vm) = (ui.C, ui.Vm);

        await Emit(ui, """{ "type": "mcp_auth_required", "serverName": "web", "reason": "expired" }""");
        await Until(() => c.HasChatAttention && !c.IsChatLoading && ui.Sent.Contains("get_mcp_servers"), "chip marked");
        Assert.Equal(Loc.F("Sign in to {0}", "web"), c.ChipLabel);
        Assert.Equal(Loc.T("Sign-in expired"), c.ChatServers.Single(s => s.Name == "web").StatusText);

        // The conversation's notice, in the app's language, links to the list
        await Until(() => vm.Rows.OfType<NoticeRowViewModel>().Any(n => n.HasConnectorLink), "notice");
        var notice = vm.Rows.OfType<NoticeRowViewModel>().Single(n => n.HasConnectorLink);
        Assert.Equal(("web", Loc.F("Connector “{0}” needs you to sign in again.", "web")), (notice.McpServer, notice.Text));
        await Settle(200);
        var link = ui.W.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(b) == Loc.F("Show connectors: {0}", "web"));
        await Shot(ui, "chat-sign-in-notice");

        // Dismissed on the page, the sign-in stays on the chip
        vm.DismissConnectorSignInPrompt("web");
        await Until(() => vm.McpAuthRequests.Count == 0, "prompt dismissed");
        await Settle(100);
        Assert.True(c.HasChatAttention);

        link.Command!.Execute(link.CommandParameter);
        await Until(() => vm.IsSettingsOpen && vm.PluginsHub.IsMcpTab, "the MCP list");
    }

    [AvaloniaFact]
    public async Task Sign_out_forgets_the_sign_in_and_the_row_offers_sign_in_again()
    {
        await using var ui = await Open();
        var c = ui.C;
        var web = c.Servers.Single(s => s.Name == "web");
        Assert.True(web.CanSignOut); // expired: omp still keeps a sign-in
        await web.SignInCommand.ExecuteAsync(null);
        ui.Vm.PluginsHub.OpenServerCommand.Execute(web);
        await Settle(200);
        Assert.True(ui.W.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SignOutServerButton").IsEffectivelyVisible);
        Assert.True(ui.W.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ReconnectServerButton").IsEffectivelyVisible);
        await Shot(ui, "connectors-details-sign-out");

        await web.SignOutCommand.ExecuteAsync(null);
        Assert.Contains("mcp_logout web", ui.Sent);
        Assert.Equal(("signed_out", true, false), (web.Auth, web.SuggestSignIn, web.CanSignOut));
        Assert.Equal(Loc.F("Signed out of “{0}”.", "web"), c.Feedback.Notice);
    }

    [AvaloniaFact]
    public async Task A_typed_mcp_reauth_opens_its_sign_in_page_in_the_chats_browser()
    {
        await using var ui = await Open(connectors: false);
        var vm = ui.Vm;
        vm.ComposerText = "/mcp reauth web";
        await vm.SendCommand.ExecuteAsync(null);
        await Emit(ui, """{ "type": "extension_ui_request", "id": "t1", "method": "open_url", "url": "https://auth.example.com/typed", "mcpServer": "web" }""");
        await Until(() => vm.Preview.CurrentUrl?.AbsoluteUri == "https://auth.example.com/typed", "the typed sign-in's page");
        Assert.Equal(SidePane.Browser, vm.ActivePane);
        Assert.Empty(ui.Opened);

        // Another server's page nobody asked for here: the link card
        await Emit(ui, """{ "type": "extension_ui_request", "id": "t2", "method": "open_url", "url": "https://auth.example.com/other", "mcpServer": "echo" }""");
        await Until(() => vm.PendingUrl is not null, "link card");
        Assert.Empty(ui.Opened);
        Assert.Equal("https://auth.example.com/typed", vm.Preview.CurrentUrl!.AbsoluteUri);

        // A pane that cannot show pages (headless: no web engine, found once the pane showed) hands sign-ins to the
        // default browser
        await Until(() => vm.Preview.IsEngineUnavailable, "the headless pane's engine check");
        vm.OpenLink("http://127.0.0.1:53682/callback?code=c&state=s", LinkTarget.SignIn);
        Assert.Equal(["http://127.0.0.1:53682/callback?code=c&state=s"], ui.Opened);
    }

    private const string Form = """
        { "type": "extension_ui_request", "id": "elicit-1", "method": "elicit", "serverName": "web", "message": "Where should the report go?",
          "fields": [
            { "name": "email", "kind": "text", "title": "Email", "required": true, "format": "email" },
            { "name": "copies", "kind": "integer", "title": "Copies", "minimum": 1, "maximum": 5 },
            { "name": "urgent", "kind": "boolean", "title": "Urgent", "default": false },
            { "name": "format", "kind": "select", "title": "Format", "required": true, "options": [ { "value": "pdf", "label": "PDF" }, { "value": "md", "label": "Markdown" } ] },
            { "name": "tags", "kind": "multiselect", "title": "Tags", "minItems": 1, "options": [ { "value": "a", "label": "Alpha" }, { "value": "b", "label": "Beta" } ] }
          ] }
        """;

    [AvaloniaFact]
    public async Task A_connector_form_is_checked_here_and_its_values_go_only_to_omp()
    {
        await using var ui = await Open(connectors: false);
        var vm = ui.Vm;

        await Emit(ui, Form);
        await Until(() => vm.CurrentDialog?.IsElicitation == true, "form card");
        var d = vm.CurrentDialog!;
        Assert.Equal(Loc.F("Connector “{0}” asks for information", "web"), d.Title);
        Assert.Equal("Where should the report go?", d.Message);
        Assert.Equal(["text", "integer", "boolean", "select", "multiselect"], d.Fields.Select(f => f.Field.Kind));
        Assert.Equal(("Email *", true), (d.Fields[0].Label, d.Fields[0].IsText));
        await Shot(ui, "elicitation-card", scrollTo: "DialogTitle");

        // Client checks: nothing reaches omp while a field is wrong
        d.Fields[0].Text = "not-an-email";
        d.Fields[1].Text = "9";
        await d.SendFormCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("Enter an email address."), d.Fields[0].Error);
        Assert.Equal(Loc.F("At most {0}.", "5"), d.Fields[1].Error);
        Assert.Equal(Loc.T("Choose one."), d.Fields[3].Error);
        Assert.Equal(Loc.Plural(1, "Choose at least {0}.", "Choose at least {0}."), d.Fields[4].Error);
        Assert.True(d.HasFormError);
        Assert.Same(d, vm.CurrentDialog);
        Assert.DoesNotContain(ui.Sent, l => l.StartsWith("elicit", StringComparison.Ordinal));
        await Shot(ui, "elicitation-card-errors", scrollTo: "SendFormButton");

        d.Fields[0].Text = "me@secret-domain.example";
        d.Fields[1].Text = "2";
        d.Fields[2].IsChecked = true;
        d.Fields[3].Selected = d.Fields[3].Options[1];
        d.Fields[4].Options[0].IsChecked = true;
        await d.SendFormCommand.ExecuteAsync(null);
        await Until(() => ui.Sent.Any(l => l.StartsWith("elicit", StringComparison.Ordinal)), "answer");
        var line = ui.Sent.Single(l => l.StartsWith("elicit", StringComparison.Ordinal));
        using var answer = JsonDocument.Parse(line["elicit ".Length..]);
        Assert.Equal("accept", answer.RootElement.GetProperty("action").GetString());
        Assert.Equal("""{"email":"me@secret-domain.example","copies":2,"urgent":true,"format":"md","tags":["a"]}""",
            answer.RootElement.GetProperty("content").GetRawText());
        await Until(() => vm.CurrentDialog is null, "card closed");

        // The values never reach the conversation or the debug log
        var snap = ui.T.Session.Snapshot();
        Assert.DoesNotContain(snap.Items, i => i.ToString()!.Contains("secret-domain", StringComparison.Ordinal));
        Assert.DoesNotContain(snap.DebugTail, e => e.Summary.Contains("secret-domain", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task A_connector_form_can_be_declined_or_dismissed()
    {
        await using var ui = await Open(connectors: false);
        var vm = ui.Vm;
        bool Answered(string l) => l.StartsWith("elicit", StringComparison.Ordinal);

        await Emit(ui, Form);
        await Until(() => vm.CurrentDialog?.IsElicitation == true, "form card");
        await vm.CurrentDialog!.DeclineFormCommand.ExecuteAsync(null);
        await Until(() => ui.Sent.Any(Answered), "decline");
        Assert.Contains("\"action\":\"decline\"", ui.Sent.Single(Answered));

        await Emit(ui, Form.Replace("elicit-1", "elicit-2"));
        await Until(() => vm.CurrentDialog?.Id == "elicit-2", "second form");
        await vm.CurrentDialog!.DismissCommand.ExecuteAsync(null);
        await Until(() => ui.Sent.Count(Answered) == 2, "dismiss");
        Assert.Contains("\"cancelled\":true", ui.Sent.Last(Answered));
    }
}
