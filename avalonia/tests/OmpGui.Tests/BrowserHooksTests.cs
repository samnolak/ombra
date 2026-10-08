using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App.Controls;
using OmpGui.App.ViewModels;
using OmpGui.ClientCore.Browser;

namespace OmpGui.Tests;

/// <summary>
/// The app side of the macOS engine hooks (BROWSER_PLAN B2): what <c>Platform/Mac/WebKitHooks</c> reports through a
/// tab's <see cref="BrowserPageHooks"/> and what it asks the pane for — pop-up tabs, the error and crash pages,
/// downloads saved where the user chose, the pane's downloads list, omp-caused questions, remembered site permissions
/// and Safari's user agent. The native side runs in tools/verify/webview-harness (dialogs, popup, download,
/// file-input, crash, load-failure, permission, beforeunload, user-agent with <c>--hooks app</c>).
/// </summary>
public sealed class BrowserHooksTests
{
    private static (Window Window, PreviewPanel Panel, PreviewViewModel Vm) Show(PreviewViewModel? vm = null)
    {
        vm ??= new PreviewViewModel();
        var panel = new PreviewPanel { DataContext = vm };
        var w = new Window { Content = panel, Width = 900, Height = 640 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (w, panel, vm);
    }

    private static List<string> Texts(Control root) =>
        [.. root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")];

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    /// <summary>A pop-up as the engine hooks hand it to the app (the native view itself is the harness's business).</summary>
    private sealed class FakePopup : BrowserPopup
    {
        public int Closes;
        public readonly List<Uri> Navigations = [];
        public readonly List<string> Scripts = [];

        public override nint NativeHandle => 0;
        public override string HandleDescriptor => "NSView";
        public override Uri? Url => Navigations.LastOrDefault() ?? RequestedUrl;
        public override string Title => "Sign in - Accounts";
        public override bool CanGoBack => Navigations.Count > 0;
        public override bool CanGoForward => false;
        public override bool IsClosed => Closes > 0;
        public override void Navigate(Uri url) => Navigations.Add(url);
        public override void Reload() { }
        public override void GoBack() { }
        public override void GoForward() { }

        public override Task<string?> RunScriptAsync(string script)
        {
            Scripts.Add(script);
            return Task.FromResult<string?>("\"ok\"");
        }

        public override void Close() => Closes++;
        public static FakePopup For(string url) => new() { RequestedUrl = new Uri(url) };
    }

    // ── pop-ups ──

    [AvaloniaFact]
    public async Task A_popup_opens_as_a_tab_after_its_opener_and_closing_itself_brings_the_opener_back()
    {
        var (w, panel, vm) = Show();
        // A page shown (as the web view reports it; headless has no engine to navigate)
        vm.OnNavigationCompleted(vm.ActiveTab, new Uri("https://app.example.com/login"), true, false, false);
        var opener = vm.ActiveTab;
        var popup = FakePopup.For("https://accounts.example.com/oauth?client=1");

        var popupPage = opener.Page.OpenPopup!(popup)!;
        Dispatcher.UIThread.RunJobs();

        var tab = vm.Tabs[1];
        Assert.Same(popupPage, tab.Page);
        Assert.True(tab.IsPopup);
        Assert.Same(opener, tab.Opener);
        Assert.Same(tab, vm.ActiveTab); // the opener was on screen: the pop-up shows at once
        Assert.True(tab.CanClose);
        Assert.Equal("accounts.example.com", tab.Header);
        Assert.Equal("Pop-up from app.example.com", tab.ToolTip);
        Assert.True(vm.ShowTabStrip);
        Assert.Contains("Pop-up", Texts(panel));
        // The engine's own view is hosted in the page area; omp's browser drives it like any tab
        var host = Assert.Single(Named<Panel>(panel, "WebHost").Children.OfType<PopupViewHost>());
        Assert.Same(popup, host.Popup);
        Assert.Equal("\"ok\"", await tab.RunScript!("document.title"));
        Assert.Equal(new[] { "document.title" }, popup.Scripts);

        // Its own delegate reports the loading (pop-ups have no NativeWebView)
        popupPage.RaiseStarted(new Uri("https://accounts.example.com/consent"));
        Assert.True(vm.IsLoading);
        Assert.Equal("https://accounts.example.com/consent", vm.Address);
        popup.Navigate(new Uri("https://accounts.example.com/consent"));
        popupPage.RaiseFinished(new Uri("https://accounts.example.com/consent"));
        Assert.False(vm.IsLoading);
        Assert.Equal("Sign in - Accounts", vm.Title);
        Assert.True(vm.CanGoBack);

        // The address box drives the pop-up's own view
        vm.NavigateCommand.Execute("https://accounts.example.com/other");
        Assert.Equal(new Uri("https://accounts.example.com/other"), popup.Navigations[^1]);

        // window.close(): the tab goes, the opener shows again, the view is released once
        popupPage.RaiseClosed();
        Dispatcher.UIThread.RunJobs();
        Assert.Single(vm.Tabs);
        Assert.Same(opener, vm.ActiveTab);
        Assert.True(tab.IsClosed);
        Assert.Equal(1, popup.Closes);
        Assert.Empty(Named<Panel>(panel, "WebHost").Children.OfType<PopupViewHost>());
        w.Close();
    }

    [AvaloniaFact]
    public void Popups_go_with_their_opener_the_user_can_close_one_and_a_closed_tab_opens_none()
    {
        var vm = new PreviewViewModel();
        var omp = vm.OpenAgentTab("surface:7");
        var first = FakePopup.For("https://accounts.example.com/a");
        var nested = FakePopup.For("https://accounts.example.com/b");
        var firstPage = omp.Page.OpenPopup!(first)!;
        var firstTab = vm.Tabs.Single(t => t.Popup == first);
        firstPage.OpenPopup!(nested);
        var nestedTab = vm.Tabs.Single(t => t.Popup == nested);
        // Each pop-up sits right after its opener chain
        Assert.Equal(new[] { vm.Tabs[0], omp, firstTab, nestedTab }, vm.Tabs.ToArray());
        Assert.Same(nestedTab, vm.ActiveTab);

        // A pop-up opened from a tab in the background stays in the background
        vm.SelectTabCommand.Execute(vm.Tabs[0]);
        var background = FakePopup.For("https://accounts.example.com/c");
        omp.Page.OpenPopup!(background);
        Assert.Same(vm.Tabs[0], vm.ActiveTab);
        var backgroundTab = vm.Tabs.Single(t => t.Popup == background);
        Assert.Equal(4, vm.Tabs.IndexOf(backgroundTab));

        // The user's ×
        vm.CloseTabCommand.Execute(backgroundTab);
        Assert.Equal(1, background.Closes);
        Assert.DoesNotContain(backgroundTab, vm.Tabs);

        // omp's tab goes: its pop-ups (and theirs) go with it
        vm.CloseTabCommand.Execute(omp);
        Assert.Single(vm.Tabs);
        Assert.Equal(1, first.Closes);
        Assert.Equal(1, nested.Closes);
        Assert.Null(omp.Page.OpenPopup!(FakePopup.For("https://accounts.example.com/late")));
        Assert.Single(vm.Tabs);
    }

    // ── failures and crashes ──

    [AvaloniaFact]
    public void A_failed_navigation_shows_why_with_try_again_on_the_address_that_failed()
    {
        var (w, panel, vm) = Show();
        var navigations = new List<Uri>();
        vm.NavigationRequested += navigations.Add;
        var failed = new Uri("http://localhost:3999/app");
        vm.OnNavigationStarted(vm.ActiveTab, failed); // as the web view reports it
        vm.ActiveTab.Page.RaiseFailed(new BrowserLoadFailure(failed, "NSURLErrorDomain", -1004, "Could not connect to the server.", true));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.ShowErrorPage);
        Assert.False(vm.ShowWebView); // the native view would draw over the page
        Assert.False(vm.IsLoading);
        Assert.True(vm.LoadFailed);
        Assert.Equal("", vm.Message); // the error page says it, not the notice bar
        var shown = Texts(panel);
        Assert.Contains("The page couldn't be opened", shown);
        Assert.Contains("http://localhost:3999/app: Could not connect to the server.", panel.GetVisualDescendants().OfType<SelectableTextBlock>().Select(t => t.Text));
        Assert.True(Named<Button>(panel, "ErrorRetryButton").IsEffectivelyVisible);

        // Try again: the address that failed (the engine still shows the page before it)
        navigations.Clear();
        vm.ReloadCommand.Execute(null);
        Assert.Equal(new[] { failed }, navigations);
        Assert.Null(vm.LoadError);

        // A refused certificate says so
        vm.ActiveTab.Page.RaiseFailed(new BrowserLoadFailure(failed, "NSURLErrorDomain", -1202, "The certificate for this server is invalid.", true));
        Assert.Equal("This connection isn't private", vm.ErrorTitle);

        // The next page that loads clears it
        vm.OnNavigationCompleted(vm.ActiveTab, new Uri("http://localhost:3999/"), true, false, false);
        Assert.Null(vm.LoadError);
        Assert.False(vm.ShowErrorPage);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_crashed_page_cancels_its_questions_shows_the_crash_page_and_reload_brings_it_back()
    {
        var (w, panel, vm) = Show();
        var reloads = 0;
        vm.ReloadRequested += () => reloads++;
        vm.OnNavigationStarted(vm.ActiveTab, new Uri("http://localhost:3000/"));
        vm.OnNavigationCompleted(vm.ActiveTab, new Uri("http://localhost:3000/"), true, false, false);
        var page = vm.ActiveTab.Page;
        var confirm = page.Broker()!.ConfirmAsync(new ConfirmRequest("Leave?"), CancellationToken.None);

        page.RaiseTerminated();
        Dispatcher.UIThread.RunJobs();

        Assert.False(await confirm.WaitAsync(TimeSpan.FromSeconds(5))); // the engine's no-answer default
        Assert.True(vm.ShowCrashPage);
        Assert.False(vm.ShowWebView);
        Assert.Contains("This page stopped working", Texts(panel));
        Assert.True(Named<Button>(panel, "CrashReloadButton").IsEffectivelyVisible);
        Assert.True(Named<Button>(panel, "CrashOpenExternallyButton").IsEffectivelyVisible);

        Named<Button>(panel, "CrashReloadButton").Command!.Execute(null);
        Assert.Equal(1, reloads);
        Assert.False(vm.ShowCrashPage);
        w.Close();
    }

    [AvaloniaFact]
    public void A_tab_in_the_background_keeps_its_crash_and_error_until_it_shows()
    {
        var vm = new PreviewViewModel();
        vm.NavigateCommand.Execute("http://localhost:3000");
        var omp = vm.OpenAgentTab("surface:2");
        vm.NavigateTabForAgent(omp, "http://localhost:4000");
        vm.SelectTabCommand.Execute(vm.Tabs[0]);

        omp.Page.RaiseTerminated();
        Assert.False(vm.ShowCrashPage);
        vm.SelectTabCommand.Execute(omp);
        Assert.True(vm.ShowCrashPage);
        vm.SelectTabCommand.Execute(vm.Tabs[0]);
        Assert.False(vm.ShowCrashPage);

        vm.ActiveTab.Page.RaiseFailed(new BrowserLoadFailure(new Uri("http://localhost:3000/"), "NSURLErrorDomain", -1004, "Could not connect to the server.", true));
        vm.SelectTabCommand.Execute(omp);
        Assert.False(vm.ShowErrorPage);
        vm.SelectTabCommand.Execute(vm.Tabs[0]);
        Assert.True(vm.ShowErrorPage);
    }

    // ── downloads ──

    [AvaloniaFact]
    public async Task A_download_is_saved_only_where_the_user_chose()
    {
        var folder = Directory.CreateTempSubdirectory("ompgui-downloads-").FullName;
        try
        {
            var (w, panel, vm) = Show();
            var page = vm.ActiveTab.Page;
            var picks = new List<(string Name, string? Folder)>();
            string? pickAnswer = Path.Combine(folder, "picked-report.txt");
            vm.Downloads.PickSaveLocation = (name, start) =>
            {
                picks.Add((name, start));
                return Task.FromResult<string?>(pickAnswer);
            };
            var request = new DownloadRequest("report.txt", 12, new Uri("https://example.com/files/report.txt")) { PageUrl = new Uri("https://example.com/") };

            // 1. "Save" before any folder was chosen: the save panel asks (nothing goes to a default folder)
            var first = page.ChooseDownloadPath(request, CancellationToken.None);
            Dispatcher.UIThread.RunJobs();
            var card = Assert.IsType<BrowserDialogCardViewModel>(vm.Dialogs.Card);
            Assert.Equal("Download report.txt?", card.Title);
            await card.PrimaryCommand.ExecuteAsync(null);
            Assert.Equal(Path.Combine(folder, "picked-report.txt"), await first.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(new[] { ("report.txt", (string?)null) }, picks);
            Assert.Equal(folder, vm.SiteSettings.DownloadFolder);

            // 2. "Save" again: straight into that folder, never over an existing file
            File.WriteAllText(Path.Combine(folder, "report.txt"), "older");
            var second = page.ChooseDownloadPath(request, CancellationToken.None);
            Dispatcher.UIThread.RunJobs();
            await vm.Dialogs.Card!.PrimaryCommand.ExecuteAsync(null);
            Assert.Equal(Path.Combine(folder, "report (2).txt"), await second.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Single(picks);

            // 3. "Save as…": the panel again, starting in the chosen folder
            var third = page.ChooseDownloadPath(request with { FileName = "../../escape.txt" }, CancellationToken.None);
            Dispatcher.UIThread.RunJobs();
            pickAnswer = Path.Combine(folder, "chosen.txt");
            vm.Dialogs.Card!.TertiaryCommand.Execute(null);
            Assert.Equal(Path.Combine(folder, "chosen.txt"), await third.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(("escape.txt", folder), picks[^1]);

            // 4. Cancel, and a panel cancelled: not downloaded
            var fourth = page.ChooseDownloadPath(request, CancellationToken.None);
            Dispatcher.UIThread.RunJobs();
            vm.Dialogs.Card!.SecondaryCommand.Execute(null);
            Assert.Null(await fourth.WaitAsync(TimeSpan.FromSeconds(5)));
            pickAnswer = null;
            var fifth = page.ChooseDownloadPath(request, CancellationToken.None);
            Dispatcher.UIThread.RunJobs();
            vm.Dialogs.Card!.TertiaryCommand.Execute(null);
            Assert.Null(await fifth.WaitAsync(TimeSpan.FromSeconds(5)));
            w.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Without_a_chosen_folder_or_a_save_panel_nothing_is_downloaded()
    {
        var downloads = new BrowserDownloads(new BrowserSiteSettings());
        using var broker = new DialogBroker();
        var answer = downloads.ChooseDestinationAsync(new DownloadRequest("a.zip", null, null), broker, CancellationToken.None);
        broker.Current!.Complete(DownloadAnswer.Save);
        Assert.Null(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(await downloads.ChooseDestinationAsync(new DownloadRequest("a.zip", null, null), null, CancellationToken.None));
        Assert.Null(downloads.Settings.DownloadFolder);
    }

    [AvaloniaFact]
    public void The_pane_lists_downloads_being_saved_with_progress_stop_and_show_in_finder()
    {
        var (w, panel, vm) = Show();
        var shown = new List<BrowserDownload>();
        vm.ShowDownloadRequested += shown.Add;
        var page = vm.ActiveTab.Page;
        var stops = 0;
        var d = new BrowserDownload(new Uri("https://example.com/files/data.csv"), new Uri("https://example.com/")) { State = BrowserDownloadState.Asking };
        d.Canceller = () => ++stops > 0;

        page.RaiseDownloadChanged(d);
        Assert.False(vm.HasDownloads); // still asking where: nothing to list yet
        d.FileName = "data.csv";
        d.Path = "/tmp/example/data.csv";
        d.TotalBytes = 2048;
        d.BytesReceived = 1024;
        d.State = BrowserDownloadState.Saving;
        page.RaiseDownloadChanged(d);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.HasDownloads);
        Assert.True(Named<Border>(panel, "DownloadsStrip").IsEffectivelyVisible);
        var texts = Texts(panel);
        Assert.Contains("data.csv", texts);
        Assert.Contains("1 KB of 2 KB", texts);
        vm.CancelDownloadCommand.Execute(d);
        Assert.Equal(1, stops);

        d.BytesReceived = 2048;
        d.State = BrowserDownloadState.Finished;
        page.RaiseDownloadChanged(d);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("Saved · 2 KB", Texts(panel));
        vm.ShowDownloadCommand.Execute(d);
        Assert.Equal(new[] { d }, shown);
        vm.CancelDownloadCommand.Execute(d);
        Assert.Equal(1, stops); // finished: nothing to stop

        vm.ClearDownloadsCommand.Execute(null);
        Assert.False(vm.HasDownloads);
        w.Close();
    }

    [AvaloniaFact]
    public void A_navigation_that_turns_into_a_download_leaves_the_page_where_it_was()
    {
        var vm = new PreviewViewModel();
        vm.NavigateCommand.Execute("https://example.com/reports");
        vm.OnNavigationCompleted(vm.ActiveTab, new Uri("https://example.com/reports"), true, false, false);
        // Avalonia reports the link as a navigation before the response turns it into a download
        vm.OnNavigationStarted(vm.ActiveTab, new Uri("https://example.com/files/q3.pdf"));
        Assert.True(vm.IsLoading);

        vm.ActiveTab.Page.RaiseDownloadChanged(new BrowserDownload(new Uri("https://example.com/files/q3.pdf"), new Uri("https://example.com/reports"))
        {
            State = BrowserDownloadState.Asking,
        });
        Assert.False(vm.IsLoading);
        Assert.Equal("https://example.com/reports", vm.Address);
    }

    // ── who caused it ──

    [Fact]
    public async Task What_the_page_asks_while_omp_acts_on_it_and_a_second_after_is_omps()
    {
        var vm = new PreviewViewModel();
        var tab = vm.OpenAgentTab("surface:3");
        Assert.False(tab.Page.AgentActive());
        var outer = tab.AgentAction();
        var inner = tab.AgentAction();
        inner.Dispose();
        Assert.True(tab.Page.AgentActive()); // the outer action still runs
        outer.Dispose();
        outer.Dispose(); // once
        Assert.True(tab.Page.AgentActive()); // grace: the page's answer to omp's last action
        await Task.Delay(PreviewTab.AgentGrace + TimeSpan.FromMilliseconds(250));
        Assert.False(tab.Page.AgentActive());
        Assert.False(vm.Tabs[0].Page.AgentActive()); // per tab
    }

    // ── engine hooks contract ──

    [Fact]
    public async Task Page_hooks_survive_a_failing_listener_and_a_crash_cancels_the_questions()
    {
        using var broker = new DialogBroker();
        var hooks = new BrowserPageHooks { Broker = () => broker };
        var seen = new List<string>();
        hooks.Failed += _ => throw new InvalidOperationException("listener bug");
        hooks.Failed += f => seen.Add(f.Message);
        hooks.RaiseFailed(new BrowserLoadFailure(null, "NSURLErrorDomain", -1001, "The request timed out.", true));
        Assert.Equal(new[] { "The request timed out." }, seen);

        var prompt = broker.PromptAsync(new PromptRequest("Name?", "Ann"), CancellationToken.None);
        var terminated = 0;
        hooks.Terminated += () => terminated++;
        hooks.RaiseTerminated();
        Assert.Null(await prompt.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, terminated);

        // Sign-in: an explicit handler wins, else the broker, else Cancel
        Assert.Null(await ((IBrowserAuthHandler)new BrowserPageHooks()).RequestCredentialsAsync(new CredentialRequest("example.com", 443, null, false, false), CancellationToken.None));
    }

    [Theory]
    [InlineData("NSURLErrorDomain", -999, true)]
    [InlineData("WebKitErrorDomain", 102, true)]
    [InlineData("NSURLErrorDomain", -1004, false)]
    [InlineData("WebKitErrorDomain", 101, false)]
    public void Cancelled_and_download_interrupted_navigations_are_not_failures(string domain, long code, bool ignored) =>
        Assert.Equal(ignored, BrowserLoadFailure.IsIgnored(domain, code));

    // ── site permissions ──

    [Fact]
    public void Always_allow_on_this_site_is_remembered_per_site_and_kind_across_restarts()
    {
        var dir = Directory.CreateTempSubdirectory("ompgui-sites-").FullName;
        try
        {
            var path = Path.Combine(dir, "browser-sites.json");
            var sites = new BrowserSiteSettings(path);
            Assert.False(sites.IsAlwaysAllowed("https://meet.example.com", PermissionKind.Microphone));
            sites.AlwaysAllow("https://Meet.Example.com:443", PermissionKind.CameraAndMicrophone);
            sites.DownloadFolder = dir;

            var again = new BrowserSiteSettings(path); // the next run
            Assert.True(again.IsAlwaysAllowed("https://meet.example.com", PermissionKind.Microphone));
            Assert.True(again.IsAlwaysAllowed("https://meet.example.com", PermissionKind.Camera));
            Assert.False(again.IsAlwaysAllowed("http://meet.example.com", PermissionKind.Camera)); // another origin
            Assert.False(again.IsAlwaysAllowed("https://other.example.com", PermissionKind.Microphone));
            Assert.Equal(dir, again.DownloadFolder);
            Assert.Equal(new[] { "https://meet.example.com" }, again.Sites);
            Assert.DoesNotContain("password", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);

            again.Forget("https://meet.example.com");
            Assert.False(new BrowserSiteSettings(path).IsAlwaysAllowed("https://meet.example.com", PermissionKind.Microphone));

            File.WriteAllText(path, "{ not json");
            Assert.Empty(new BrowserSiteSettings(path).Sites); // unreadable: starts empty
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Every_tab_remembers_into_the_panes_site_settings()
    {
        var sites = new BrowserSiteSettings();
        var vm = new PreviewViewModel(sites);
        var omp = vm.OpenAgentTab("surface:4");
        Assert.Same(sites, vm.Tabs[0].Page.SiteSettings);
        Assert.Same(sites, omp.Page.SiteSettings);
        Assert.Same(sites, vm.Downloads.Settings);
    }

    // ── user agent ──

    [Theory]
    [InlineData("27.0", "21625.1.29.18.28", "21625.1.29.18.28", "26.6", "Version/27.0 Safari/605.1.15")] // Safari's staged WebKit is loaded
    [InlineData("27.0", "21625.1.29.18.28", "21624.5.1.11.3", "26.6", "Version/26.6 Safari/605.1.15")] // another WebKit: this macOS's Safari
    [InlineData(null, null, "20621.1.15", "15.4", "Version/18.4 Safari/605.1.15")]
    [InlineData(null, null, null, "14.6", "Version/17.6 Safari/605.1.15")]
    [InlineData("27.0 beta", "1", "1", "26.0", "Version/26.0 Safari/605.1.15")] // not a version number
    public void The_user_agent_token_is_safaris_for_the_webkit_that_runs(string? safari, string? safariBuild, string? webKit, string os, string expected) =>
        Assert.Equal(expected, SafariUserAgent.ApplicationName(safari, safariBuild, webKit, Version.Parse(os)));

    // ── file names ──

    [Theory]
    [InlineData("../../etc/hosts", "hosts")]
    [InlineData("..\\..\\boot.ini", "boot.ini")]
    [InlineData(".profile", "profile")]
    [InlineData("a:b.txt", "a_b.txt")]
    [InlineData("", "download")]
    [InlineData(null, "download")]
    public void Download_names_never_leave_the_chosen_folder(string? name, string expected) =>
        Assert.Equal(expected, BrowserDownloads.SafeFileName(name));

    [Fact]
    public void A_taken_name_gets_a_number()
    {
        var dir = Directory.CreateTempSubdirectory("ompgui-unique-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "");
            File.WriteAllText(Path.Combine(dir, "a (2).txt"), "");
            Assert.Equal(Path.Combine(dir, "a (3).txt"), BrowserDownloads.UniquePath(dir, "a.txt"));
            Assert.Equal(Path.Combine(dir, "b"), BrowserDownloads.UniquePath(dir, "../b"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
