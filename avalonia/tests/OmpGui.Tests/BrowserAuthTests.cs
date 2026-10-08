using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App.Controls;
using OmpGui.App.ViewModels;
using OmpGui.ClientCore.Browser;

namespace OmpGui.Tests;

/// <summary>
/// HTTP authentication in the browser pane (BROWSER_PLAN A1): what the engine hooks (Platform/Mac/WebKitHooks.cs,
/// Platform/Windows/WebView2Hooks.cs, Platform/Linux/WebKitGtkHooks.cs) receive back through the hooks of the tab whose
/// web view asks (<see cref="PreviewTab.Page"/>, attached by the panel). The native path itself runs in
/// tools/verify/webview-harness (auth-basic).
/// </summary>
public sealed class BrowserAuthTests
{
    private static (Window Window, PreviewPanel Panel, PreviewViewModel Vm) Show()
    {
        var vm = new PreviewViewModel();
        var panel = new PreviewPanel { DataContext = vm };
        var w = new Window { Content = panel, Width = 720, Height = 640 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (w, panel, vm);
    }

    private static CredentialRequest StudioRequest(bool failedBefore = false, string? user = null) =>
        new("studio.example.com", 443, "Studio", IsProxy: false, failedBefore, user, "Basic") { PageUrl = new Uri("https://studio.example.com/") };

    private static IEnumerable<string> Texts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "");

    /// <summary>What the engine hooks of <paramref name="tab"/>'s web view call for a sign-in request.</summary>
    private static IBrowserAuthHandler Auth(PreviewTab tab) => tab.Page;

    [AvaloniaFact]
    public async Task A_sign_in_request_shows_the_card_in_the_pane_and_gives_the_engine_what_the_user_typed()
    {
        var (w, panel, vm) = Show();
        var answer = Auth(vm.ActiveTab).RequestCredentialsAsync(StudioRequest(), CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        var card = Assert.IsType<BrowserDialogCardViewModel>(vm.Dialogs.Card);
        Assert.True(card.IsCredentials);
        var shown = Texts(panel).ToList();
        Assert.Contains("studio.example.com wants you to sign in", shown);
        Assert.Contains("Realm: Studio", shown);
        Assert.DoesNotContain("That username or password didn't work", shown);
        Assert.False(answer.IsCompleted);

        card.UserName = "ada";
        card.Password = "s3cret";
        await card.PrimaryCommand.ExecuteAsync(null);
        var credentials = await answer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new CredentialAnswer("ada", "s3cret"), credentials);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Dialogs.Card);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_refused_password_asks_again_with_the_error_and_the_user_name_filled_in()
    {
        var (w, panel, vm) = Show();
        var answer = Auth(vm.ActiveTab).RequestCredentialsAsync(StudioRequest(failedBefore: true, user: "ada"), CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        var card = Assert.IsType<BrowserDialogCardViewModel>(vm.Dialogs.Card);
        Assert.Contains("That username or password didn't work", Texts(panel));
        Assert.Equal("ada", card.UserName);
        card.SecondaryCommand.Execute(null); // Cancel: the engine shows the server's 401 page
        Assert.Null(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
        w.Close();
    }

    [AvaloniaFact]
    public async Task Plain_http_sign_in_warns_that_the_connection_is_not_secure()
    {
        var (w, panel, vm) = Show();
        var request = new CredentialRequest("intranet.local", 8080, "Admin", false, false, AuthScheme: "Basic") { PageUrl = new Uri("http://intranet.local:8080/") };
        var answer = Auth(vm.ActiveTab).RequestCredentialsAsync(request, CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("intranet.local:8080 wants you to sign in", Texts(panel));
        Assert.Contains("This site doesn't use a secure connection.", Texts(panel));
        vm.Dialogs.Card!.CancelCommand.Execute(null); // Esc
        Assert.Null(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
        w.Close();
    }

    [AvaloniaFact]
    public async Task Navigating_away_answers_a_waiting_sign_in_request_with_cancel_exactly_once()
    {
        var (w, panel, vm) = Show();
        var answer = Auth(vm.ActiveTab).RequestCredentialsAsync(StudioRequest(), CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        var card = Assert.IsType<BrowserDialogCardViewModel>(vm.Dialogs.Card);

        vm.Address = "http://localhost:5173";
        vm.NavigateCommand.Execute(null);
        Assert.Null(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Dialogs.Card);

        // A late click on the old card changes nothing
        card.UserName = "ada";
        await card.PrimaryCommand.ExecuteAsync(null);
        Assert.Null(await answer);
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_engine_cancelling_its_challenge_takes_the_card_away()
    {
        var (w, panel, vm) = Show();
        using var cts = new CancellationTokenSource();
        var answer = Auth(vm.ActiveTab).RequestCredentialsAsync(StudioRequest(), cts.Token);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(vm.Dialogs.Card);

        cts.Cancel(); // the hook detaches (web view destroyed) or the engine gave up on the load
        Assert.Null(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Dialogs.Card);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_tabs_sign_in_request_waits_for_its_tab_and_a_closed_tab_means_cancel()
    {
        var (w, panel, vm) = Show();
        var omp = vm.OpenAgentTab("surface:2");
        vm.SelectTabCommand.Execute(vm.Tabs[0]); // the user looks at their own tab
        Dispatcher.UIThread.RunJobs();

        var answer = Auth(omp).RequestCredentialsAsync(StudioRequest(), CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Dialogs.Card); // omp's tab asks; the card waits for that tab
        vm.SelectTabCommand.Execute(omp);
        Dispatcher.UIThread.RunJobs();
        var card = Assert.IsType<BrowserDialogCardViewModel>(vm.Dialogs.Card);
        Assert.Contains("studio.example.com wants you to sign in", Texts(panel));
        card.SecondaryCommand.Execute(null);
        Assert.Null(await answer.WaitAsync(TimeSpan.FromSeconds(5)));

        var late = Auth(omp).RequestCredentialsAsync(StudioRequest(), CancellationToken.None);
        vm.CloseTabCommand.Execute(omp);
        Assert.Null(await late.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(await Auth(omp).RequestCredentialsAsync(StudioRequest(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        w.Close();
    }

    [Theory]
    [InlineData("Basic realm=\"Studio\"", "Basic", "Studio")]
    [InlineData("Basic realm=\"Studio\", charset=\"UTF-8\"", "Basic", "Studio")]
    [InlineData("Digest qop=\"auth\", realm=\"My \\\"Lab\\\"\", nonce=\"abc\"", "Digest", "My \"Lab\"")]
    [InlineData("Basic realm=Intranet", "Basic", "Intranet")]
    [InlineData("NTLM", "NTLM", null)]
    [InlineData("Negotiate", "Negotiate", null)]
    [InlineData("Digest myrealm=\"x\", realm=\"y\"", "Digest", "y")]
    [InlineData("", null, null)]
    public void WebView2_challenge_headers_give_the_scheme_and_realm_shown_on_the_card(string header, string? scheme, string? realm) =>
        Assert.Equal((scheme, realm), WwwAuthenticate.Parse(header));
}
