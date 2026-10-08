using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.Controls;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// Right-click menus as the user opens them: a right click, and on macOS Control+click, on each surface (messages, a
/// reply, text fields, the sidebar's sessions and projects, the Files pane's rows and code, code blocks and links). Each
/// menu acts on the thing clicked, not on whatever is selected. With OMPGUI_REVIEW_DIR set, an open menu is saved in
/// both themes to its "menus" folder.
/// </summary>
public sealed class ContextMenuUiTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("OMPGUI_REVIEW_DIR");

    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Settle(int ms = 150)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    private static async Task<(MainWindow w, MainViewModel vm)> OpenAsync(string scenario, string? project = null, double height = 900)
    {
        var s = new SessionController(TestProcesses.FakeFactory(scenario, TestProcesses.TempDir("menu-sessions")),
            new LaunchRequest(project ?? TestProcesses.TempDir("menu-project")));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1300, Height = height };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        return (w, vm);
    }

    private static async Task SendAsync(MainWindow w, MainViewModel vm, string text)
    {
        w.FindControl<TextBox>("Composer")!.Focus();
        w.KeyTextInput(text);
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<UserRowViewModel>().Any(u => u.Text == text), "run " + text);
    }

    private static Point Center(Control c, TopLevel w) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w)!.Value;

    private static async Task RightClick(Window w, Control c)
    {
        var p = Center(c, w);
        w.MouseMove(p);
        w.MouseDown(p, MouseButton.Right);
        w.MouseUp(p, MouseButton.Right);
        await Settle();
    }

    private static async Task ControlClick(Window w, Control c)
    {
        var p = Center(c, w);
        w.MouseMove(p, RawInputModifiers.Control);
        w.MouseDown(p, MouseButton.Left, RawInputModifiers.Control);
        w.MouseUp(p, MouseButton.Left, RawInputModifiers.Control);
        await Settle();
    }

    /// <summary>The menu open on <paramref name="c"/> or around it (null: none).</summary>
    private static FlyoutBase? OpenMenu(Control c) =>
        c.GetSelfAndVisualAncestors().OfType<Control>().Select(x => x.ContextFlyout).FirstOrDefault(f => f is { IsOpen: true });

    private static Button Item(FlyoutBase menu, string name) =>
        ((Control)((Flyout)menu).Content!).GetLogicalDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);

    /// <summary>Chooses a menu row the way assistive technology does (its command and Click).</summary>
    private static async Task Choose(Control item)
    {
        ((IInvokeProvider)ControlAutomationPeer.CreatePeerForElement(item)).Invoke();
        await Settle();
    }

    private static T Within<T>(Control root, Func<T, bool> match) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(match);

    private static void Save(Window w, string name)
    {
        if (Dir is null) return;
        Directory.CreateDirectory(Path.Combine(Dir, "menus"));
        using var frame = w.CaptureRenderedFrame();
        using var f = File.Create(Path.Combine(Dir, "menus", name + ".png"));
        frame?.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    [AvaloniaFact]
    public async Task Messages_replies_and_the_message_box_open_their_menu_on_the_row_clicked()
    {
        var (w, vm) = await OpenAsync("markdown", height: 2400);
        var copied = new List<string>();
        vm.CopyTextRequested += copied.Add;
        var replies = new List<string>();
        AssistantRowViewModel.CopyRequested += replies.Add;
        try
        {
            await SendAsync(w, vm, "first message");
            await SendAsync(w, vm, "second message");
            await Until(() => vm.Rows.OfType<AssistantRowViewModel>().Count(r => r.HasText) == 2, "both replies");
            await Settle(400);
            var transcript = w.FindControl<ListBox>("Transcript")!;
            var users = vm.Rows.OfType<UserRowViewModel>().ToList();

            async Task<(Border Bubble, SelectableTextBlock Text)> Message(UserRowViewModel row)
            {
                transcript.ScrollIntoView(row);
                await Settle();
                var bubble = Within<Border>((Control)transcript.ContainerFromItem(row)!, b => b.Classes.Contains("user-bubble"));
                return (bubble, Within<SelectableTextBlock>(bubble, _ => true));
            }

            // A right click on the words of an earlier message: its menu, and Copy copies that message
            var (firstBubble, firstText) = await Message(users[0]);
            await RightClick(w, firstText);
            var menu = OpenMenu(firstText);
            Assert.Same(w.FindResource("UserMessageMenu"), menu);
            Assert.Same(firstBubble, ((PopupFlyoutBase)menu!).Target);
            Assert.True(Item(menu, "Rewind to here").IsEffectivelyVisible);
            await Choose(Item(menu, "Copy"));
            Assert.False(menu.IsOpen);
            Assert.Equal("first message", copied[^1]);

            // With words selected the text's own menu copies the selection
            firstText.SelectAll();
            await RightClick(w, firstText);
            Assert.IsType<MenuFlyout>(OpenMenu(firstText));
            OpenMenu(firstText)!.Hide();
            firstText.ClearSelection();

            // Control+click is a right click on macOS (and an ordinary click elsewhere); Esc closes the menu
            var (secondBubble, secondText) = await Message(users[1]);
            await ControlClick(w, secondText);
            if (OperatingSystem.IsMacOS())
            {
                menu = OpenMenu(secondText);
                Assert.Same(secondBubble, ((PopupFlyoutBase)menu!).Target);
                w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                await Settle();
                Assert.False(menu.IsOpen);
                await ControlClick(w, secondBubble);
                await Choose(Item(OpenMenu(secondBubble)!, "Copy"));
                Assert.Equal("second message", copied[^1]);
            }
            else Assert.Null(OpenMenu(secondText));

            // A reply: Copy is the text as it reads, Copy as Markdown its source; no Retry on a reply that worked
            var reply = vm.Rows.OfType<AssistantRowViewModel>().First(r => r.HasText);
            transcript.ScrollIntoView(reply);
            await Settle();
            var replyRow = Within<StackPanel>((Control)transcript.ContainerFromItem(reply)!, p => p.Classes.Contains("assistant"));
            var paragraph = Within<SelectableTextBlock>(Within<MarkdownView>(replyRow, _ => true), _ => true);
            await RightClick(w, paragraph);
            menu = OpenMenu(paragraph);
            Assert.Same(w.FindResource("ReplyMenu"), menu);
            Assert.Same(replyRow, ((PopupFlyoutBase)menu!).Target);
            Assert.False(Item(menu, "Retry").IsEffectivelyVisible);
            Save(w, "reply-menu-" + Avalonia.Application.Current!.ActualThemeVariant);
            await Choose(Item(menu, "Copy"));
            Assert.Contains("I will change the greet function and explain it.", replies[^1]);
            Assert.DoesNotContain("**", replies[^1]);
            await RightClick(w, paragraph);
            await Choose(Item(OpenMenu(paragraph)!, "Copy as Markdown"));
            Assert.Equal(reply.Text, replies[^1]);

            // In the reply, a code block's words open the code block's menu, a link its own
            var codeBlock = Within<Border>(replyRow, b => b.Classes.Contains("codeblock"));
            var codeText = Within<SelectableTextBlock>(codeBlock, _ => true);
            await RightClick(w, codeText);
            menu = OpenMenu(codeText);
            Assert.Same(codeBlock, ((PopupFlyoutBase)menu!).Target);
            await Choose(Item(menu, "Copy code"));
            Assert.Equal("def greet():\n    print('Hello')", (await w.Clipboard!.TryGetTextAsync())?.ReplaceLineEndings("\n"));
            var link = Within<HyperlinkButton>(replyRow, l => l.NavigateUri?.AbsoluteUri == "https://example.com/docs");
            await RightClick(w, link);
            menu = OpenMenu(link);
            Assert.Same(link, ((PopupFlyoutBase)menu!).Target);
            await Choose(Item(menu, "Copy link"));
            Assert.Equal("https://example.com/docs", await w.Clipboard!.TryGetTextAsync());

            // The message box: Cut, Copy, Paste and Select all, which selects the box's text
            var composer = w.FindControl<TextBox>("Composer")!;
            composer.Text = "draft words";
            await RightClick(w, composer);
            var textMenu = Assert.IsType<MenuFlyout>(OpenMenu(composer));
            Save(w, "text-field-menu-" + Avalonia.Application.Current!.ActualThemeVariant);
            var items = textMenu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(["Cut", "Copy", "Paste", "Select all"], items.Select(i => (string)i.Header!));
            var selectAll = Center(items[^1], w);
            w.MouseMove(selectAll);
            w.MouseDown(selectAll, MouseButton.Left);
            w.MouseUp(selectAll, MouseButton.Left);
            await Settle();
            Assert.False(textMenu.IsOpen);
            Assert.Equal("draft words", composer.SelectedText);
        }
        finally
        {
            AssistantRowViewModel.CopyRequested -= replies.Add;
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task Sidebar_sessions_and_projects_open_their_menu_even_while_sessions_cannot_change()
    {
        var (w, vm) = await OpenAsync("normal");
        var copied = new List<string>();
        vm.CopyTextRequested += copied.Add;
        await SendAsync(w, vm, "first topic");
        await Until(() => vm.Sessions.Any(x => x.Title == "first topic"), "first listed");
        await vm.NewSessionCommand.ExecuteAsync(null);
        await Until(() => vm.Rows.Count == 0 && vm.Phase == SessionPhase.Ready, "new session");
        await SendAsync(w, vm, "second topic");
        await Until(() => vm.Sessions.Count == 2 && vm.Sessions.Single(x => x.Title == "second topic").IsCurrent, "both listed");
        await Settle(300);
        var list = w.FindControl<ListBox>("SessionList")!;
        var other = vm.Sessions.Single(x => x.Title == "first topic");
        TextBlock Title() => Within<TextBlock>(Within<Panel>(list, p => p.Classes.Contains("session-row") && p.DataContext == other), t => t.Text == "first topic");

        // A session that is not the current one: the menu is for it
        await RightClick(w, Title());
        var menu = OpenMenu(Title());
        Assert.NotNull(menu);
        await Choose(Within<Button>((Control)((Flyout)menu!).Content!, b => b.Name == "CopyPathItem"));
        Assert.False(menu.IsOpen);
        Assert.Equal(other.Model.Path, copied[^1]);

        // While sessions cannot change (signing in) the rows still have their menu
        vm.SigningIn = "a provider";
        await Settle();
        await RightClick(w, Title());
        Assert.NotNull(OpenMenu(Title()));
        OpenMenu(Title())!.Hide();
        vm.SigningIn = null;
        await Settle();

        // Control+click opens the menu and does not open the session
        await ControlClick(w, Title());
        await Settle(300);
        Assert.False(other.IsCurrent);
        Assert.Equal(OperatingSystem.IsMacOS(), OpenMenu(Title()) is not null);
        OpenMenu(Title())?.Hide();

        // A project's header: Copy path copies its folder
        var group = vm.SessionGroups.First(g => g.IsProject);
        var label = Within<TextBlock>(Within<Grid>(list, g => g.Classes.Contains("group-header") && g.DataContext == group), t => t.Classes.Contains("group-label"));
        await RightClick(w, label);
        menu = OpenMenu(label);
        await Choose(((Control)((Flyout)menu!).Content!).GetLogicalDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Copy path"));
        Assert.Equal(group.Cwd, copied[^1]);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Files_rows_results_and_code_open_their_menu_for_what_was_clicked()
    {
        var root = FileTestRepo.Create(git: false);
        var (w, vm) = await OpenAsync("normal", root);
        var copied = new List<string>();
        vm.CopyTextRequested += copied.Add;
        var f = await FileTestRepo.Shown(vm, git: false);
        await Settle(300);
        var tree = Within<ListBox>(w, l => l.Name == "Tree");

        // The row clicked, not the one selected
        f.SelectedNode = FileTestRepo.Node(f, "src");
        var readme = (Control)tree.ContainerFromItem(FileTestRepo.Node(f, "README.md"))!;
        await RightClick(w, readme);
        var menu = OpenMenu(readme);
        Assert.Same(readme, ((PopupFlyoutBase)menu!).Target);
        Save(w, "file-menu-" + Avalonia.Application.Current!.ActualThemeVariant);
        await Choose(Item(menu, "Copy relative path"));
        Assert.False(menu.IsOpen);
        Assert.Equal("README.md", copied[^1]);

        if (OperatingSystem.IsMacOS())
        {
            var docs = (Control)tree.ContainerFromItem(FileTestRepo.Node(f, "docs"))!;
            await ControlClick(w, docs);
            await Choose(Item(OpenMenu(docs)!, "Copy relative path"));
            Assert.Equal("docs", copied[^1]);
            Assert.False(FileTestRepo.Node(f, "docs").IsExpanded);
        }

        // Below the rows there is nothing to act on: no menu
        var last = (Control)tree.ContainerFromIndex(f.Nodes.Count - 1)!;
        var below = last.TranslatePoint(new Point(last.Bounds.Width / 2, last.Bounds.Height + 30), w)!.Value;
        Assert.True(below.Y < tree.TranslatePoint(new Point(0, tree.Bounds.Height), w)!.Value.Y);
        w.MouseDown(below, MouseButton.Right);
        w.MouseUp(below, MouseButton.Right);
        await Settle();
        Assert.DoesNotContain(tree.GetVisualDescendants().OfType<Control>().Prepend(tree), c => c.ContextFlyout is { IsOpen: true });

        // "Go to file" results have the same menu
        f.Filter = "guide";
        await Until(() => f.Results.Count > 0 && !f.IsSearching, "a match");
        await Settle();
        var results = Within<ListBox>(w, l => l.Name == "Results");
        var match = (Control)results.ContainerFromIndex(0)!;
        await RightClick(w, match);
        await Choose(Item(OpenMenu(match)!, "Copy relative path"));
        Assert.Equal("docs/guide.md", copied[^1]);
        f.Filter = "";

        // The code viewer: Copy when something is selected, and Select all
        await f.OpenFileAsync(Path.Combine(root, "src", "app.py"), line: 1);
        await Settle(400);
        var surface = w.GetVisualDescendants().OfType<CodeView>().Single().Surface;
        await RightClick(w, surface);
        menu = OpenMenu(surface);
        Assert.False(Item(menu!, "Copy").IsEnabled);
        await Choose(Item(menu!, "Select all"));
        Assert.StartsWith("def step_1(x)", surface.SelectedText);
        Assert.Contains("# step 40", surface.SelectedText);
        await RightClick(w, surface);
        Assert.True(Item(OpenMenu(surface)!, "Copy").IsEnabled);
        OpenMenu(surface)!.Hide();
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Code_blocks_and_links_copy_and_open_what_was_clicked(string theme)
    {
        MainViewModel.ApplyTheme(theme);
        var block = new Border { Width = 200, Height = 60, Background = Avalonia.Media.Brushes.Gray, ContextFlyout = ContextMenus.ForCodeBlock(() => "print('hi')") };
        var link = new Border { Width = 200, Height = 30, Background = Avalonia.Media.Brushes.Gray, ContextFlyout = ContextMenus.ForLink("https://example.com/docs") };
        var w = new Window { Width = 600, Height = 400, Content = new StackPanel { Spacing = 20, Margin = new Thickness(20), Children = { block, link } } };
        w.Bind(Window.BackgroundProperty, w.GetResourceObservable("GuiWindow"));
        w.Show();
        try
        {
            await Settle();
            await RightClick(w, block);
            var menu = OpenMenu(block)!;
            Save(w, "code-menu-" + theme);
            await Choose(Item(menu, "Copy code"));
            Assert.False(menu.IsOpen);
            Assert.Equal("print('hi')", await w.Clipboard!.TryGetTextAsync());

            await RightClick(w, link);
            menu = OpenMenu(link)!;
            Assert.True(Item(menu, "Open link").IsEnabled);
            Save(w, "link-menu-" + theme);
            await Choose(Item(menu, "Copy link"));
            Assert.Equal("https://example.com/docs", await w.Clipboard!.TryGetTextAsync());
        }
        finally
        {
            w.Close();
            MainViewModel.ApplyTheme("light");
        }
    }
}
