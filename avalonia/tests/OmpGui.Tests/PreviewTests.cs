using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.Controls;
using OmpGui.App.ViewModels;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

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

        var closed = 0;
        vm.CloseRequested += () => closed++;
        vm.CloseCommand.Execute(null);
        Assert.Equal(1, closed);
    }

    // ── MainViewModel: panel state and the tool-output hook ──

    [AvaloniaFact]
    public async Task The_main_view_model_toggles_the_panel_and_offers_urls_from_tool_output()
    {
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        Assert.False(vm.IsPreviewOpen);
        vm.TogglePreviewCommand.Execute(null);
        Assert.True(vm.IsPreviewOpen);
        vm.Preview.CloseCommand.Execute(null);
        Assert.False(vm.IsPreviewOpen);

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
        Assert.False(Named<Border>(panel, "WebHost").IsVisible);
        Assert.False(Named<ItemsControl>(panel, "SuggestionRow").IsVisible);
        Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Start a dev server and its address shows up here");
        Assert.Equal(48, Named<Button>(panel, "BackButton").Parent is Grid g && g.Parent is Border b ? b.Height : 0);

        vm.OfferUrlsFrom("Local: http://localhost:5173/ and http://127.0.0.1:8000");
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<ItemsControl>(panel, "SuggestionRow").IsVisible);
        // With a server found, the empty page points at it instead of asking for one
        Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Open the address found above, or type one");
        var chips = panel.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("chip")).ToList();
        Assert.Equal(2, chips.Count);
        Assert.Equal("http://127.0.0.1:8000", chips[0].CommandParameter);
        using var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Null(panel.WebView);
        w.Close();
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
        Assert.False(Named<Border>(panel, "WebHost").IsVisible);
        Assert.True(Named<Border>(panel, "FallbackCard").IsEffectivelyVisible);
        Assert.StartsWith("The embedded browser needs ", vm.FallbackText);
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
}
