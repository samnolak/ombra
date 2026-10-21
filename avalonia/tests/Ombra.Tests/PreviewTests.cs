using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Controls;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>Browser preview panel: local URL detection, address normalisation, and the control without a web engine.</summary>
public sealed class PreviewTests
{
    // ── URL detection ──

    [Theory]
    [InlineData("  ➜  Local:   http://localhost:5173/", "http://localhost:5173")]
    [InlineData("Uvicorn running on http://127.0.0.1:8000/docs (Press CTRL+C to quit)", "http://127.0.0.1:8000/docs")]
    [InlineData("Listening on http://0.0.0.0:3000", "http://localhost:3000")]
    [InlineData("server at http://[::1]:4000/app?x=1#top", "http://[::1]:4000/app?x=1#top")]
    [InlineData("listening on http://[::]:8080", "http://localhost:8080")]
    [InlineData("Open https://localhost:8443.", "https://localhost:8443")]
    [InlineData("(see http://localhost:3000/a)", "http://localhost:3000/a")]
    [InlineData("\"url\": \"http://localhost:9000/api\",", "http://localhost:9000/api")]
    [InlineData("HTTP://LOCALHOST:3000", "http://localhost:3000")]
    [InlineData("ready on http://localhost", "http://localhost")]
    // Vite colours the address and bolds the port
    [InlineData("  \u001b[32m➜\u001b[39m  \u001b[1mLocal\u001b[22m:   \u001b[36mhttp://localhost:\u001b[1m5173\u001b[22m/\u001b[39m", "http://localhost:5173")]
    public void Finds_a_local_address(string text, string expected) =>
        Assert.Equal(expected, Assert.Single(PreviewViewModel.FindLocalUrls(text)));

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://localhost.example.com:3000")]
    [InlineData("http://localhostfoo:3000")]
    [InlineData("http://127.0.0.10:3000")]
    [InlineData("http://192.168.1.5:3000")]
    [InlineData("ftp://localhost:21")]
    [InlineData("file:///home/me/index.html")]
    [InlineData("localhost:3000 (no scheme in plain text)")]
    [InlineData("http://localhost:99999")]
    [InlineData("xhttp://localhost:3000")]
    [InlineData("")]
    public void Ignores_what_is_not_a_local_web_address(string text) => Assert.Empty(PreviewViewModel.FindLocalUrls(text));

    [Fact]
    public void Several_addresses_in_one_text_come_in_order_without_repeats()
    {
        var found = PreviewViewModel.FindLocalUrls("Local: http://localhost:5173/\nNetwork: http://0.0.0.0:5173/\nalso http://127.0.0.1:8000");
        Assert.Equal(["http://localhost:5173", "http://127.0.0.1:8000"], found);
    }

    [AvaloniaFact]
    public void Suggestions_are_deduplicated_most_recent_first_and_at_most_five()
    {
        var vm = new PreviewViewModel();
        vm.OfferUrlsFrom("http://localhost:3000");
        vm.OfferUrlsFrom("again http://localhost:3000/ and nothing else");
        Assert.Equal(["http://localhost:3000"], vm.Suggestions);

        for (var port = 3001; port <= 3006; port++) vm.OfferUrlsFrom($"started on http://localhost:{port}");
        Assert.Equal(PreviewViewModel.MaxSuggestions, vm.Suggestions.Count);
        Assert.Equal(["http://localhost:3006", "http://localhost:3005", "http://localhost:3004", "http://localhost:3003", "http://localhost:3002"], vm.Suggestions);

        // One seen before moves to the front instead of being added twice.
        vm.OfferUrlsFrom("http://localhost:3003");
        Assert.Equal("http://localhost:3003", vm.Suggestions[0]);
        Assert.Equal(5, vm.Suggestions.Count);
        Assert.Single(vm.Suggestions, s => s == "http://localhost:3003");
        Assert.True(vm.HasSuggestions);
    }

    [AvaloniaFact]
    public void The_first_address_fills_the_box_but_nothing_is_navigated()
    {
        var vm = new PreviewViewModel();
        var navigations = 0;
        vm.NavigationRequested += _ => navigations++;
        vm.OfferUrlsFrom("no address here");
        Assert.Equal("", vm.Address);
        vm.OfferUrlsFrom("VITE ready at http://localhost:5173/");
        Assert.Equal("http://localhost:5173", vm.Address);
        vm.OfferUrlsFrom("api on http://localhost:8000");
        Assert.Equal("http://localhost:5173", vm.Address); // the first one stays; later ones are chips
        Assert.Equal(0, navigations);
        Assert.Null(vm.CurrentUrl);

        // With a page open the box keeps showing that page.
        vm.NavigateCommand.Execute(null);
        vm.OfferUrlsFrom("http://localhost:9999");
        Assert.Equal("http://localhost:5173", vm.Address);
    }

