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

        await web.SignInCommand.ExecuteAsync(null);
        Assert.Equal(["https://auth.example.com/authorize?client_id=c&state=s1"], ui.Opened);
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
        Assert.Single(ui.Opened);
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
        await Until(() => ui.Opened.Count == 1 && web.IsSigningIn, "sign-in page");
        Assert.False(web.SuggestSignIn); // Cancel instead
        await Shot(ui, "connectors-signing-in", scrollTo: "ConnectorList");
        await web.CancelSignInCommand.ExecuteAsync(null);
        await signIn;
        Assert.False(web.IsSigningIn);
        Assert.Equal(Loc.F("Sign-in to “{0}” cancelled.", "web"), c.Feedback.Notice);
        Assert.Equal("", web.SignInError);
        Assert.Contains("mcp_login_cancel web", ui.Sent);
        Assert.Equal("expired", web.Auth);
    }

    [AvaloniaFact]
    public async Task Reconnect_all_asks_omp_once_and_shows_the_new_states()
    {
        await using var ui = await Open();
        var c = ui.C;

        await c.ReconnectAllCommand.ExecuteAsync(null);
        Assert.Single(ui.Sent, "mcp_reconnect *");
        Assert.Equal(Loc.F("omp reconnected the MCP servers: {0} of {1} connected.", 2, 3), c.Feedback.Notice);
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
        await prompt.SignInCommand.ExecuteAsync(null);
        await Until(() => !c.HasAuthPrompts, "prompt gone after sign-in");
        Assert.Single(ui.Opened);

        await Emit(ui, """{ "type": "mcp_auth_required", "serverName": "echo", "reason": "signed_out" }""");
        await Until(() => c.HasAuthPrompts, "second prompt");
        Assert.Equal(Loc.T("Sign in"), c.AuthPrompts[0].SignInLabel);
        c.AuthPrompts[0].DismissCommand.Execute(null);
        await Until(() => !c.HasAuthPrompts, "dismissed");
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