    [AvaloniaFact]
    public async Task Addresses_offered_from_another_thread_arrive_on_the_ui_thread()
    {
        var vm = new PreviewViewModel();
        await Task.Run(() => vm.OfferUrlsFrom("http://localhost:4321"));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.Suggestions.Count == 0 && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.Equal(["http://localhost:4321"], vm.Suggestions);
    }

    // ── Navigation ──

    [Theory]
    [InlineData("localhost:3000", "http://localhost:3000/")]
    [InlineData("  localhost:3000/app?q=1 ", "http://localhost:3000/app?q=1")]
    [InlineData("127.0.0.1:8080", "http://127.0.0.1:8080/")]
    [InlineData("[::1]:3000", "http://[::1]:3000/")]
    [InlineData("localhost", "http://localhost/")]
    [InlineData("0.0.0.0:5000", "http://localhost:5000/")]
    [InlineData("http://0.0.0.0:5000/x", "http://localhost:5000/x")]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("https://example.com/a", "https://example.com/a")]
    [InlineData("HTTP://LOCALHOST:3000", "http://localhost:3000/")]
    public void Navigate_completes_and_normalises_the_address(string typed, string expected)
    {
        var vm = new PreviewViewModel { Address = typed };
        Uri? requested = null;
        vm.NavigationRequested += u => requested = u;
        vm.NavigateCommand.Execute(null);
        Assert.Equal(expected, requested?.AbsoluteUri);
        Assert.Equal(expected, vm.CurrentUrl?.AbsoluteUri);
        Assert.Equal(PreviewViewModel.Display(requested!), vm.Address);
        Assert.False(vm.HasMessage);
        Assert.True(vm.IsLoading);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("file:/etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(document.cookie)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("about:blank")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("ftp://localhost/")]
    [InlineData("vbscript:msgbox")]
    [InlineData("http://")]
    [InlineData("not an address")]
    public void Navigate_refuses_anything_but_http_and_https(string typed)
    {
        var vm = new PreviewViewModel { Address = typed };
        var navigations = 0;
        vm.NavigationRequested += _ => navigations++;
        vm.NavigateCommand.Execute(null);
        Assert.Equal(0, navigations);
        Assert.Null(vm.CurrentUrl);
        Assert.True(vm.HasMessage, "a refusal says why");
        Assert.False(PreviewViewModel.TryNormalize(typed, out _, out var error));
        Assert.NotEmpty(error);
    }

    [AvaloniaFact]
    public void A_suggestion_chip_navigates_to_its_address_and_the_commands_follow_the_page()
    {
        var vm = new PreviewViewModel();
        Assert.False(vm.ReloadCommand.CanExecute(null));
        Assert.False(vm.OpenExternallyCommand.CanExecute(null));
        Assert.False(vm.BackCommand.CanExecute(null));
        vm.NavigateCommand.Execute("http://localhost:5173");
        Assert.Equal("http://localhost:5173", vm.Address);
        Assert.True(vm.ReloadCommand.CanExecute(null));

        Uri? external = null;
        vm.OpenExternallyRequested += u => external = u;
        vm.OpenExternallyCommand.Execute(null);
        Assert.Equal("http://localhost:5173/", external?.AbsoluteUri);

        // The web view reports a link clicked in the page, then history.
        vm.OnNavigationStarted(new Uri("http://localhost:5173/about"));
        vm.OnNavigationCompleted(new Uri("http://localhost:5173/about"), true, canGoBack: true, canGoForward: false);
        Assert.Equal("http://localhost:5173/about", vm.Address);
        Assert.False(vm.IsLoading);
        Assert.True(vm.BackCommand.CanExecute(null));
        Assert.False(vm.ForwardCommand.CanExecute(null));
        var back = 0;
        vm.BackRequested += () => back++;
        vm.BackCommand.Execute(null);
        Assert.Equal(1, back);

        vm.OnNavigationCompleted(new Uri("http://localhost:5173/missing"), false, true, false);
        Assert.True(vm.HasMessage);
    }

    // ── MainViewModel: panel state and the tool-output hook ──

    [AvaloniaFact]
    public async Task The_main_view_model_shows_the_browser_tab_and_offers_urls_from_tool_output()
    {
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        Assert.False(vm.IsPreviewOpen);
        // ⇧⌘B: the right panel opens on the browser, its pages in the strip; again hides the panel and keeps them
        vm.TogglePaneCommand.Execute(SidePane.Browser);
        Assert.True(vm.IsPreviewOpen);
        Assert.True(vm.IsSidePaneOpen);
        Assert.True(vm.HasBrowserTabs);
        vm.TogglePaneCommand.Execute(SidePane.Browser);
        Assert.False(vm.IsPreviewOpen);
        Assert.False(vm.IsSidePaneOpen);
        Assert.True(vm.HasBrowserTabs);
        // Closing the browser (the × of the user's own page, or IsPreviewOpen = false) takes its pages out of the
        // panel and ends annotate mode; the panel shows its next tab, or hides
        vm.ShowPaneCommand.Execute(SidePane.Plan);
        vm.IsPreviewOpen = true;
        vm.Preview.IsAnnotating = true;
        vm.IsPreviewOpen = false;
        Assert.False(vm.Preview.IsAnnotating);
        Assert.False(vm.HasBrowserTabs);
        Assert.Equal(SidePane.Plan, vm.ActivePane);
        vm.IsPreviewOpen = true;
        vm.ClosePanelTabCommand.Execute(vm.PanelTabs.Single(t => t.IsPage));
        Assert.False(vm.HasBrowserTabs);
        Assert.Equal(SidePane.Plan, vm.ActivePane);

        static SessionSnapshot Snap(long version, params TranscriptItem[] items) =>
            new(version, SessionPhase.Ready, items, null, 0, null, 0, 0, null, null, null, []);
        var running = new ToolItem(1, "call-1", "bash", "npm run dev", ToolStatus.Running, "> vite\n\n  VITE v6 ready\n");
        vm.Apply(Snap(1, running));
        Assert.Empty(vm.Preview.Suggestions);
        // The output grows while the tool runs; the address arrives in a later update.
        vm.Apply(Snap(2, running with { Output = running.Output + "  ➜  Local:   http://localhost:5173/\n" }));
        Assert.Equal(["http://localhost:5173"], vm.Preview.Suggestions);
        Assert.Equal("http://localhost:5173", vm.Preview.Address);
        // Another tool, and text that is not a tool's (a user message) is not scanned.
        vm.Apply(Snap(3, running with { Output = running.Output + "  ➜  Local:   http://localhost:5173/\n" },
            new UserItem(2, "try http://localhost:1111", true),
            new ToolItem(3, "call-2", "bash", "python3 -m http.server", ToolStatus.Succeeded, "Serving HTTP on 0.0.0.0 port 8000 (http://0.0.0.0:8000/) ...")));
        Assert.Equal(["http://localhost:8000", "http://localhost:5173"], vm.Preview.Suggestions);
        await vm.DisposeAsync();
    }

    // ── The control, headless (no native window, so no web engine) ──

    private static (Window w, PreviewPanel panel, PreviewViewModel vm) Show()
    {
        var vm = new PreviewViewModel();
        var panel = new PreviewPanel { DataContext = vm };
        var w = new Window { Content = panel, Width = 520, Height = 640 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (w, panel, vm);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [AvaloniaFact]
    public void The_panel_renders_its_empty_state_and_suggestion_chips()
    {
        var (w, panel, vm) = Show();
        Assert.True(Named<StackPanel>(panel, "EmptyState").IsEffectivelyVisible);
        Assert.False(Named<Border>(panel, "FallbackCard").IsVisible);
        Assert.False(Named<Panel>(panel, "WebHost").IsVisible);
        Assert.False(Named<ItemsControl>(panel, "SuggestionRow").IsVisible);
        Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("Start a dev server and its address shows up here"));
        // The toolbar is 48 high; back, forward and reload share one pill (the ChatGPT app's grouped toolbar)
        var back = Named<Button>(panel, "BackButton");
        Assert.Equal(48, back.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("preview-toolbar")).Height);
        var pill = back.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("toolbar-pill"));
        Assert.Contains(pill.GetVisualDescendants().OfType<Button>(), b => b.Name == "ReloadButton");

        vm.OfferUrlsFrom("Local: http://localhost:5173/ and http://127.0.0.1:8000");
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<ItemsControl>(panel, "SuggestionRow").IsVisible);
        // With a server found, the empty page points at it instead of asking for one
        Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("Open the address found above, or type one"));
        var chips = panel.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("chip")).ToList();
        Assert.Equal(2, chips.Count);
        Assert.Equal("http://127.0.0.1:8000", chips[0].CommandParameter);
        using var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Null(panel.WebView);
        w.Close();
    }

    /// <summary>The right panel on the browser (its pages are the strip's tabs), as the window hosts it.</summary>
    private static (Window w, Ombra.App.Views.Panes.RightPanel panel, MainViewModel main) ShowPanel(double width = 520)
    {
        var main = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var panel = new Ombra.App.Views.Panes.RightPanel { DataContext = main };
        var w = new Window { Content = panel, Width = width, Height = 640 };
        w.Show();
        main.IsPreviewOpen = true;
        Dispatcher.UIThread.RunJobs();
        return (w, panel, main);
    }

    private static List<Border> PanelTabs(Control panel) =>
        [.. Named<ItemsControl>(panel, "PanelTabItems").GetVisualDescendants().OfType<Border>().Where(x => x.Classes.Contains("panel-tab") && x.IsEffectivelyVisible)];

    [AvaloniaFact]
    public async Task Omps_tabs_show_in_the_panels_strip_the_user_can_switch_and_close()
    {
        var (w, panel, main) = ShowPanel();
        var vm = main.Preview;
        var a = vm.OpenAgentTab("A");
        vm.NavigateTabForAgent(a, "localhost:3000");
        var b = vm.OpenAgentTab("B");
        Dispatcher.UIThread.RunJobs();
        var items = Named<ItemsControl>(panel, "PanelTabItems");
        // The user's own tab, empty and not shown, is not listed (no "New tab" before omp's)
        Assert.False(items.ContainerFromIndex(0)!.IsVisible);
        var tabs = PanelTabs(panel);
        Assert.Equal(2, tabs.Count);
        Assert.Equal([false, true], tabs.Select(x => x.Classes.Contains("active")));
        var closers = tabs.Select(x => x.GetVisualDescendants().OfType<Button>().Any(c => c.IsVisible && AutomationProperties.GetName(c) == Loc.T("Close tab"))).ToList();
        Assert.Equal([true, true], closers);
        Assert.Contains(tabs[0].GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "localhost:3000");

        // A click on a tab shows it; its close button drops it
        var select = tabs[0].GetVisualDescendants().OfType<Button>().First(c => c.Classes.Contains("panel-tab-button"));
        select.Command!.Execute(select.CommandParameter);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(a, vm.ActiveTab);
        Assert.Equal("http://localhost:3000", vm.Address);
        var close = tabs[1].GetVisualDescendants().OfType<Button>().First(c => AutomationProperties.GetName(c) == Loc.T("Close tab"));
        close.Command!.Execute(close.CommandParameter);
        Dispatcher.UIThread.RunJobs();
        Assert.True(b.IsClosed);
        Assert.Equal(2, vm.Tabs.Count);
        Assert.Equal(2, main.PanelTabs.Count(t => t.IsPage));
        using (var frame = w.CaptureRenderedFrame()) Assert.NotNull(frame);

        // The × of the user's own page takes the browser out of the panel: with nothing else in it, the panel hides
        main.ClosePanelTabCommand.Execute(main.PanelTabs.First(t => t.Page == vm.Tabs[0]));
        Assert.False(main.HasBrowserTabs);
        Assert.False(main.IsPreviewOpen);
        Assert.False(main.IsSidePaneOpen);
        w.Close();
        await main.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Plus_opens_the_new_tab_page_whose_browser_opens_a_page_and_an_empty_one_left_goes()
    {
        var (w, panel, main) = ShowPanel();
        var vm = main.Preview;
        double Left(Visual v) => v.TranslatePoint(default, panel)!.Value.X;
        vm.NavigateCommand.Execute("localhost:3000");
        Dispatcher.UIThread.RunJobs();

        // + right after the last tab opens the new-tab page, a tab of its own
        var plus = Named<Button>(panel, "NewPanelTabButton");
        plus.Command!.Execute(plus.CommandParameter);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SidePane.NewTab, main.ActivePane);
        Assert.True(Named<Ombra.App.Views.Panes.NewTabPage>(panel, "NewTabPage").IsEffectivelyVisible);
        var last = Named<ItemsControl>(panel, "PanelTabItems").ContainerFromIndex(main.PanelTabs.Count - 1)!;
        Assert.InRange(Left(plus) - (Left(last) + last.Bounds.Width), 0, 8);
        Assert.True(Left(plus) + plus.Bounds.Width < panel.Bounds.Width - 100); // after the tabs, not at the far end

        // Its Browser: a new empty page of the user's after the first, shown, closable, the keyboard in the address box;
        // the page takes the new-tab page's place
        var browser = Named<Button>(panel, "BrowserTool");
        browser.Command!.Execute(browser.CommandParameter);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.Tabs.Count);
        var mine = vm.Tabs[1];
        Assert.Same(mine, vm.ActiveTab);
        Assert.True(mine.CanClose);
        Assert.True(main.IsPreviewOpen);
        Assert.DoesNotContain(main.PanelTabs, t => t.Pane == SidePane.NewTab);
        Assert.Equal("", vm.Address);
        Assert.True(Named<TextBox>(panel, "AddressBox").IsFocused);

        // Left empty, the tab goes
        vm.SelectTabCommand.Execute(vm.Tabs[0]);
        Assert.True(mine.IsClosed);
        Assert.Single(vm.Tabs);
        Assert.Single(main.PanelTabs, t => t.IsPage);
        Assert.Equal("http://localhost:3000/", vm.CurrentUrl!.AbsoluteUri);

        // A new page given an address stays; its × shows the neighbour with a page
        vm.NewTabCommand.Execute(null);
        vm.NavigateCommand.Execute("localhost:4000");
        var second = vm.ActiveTab;
        vm.SelectTabCommand.Execute(vm.Tabs[0]);
        Assert.Contains(second, vm.Tabs);
        main.SelectPanelTabCommand.Execute(main.PanelTabs.Single(t => t.Page == second));
        main.ClosePanelTabCommand.Execute(main.PanelTabs.Single(t => t.Page == second));
        Assert.Same(vm.Tabs[0], vm.ActiveTab);
        Assert.True(main.IsPreviewOpen);
        Assert.Equal("http://localhost:3000/", vm.CurrentUrl!.AbsoluteUri);
        w.Close();
        await main.DisposeAsync();
    }

    [AvaloniaFact]
    public void Device_mode_sets_the_pages_size_and_platform_and_comes_back_as_it_was()
    {
        var (w, panel, vm) = Show();
        Assert.False(Named<Border>(panel, "DeviceBar").IsVisible);
        Assert.Null(vm.DeviceViewport);
        Assert.Null(vm.DeviceUserAgent);

        // The toolbar button: a phone the first time, its user agent, the bar with its controls
        vm.ToggleDeviceCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<Border>(panel, "DeviceBar").IsEffectivelyVisible);
        Assert.Equal(new PixelSize(393, 852), vm.DeviceViewport);
        Assert.Contains("iPhone", vm.DeviceUserAgent);
        Assert.Equal("393×852 · iPhone", vm.DeviceLabel);

        // Turned, then another preset (upright again, its platform), then a size typed in (the platform stays)
        vm.RotateDeviceCommand.Execute(null);
        Assert.Equal(new PixelSize(852, 393), vm.DeviceViewport);
        vm.ChooseDeviceCommand.Execute(PreviewViewModel.DevicePresets.Single(p => p.Name == "Pixel 8"));
        Assert.Equal((new PixelSize(412, 915), DevicePlatform.Android), (vm.DeviceViewport!.Value, vm.DevicePlatform));
        Assert.Contains("Android", vm.DeviceUserAgent);
        (vm.CustomWidth, vm.CustomHeight) = ("100", "800");
        vm.ApplyCustomSizeCommand.Execute(null);
        Assert.Equal(new PixelSize(412, 915), vm.DeviceViewport); // out of range: refused, said why
        Assert.Contains("200", vm.Message);
        (vm.CustomWidth, vm.CustomHeight) = ("600", "800");
        vm.ApplyCustomSizeCommand.Execute(null);
        Assert.Equal((new PixelSize(600, 800), DevicePlatform.Android, (DevicePreset?)null), (vm.DeviceViewport!.Value, vm.DevicePlatform, vm.DevicePreset));
        vm.ChoosePlatformCommand.Execute(DevicePlatform.Desktop);
        Assert.Null(vm.DeviceUserAgent); // desktop: the engine's own
        vm.RotateDeviceCommand.Execute(null);

        // Off fits the pane again; on again brings back the same device
        vm.ToggleDeviceCommand.Execute(null);
        Assert.Equal((null, DevicePlatform.Desktop), (vm.DeviceViewport, vm.DevicePlatform));
        vm.ToggleDeviceCommand.Execute(null);
        Assert.Equal(new PixelSize(800, 600), vm.DeviceViewport);
        w.Close();
    }

    /// <summary>
    /// The Nightly runtime emulates more of a device: each preset brings its pixel ratio and, for a phone or tablet,
    /// touch; the platform turns touch on or off and the bar's toggle overrides it; leaving device mode resets both and
    /// the toggle brings them back. What the runtime gets (EmbedDevice) follows.
    /// </summary>
    [AvaloniaFact]
    public void Device_presets_bring_their_pixel_ratio_and_touch()
    {
        var (w, _, vm) = Show();
        Assert.Null(vm.EmbedDevice);
        Assert.Equal([2, 3, 2.625, 2, 2, 2, 1], PreviewViewModel.DevicePresets.Select(p => p.PixelRatio));

        vm.ChooseDeviceCommand.Execute(PreviewViewModel.DevicePresets.Single(p => p.Name == "Pixel 8"));
        Assert.Equal((2.625, true), (vm.DevicePixelRatio, vm.DeviceTouch));
        Assert.Equal(new Ombra.App.Nightly.NightlyProtocol.Device(true, 2.625, vm.DeviceUserAgent!, true, false), vm.EmbedDevice);
        vm.RotateDeviceCommand.Execute(null);
        Assert.True(vm.EmbedDevice!.Value.Landscape);

        // A desktop preset: its ratio, no touch, Nightly's own user agent; the toggle gives it touch
        vm.ChooseDeviceCommand.Execute(PreviewViewModel.DevicePresets.Single(p => p.Name == "Laptop"));
        Assert.Equal(new Ombra.App.Nightly.NightlyProtocol.Device(false, 2, "", false, false), vm.EmbedDevice);
        vm.ToggleTouchCommand.Execute(null);
        Assert.True(vm.EmbedDevice!.Value.Touch);
        vm.ChoosePlatformCommand.Execute(DevicePlatform.IPad);
        Assert.Equal((true, true), (vm.EmbedDevice!.Value.Mobile, vm.DeviceTouch));
        vm.ChoosePlatformCommand.Execute(DevicePlatform.Desktop);
        Assert.False(vm.DeviceTouch);
        vm.DevicePixelRatio = 3;

        // Off: nothing emulated; on again: the same ratio and touch
        vm.ToggleDeviceCommand.Execute(null);
        Assert.Equal((0d, false), (vm.DevicePixelRatio, vm.DeviceTouch));
        Assert.Null(vm.EmbedDevice);
        vm.ToggleDeviceCommand.Execute(null);
        Assert.Equal((3d, false, new PixelSize(1280, 800)), (vm.DevicePixelRatio, vm.DeviceTouch, vm.DeviceViewport!.Value));
        Assert.Equal("3×", PreviewViewModel.PixelRatioName(3));
        Assert.Equal(Loc.T("Display’s"), PreviewViewModel.PixelRatioName(0));
        w.Close();
    }

    /// <summary>
    /// A Nightly pane gives the page the device's size even where the panel is smaller: the pane is the device's size
    /// where it fits (1:1) and otherwise the largest that fits with the device's aspect, the frame scaled into it (as a
    /// browser's device toolbar does); without a device it takes the room it gets.
    /// </summary>
    [AvaloniaFact]
    public void A_nightly_pane_keeps_the_devices_size_and_scales_down_where_the_panel_is_smaller()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var room = new Size(480, 681);
        Size Measured(Size? viewport)
        {
            var pane = new NightlyPane { Viewport = viewport };
            pane.Measure(room);
            return pane.DesiredSize;
        }
        Assert.Equal(room, Measured(null));
        Assert.Equal(new Size(375, 667), Measured(new Size(375, 667))); // iPhone SE fits: 1:1
        Assert.Equal(new Size(480, 300), Measured(new Size(1440, 900))); // Desktop: a third
        // Layout rounds the scaled size up to a whole device pixel (here 1 DIP).
        var phone = Measured(new Size(393, 852)); // iPhone 15 Pro: as tall as the room
        Assert.Equal(681, phone.Height, 6);
        Assert.InRange(phone.Width, 393 * 681 / 852d, 393 * 681 / 852d + 1);
        var landscape = Measured(new Size(852, 393)); // turned: as wide as the room
        Assert.Equal(480, landscape.Width, 6);
        Assert.InRange(landscape.Height, 393 * 480 / 852d, 393 * 480 / 852d + 1);
    }

    /// <summary>The agent's browser.set_device: a preset by name (any case), then what it asks over it; checked before anything changes.</summary>
    [AvaloniaFact]
    public void The_agent_sets_the_device_as_the_bar_would()
    {
        var (w, _, vm) = Show();
        var state = vm.ApplyAgentDevice(new AgentDeviceRequest(Preset: "iphone se", Landscape: true));
        Assert.Equal(new AgentDeviceState("iPhone SE", 667, 375, 2, true, "iphone", true, false), state);
        Assert.Equal(new PixelSize(667, 375), vm.DeviceViewport);

        state = vm.ApplyAgentDevice(new AgentDeviceRequest(Width: 500, Height: 700, PixelRatio: 1.5, Touch: false));
        Assert.Equal(new AgentDeviceState(null, 500, 700, 1.5, false, "iphone", false, false), state);
        Assert.Equal("ipad", vm.ApplyAgentDevice(new AgentDeviceRequest(Platform: "iPad")).Platform);

        var unknown = Assert.Throws<AgentBrowserException>(() => vm.ApplyAgentDevice(new AgentDeviceRequest(Preset: "Nokia 3310", PixelRatio: 3)));
        Assert.Equal("invalid_params", unknown.Code);
        Assert.Contains("Pixel 8", unknown.Message);
        Assert.Throws<AgentBrowserException>(() => vm.ApplyAgentDevice(new AgentDeviceRequest(Width: 50, Height: 700)));
        Assert.Throws<AgentBrowserException>(() => vm.ApplyAgentDevice(new AgentDeviceRequest(Platform: "windows phone")));
        Assert.Equal((1.5, new PixelSize(500, 700)), (vm.DevicePixelRatio, vm.DeviceViewport!.Value)); // the refused requests changed nothing

        Assert.Equal(new AgentDeviceState(null, null, null, 0, false, "desktop", false, false), vm.ApplyAgentDevice(null));
        Assert.Throws<AgentBrowserException>(() => vm.ApplyAgentDevice(new AgentDeviceRequest(Landscape: true)));
        Assert.Contains(PreviewViewModel.AgentDevicePresets, p => p == new AgentDevicePreset("Pixel 8", 412, 915, 2.625, "android"));
        w.Close();
    }

    /// <summary>
    /// Regression: with many tabs the strip was wider than the pane, so most close buttons lay out of sight, and its
    /// scroll bar was drawn over the tabs. Now long titles shrink to share the width, every tab keeps its close button,
    /// and past the narrowest the strip scrolls without a bar, keeping the active tab in view.
    /// </summary>
    [AvaloniaFact]
    public async Task Many_tabs_share_the_strip_and_keep_their_close_buttons()
    {
        var (w, panel, main) = ShowPanel(720);
        var vm = main.Preview;
        for (var i = 0; i < 4; i++) vm.OpenAgentTab("t" + i).Title = i == 1 ? "Short" : "Merchant Dashboard — Orders and refunds";
        Dispatcher.UIThread.RunJobs();
        var scroller = Named<ScrollViewer>(panel, "PanelTabScroller");
        Assert.Equal(ScrollBarVisibility.Hidden, scroller.HorizontalScrollBarVisibility);
        Button? CloseOf(Border tab) => tab.GetVisualDescendants().OfType<Button>().FirstOrDefault(c => c.IsVisible && AutomationProperties.GetName(c) == Loc.T("Close tab"));
        double Left(Visual v) => v.TranslatePoint(default, scroller)!.Value.X;

        // They fit: the long titles shrink alike (but no further than needed), the short one keeps its width, nothing scrolls
        Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 0.5);
        var tabs = PanelTabs(panel); // omp's four (the user's own, empty, is not listed)
        Assert.Equal(4, tabs.Count);
        Assert.Equal(tabs[0].Bounds.Width, tabs[3].Bounds.Width);
        Assert.InRange(tabs[0].Bounds.Width, 112, 219); // under their own 220
        Assert.True(tabs[1].Bounds.Width < tabs[0].Bounds.Width);
        foreach (var tab in tabs)
        {
            var close = CloseOf(tab)!;
            Assert.True(Left(close) + close.Bounds.Width <= Left(tab) + tab.Bounds.Width + 0.5); // inside its own tab
            Assert.True(Left(close) + close.Bounds.Width <= scroller.Viewport.Width + 0.5); // and in sight
        }

        // Too many for even their narrowest: the strip scrolls, the active (newest) tab in view, close button and all
        for (var i = 4; i < 12; i++) vm.OpenAgentTab("t" + i).Title = "Merchant Dashboard — Orders and refunds";
        Dispatcher.UIThread.RunJobs();
        tabs = PanelTabs(panel);
        Assert.True(scroller.Extent.Width > scroller.Viewport.Width);
        var activeClose = CloseOf(tabs[^1])!;
        Assert.InRange(Left(activeClose), 0, scroller.Viewport.Width - activeClose.Bounds.Width);
        var plus = Named<Button>(panel, "NewPanelTabButton"); // + stays in sight at the end
        Assert.InRange(plus.TranslatePoint(default, panel)!.Value.X + plus.Bounds.Width, scroller.Viewport.Width, panel.Bounds.Width + 0.5);
        vm.SelectTabCommand.Execute(vm.Tabs[0]);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, scroller.Offset.X);
        Assert.Equal("Merchant Dashboard — Orders and refunds\n" + Loc.T("Opened by omp"), main.PanelTabs[^1].ToolTip); // the whole title, trimmed in the strip
        w.Close();
        await main.DisposeAsync();
    }

    [AvaloniaFact]
    public void Without_a_web_engine_the_panel_shows_the_fallback_card_and_never_creates_a_web_view()
    {
        var (w, panel, vm) = Show();
        var box = Named<TextBox>(panel, "AddressBox");
        box.Focus();
        vm.Address = "localhost:3000";
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsFocused);
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); // Enter in the address box navigates
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("http://localhost:3000/", vm.CurrentUrl?.AbsoluteUri);
        Assert.True(vm.IsEngineUnavailable);
        Assert.Null(panel.WebView);
        Assert.False(Named<StackPanel>(panel, "EmptyState").IsVisible);
        Assert.False(Named<Panel>(panel, "WebHost").IsVisible);
        Assert.True(Named<Border>(panel, "FallbackCard").IsEffectivelyVisible);
        Assert.StartsWith(Loc.F("The embedded browser needs {0} on this system", "\0").Split('\0')[0], vm.FallbackText);
        Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.FallbackText);
        Assert.True(Named<Button>(panel, "FallbackOpenButton").Command!.CanExecute(null));
        using var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);

        // Reload checks again (the engine may have been installed since): still none here, still the card.
        vm.ReloadCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsEngineUnavailable);
        Assert.Null(panel.WebView);
        w.Close();
    }

    [AvaloniaFact]
    public void Nothing_native_is_attempted_while_a_container_hides_the_panel()
    {
        var vm = new PreviewViewModel();
        var panel = new PreviewPanel { DataContext = vm };
        var container = new Border { Child = panel, IsVisible = false };
        var w = new Window { Content = container, Width = 520, Height = 640 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        vm.NavigateCommand.Execute("localhost:5173");
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsEngineUnavailable); // not checked yet: the panel is not shown
        container.IsVisible = true;
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsEngineUnavailable); // checked once shown (headless: no engine)
        Assert.Null(panel.WebView);
        w.Close();
    }

    [AvaloniaFact]
    public void The_engine_probe_refuses_a_window_without_a_native_surface()
    {
        Assert.False(PreviewPanel.Probe(null).Available);
        var w = new Window();
        w.Show();
        var probe = PreviewPanel.Probe(w);
        Assert.False(probe.Available);
        Assert.NotEmpty(probe.Engine);
        Assert.NotEmpty(probe.Detail);
        w.Close();
    }

    [Theory]
    [InlineData("\"Hello \\\"world\\\"\"", "Hello \"world\"")]
    [InlineData("Plain title", "Plain title")]
    [InlineData("null", "")]
    [InlineData(null, "")]
    public void Script_results_are_decoded(string? raw, string expected) => Assert.Equal(expected, PreviewPanel.DecodeScriptString(raw));

    // ── Site groups: four or more pages of one site are one chip with a list under it ──

    [Theory]
    [InlineData("https://codepen.io/dermalhealth/full/XJNNOWo", "codepen.io")]
    [InlineData("https://www.pinterest.com/pin/1", "pinterest.com")]
    [InlineData("https://news.bbc.co.uk/a", "bbc.co.uk")]
    [InlineData("http://localhost:4321/?screen=menu", "localhost:4321")]
    [InlineData("http://localhost:3000/", "localhost:3000")]
    [InlineData("http://127.0.0.1:8000/docs", "127.0.0.1:8000")]
    [InlineData("about:blank", null)]
    [InlineData("file:///tmp/a.html", null)]
    public void A_pages_site_is_its_registrable_domain_or_its_local_server(string url, string? key) =>
        Assert.Equal(key, TabSiteGroup.SiteKey(new Uri(url)));

    [AvaloniaFact]
    public async Task Four_pages_of_one_site_become_one_chip_whose_list_opens_downward_and_two_left_are_tabs_again()
    {
        var (w, panel, main) = ShowPanel(720);
        var vm = main.Preview;
        PreviewTab Open(string id, string url, string title)
        {
            var tab = vm.OpenAgentTab(id);
            Assert.Null(vm.NavigateTabForAgent(tab, url));
            tab.Title = title;
            return tab;
        }
        var pen = Open("pen", "https://codepen.io/dermalhealth/full/XJNNOWo", "Bold modern landing page");
        var kiosks = Enumerable.Range(0, 3).Select(i => Open("k" + i, "http://localhost:4321/", "Kiosk · Mix")).ToList();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, PanelTabs(panel).Count); // three of a site are still tabs
        Assert.All(kiosks, k => Assert.Null(k.SiteGroup));

        kiosks.Add(Open("k3", "http://localhost:4321/?screen=menu", "Kiosk · Mix"));
        Dispatcher.UIThread.RunJobs();
        var group = Assert.IsType<TabSiteGroup>(kiosks[0].SiteGroup);
        Assert.Equal((4, "Kiosk · Mix", "localhost:4321"), (group.Count, group.Label, group.Key));
        Assert.Equal(kiosks, group.Members);
        Assert.Null(pen.SiteGroup);
        var chip = panel.GetVisualDescendants().OfType<TabGroupChip>().Single(c => c.IsEffectivelyVisible);
        Assert.Equal(2, PanelTabs(panel).Count); // codepen's tab and the group's chip
        Assert.True(chip.IsActive); // the newest Kiosk page is the one shown
        Assert.Equal("4", chip.FindControl<TextBlock>("CountText")!.Text);

        // The chip opens its list under it; a row shows its page and closes the list
        chip.FindControl<Button>("ChipButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(group.IsOpen);
        var popup = chip.FindControl<Popup>("ListPopup")!;
        Assert.True(popup.IsOpen);
        Assert.Equal(PlacementMode.BottomEdgeAlignedLeft, popup.Placement);
        var rows = chip.FindControl<ItemsControl>("Rows")!.GetRealizedContainers().ToList();
        Assert.Equal(4, rows.Count);
        for (var t = 0; t < 25; t++) // the list's entrance (card, then rows 40 ms apart) settles
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20);
        }
        Assert.All(rows, r => Assert.Equal(1, ((Avalonia.Controls.Presenters.ContentPresenter)r).Child!.Opacity));
        MicroUxTabTests.Shot(w, "site-group-open");
        var second = rows[1].GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("menu-item"));
        second.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Same(kiosks[1], vm.ActiveTab);
        Assert.False(group.IsOpen);
        Assert.True(chip.IsActive);

        // Closing pages: three stay a group (no flicker), two are tabs again
        main.ClosePageCommand.Execute(kiosks[3]);
        Assert.Equal(3, group.Count);
        Assert.Same(group, kiosks[0].SiteGroup);
        main.ClosePageCommand.Execute(kiosks[2]);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(kiosks[0].SiteGroup);
        Assert.Equal(3, PanelTabs(panel).Count);
        Assert.DoesNotContain(panel.GetVisualDescendants().OfType<TabGroupChip>(), c => c.IsEffectivelyVisible);
        w.Close();
        await main.DisposeAsync();
    }
}
