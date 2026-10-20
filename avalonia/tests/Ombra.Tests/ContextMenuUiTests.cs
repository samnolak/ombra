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
using Ombra.App;
using Ombra.App.Controls;
using Ombra.App.Localization;
using Ombra.App.Services.Documents;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>
/// Right-click menus as the user opens them: a right click, and on macOS Control+click, on each surface (messages, a
/// reply, text fields, the sidebar's sessions and projects, the Files pane's rows and code, code blocks and links). Each
/// menu acts on the thing clicked, not on whatever is selected. With OMBRA_REVIEW_DIR set, an open menu is saved in
/// both themes to its "menus" folder.
/// </summary>
public sealed class ContextMenuUiTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("OMBRA_REVIEW_DIR");

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

    /// <summary>The row named by its English key, as the menu shows it in the current language.</summary>
    private static Button Item(FlyoutBase menu, string name) =>
        ((Control)((Flyout)menu).Content!).GetLogicalDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == Loc.T(name));

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
        vm.CopyTextRequested += text => { copied.Add(text); return Task.FromResult(true); };
        var replies = new List<string>();
        void CopyReply(AssistantRowViewModel _, string text) => replies.Add(text);
        AssistantRowViewModel.CopyRequested += CopyReply;
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
            Assert.Same(link.GetVisualParent(), ((PopupFlyoutBase)menu!).Target); // the text the link sits in (MarkdownView.InlineLink.ShowMenu)
            await Choose(Item(menu, "Copy link"));
            Assert.Equal("https://example.com/docs", await w.Clipboard!.TryGetTextAsync());

            // The message box: Cut, Copy, Paste and Select all, which selects the box's text
            var composer = w.FindControl<TextBox>("Composer")!;
            composer.Text = "draft words";
            await RightClick(w, composer);
            var textMenu = Assert.IsType<MenuFlyout>(OpenMenu(composer));
            Save(w, "text-field-menu-" + Avalonia.Application.Current!.ActualThemeVariant);
            var items = textMenu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(new[] { "Cut", "Copy", "Paste", "Select all" }.Select(Loc.T), items.Select(i => (string)i.Header!));
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
            AssistantRowViewModel.CopyRequested -= CopyReply;
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task Sidebar_sessions_and_projects_open_their_menu_even_while_sessions_cannot_change()
    {
        var (w, vm) = await OpenAsync("normal");
        var copied = new List<string>();
        vm.CopyTextRequested += text => { copied.Add(text); return Task.FromResult(true); };
        await SendAsync(w, vm, "first topic");
        await Until(() => vm.Sessions.Any(x => x.Title == "first topic"), "first listed");
        await vm.NewChatCommand.ExecuteAsync(null);
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
        await Choose(Item(menu!, "Copy path"));
        Assert.Equal(group.Cwd, copied[^1]);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Files_rows_results_and_code_open_their_menu_for_what_was_clicked()
    {
        var root = FileTestRepo.Create(git: false);
        var (w, vm) = await OpenAsync("normal", root);
        var copied = new List<string>();
        vm.CopyTextRequested += text => { copied.Add(text); return Task.FromResult(true); };
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

        // The visible editor: an empty selection cannot copy; Select all includes the entire original document.
        var path = Path.Combine(root, "src", "app.py");
        var original = await File.ReadAllTextAsync(path);
        await f.OpenFileAsync(path, line: 1);
        await Settle(400);
        var editor = w.GetVisualDescendants().OfType<DocumentEditor>().Single(e => e.IsEffectivelyVisible).Editor;
        var surface = editor.TextArea.TextView;
        await RightClick(w, surface);
        menu = OpenMenu(surface);
        Assert.NotNull(menu);
        Assert.False(Item(menu, "Copy").IsEnabled);
        await Choose(Item(menu, "Select all"));
        Assert.False(menu.IsOpen);
        Assert.Equal(original, editor.SelectedText);
        Assert.Same(editor.TextArea, w.FocusManager!.GetFocusedElement());
        await RightClick(w, surface);
        menu = OpenMenu(surface);
        Assert.NotNull(menu);
        Assert.True(Item(menu, "Copy").IsEnabled);
        Assert.Equal(original, editor.SelectedText);
        await Choose(Item(menu, "Copy"));
        Assert.Equal(original.ReplaceLineEndings("\n"), (await w.Clipboard!.TryGetTextAsync())?.ReplaceLineEndings("\n"));
        Assert.Equal(original, editor.Text);

        if (OperatingSystem.IsMacOS())
        {
            var caret = editor.CaretOffset;
            await ControlClick(w, surface);
            menu = OpenMenu(surface);
            Assert.NotNull(menu);
            Assert.Equal(original, editor.SelectedText);
            Assert.Equal(caret, editor.CaretOffset);
            Assert.True(Item(menu, "Copy").IsEnabled);
            await Choose(Item(menu, "Copy"));
            Assert.Equal(original.ReplaceLineEndings("\n"), (await w.Clipboard!.TryGetTextAsync())?.ReplaceLineEndings("\n"));
            Assert.Same(editor.TextArea, w.FocusManager!.GetFocusedElement());
        }
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Editor_menu_preserves_readonly_and_clipboard_edit_boundaries(bool readOnly)
    {
        var path = Path.Combine(TestProcesses.TempDir("editor-menu"), "text.txt");
        const string original = "alpha\nbeta\n";
        await File.WriteAllTextAsync(path, original);
        var attributes = File.GetAttributes(path);
        if (readOnly) File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
        await using var documents = new WorkspaceDocumentService(watchFiles: false);
        var w = new Window { Width = 720, Height = 480 };
        try
        {
            var document = await documents.OpenAsync(path);
            var view = new DocumentEditor { WorkspaceDocument = document };
            var editor = view.Editor;
            var surface = editor.TextArea.TextView;
            w.Content = view;
            w.Show();
            await Settle();
            await RightClick(w, surface);
            var menu = OpenMenu(surface);
            Assert.NotNull(menu);
            Assert.False(Item(menu, "Copy").IsEnabled);
            Assert.False(Item(menu, "Cut").IsEnabled);
            Assert.Equal(!readOnly, Item(menu, "Paste").IsEnabled);
            await Choose(Item(menu, "Select all"));
            Assert.Equal(original, editor.SelectedText);
            await RightClick(w, surface);
            menu = OpenMenu(surface)!;
            Assert.True(Item(menu, "Copy").IsEnabled);
            Assert.Equal(!readOnly, Item(menu, "Cut").IsEnabled);
            Assert.Equal(!readOnly, Item(menu, "Paste").IsEnabled);
            await Choose(Item(menu, "Copy"));
            Assert.Equal(original, (await w.Clipboard!.TryGetTextAsync())?.ReplaceLineEndings("\n"));
            Assert.Same(editor.TextArea, w.FocusManager!.GetFocusedElement());
            if (readOnly)
            {
                w.KeyTextInput("blocked");
                Assert.Equal(original, document.Buffer.Text);
                Assert.False(document.IsDirty);
            }
            else
            {
                await RightClick(w, surface);
                await Choose(Item(OpenMenu(surface)!, "Cut"));
                Assert.Equal("", document.Buffer.Text);
                Assert.True(document.IsDirty);
                Assert.Equal(original, (await w.Clipboard!.TryGetTextAsync())?.ReplaceLineEndings("\n"));
                await RightClick(w, surface);
                await Choose(Item(OpenMenu(surface)!, "Paste"));
                Assert.Equal(original, document.Buffer.Text);
                Assert.Same(editor.TextArea, w.FocusManager!.GetFocusedElement());

                // Paste must use the native transaction and reject text the document cannot save.
                await RightClick(w, surface);
                await Choose(Item(OpenMenu(surface)!, "Select all"));
                await w.Clipboard!.SetTextAsync("invalid\0text");
                await RightClick(w, surface);
                await Choose(Item(OpenMenu(surface)!, "Paste"));
                Assert.Equal(original, document.Buffer.Text);
            }
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally
        {
            w.Close();
            File.SetAttributes(path, attributes);
        }
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
            Assert.False(Item(menu, "Insert into message").IsEnabled); // no composer in this standalone window
            await Choose(Item(menu, "Copy code"));
            Assert.False(menu.IsOpen);
            Assert.Equal("print('hi')", await w.Clipboard!.TryGetTextAsync());

            await RightClick(w, link);
            menu = OpenMenu(link)!;
            Assert.True(Item(menu, "Open in default browser").IsEnabled);
            Assert.False(Item(menu, "Open in preview").IsEnabled); // no chat in this standalone window
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

    /// <summary>
    /// A link in a reply, clicked: the right panel's Browser tool, with the chat still on screen, never the system's
    /// browser (the owner's default browser was Nightly, outside Ombra). ⌘-click (Ctrl-click off macOS) opens the link's
    /// menu instead: "Open in default browser" and "Open in Browser page". The headless window has no native surface, so
    /// once the panel tries to show the page its engine is unavailable and the next click goes to the system.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task A_link_in_a_reply_opens_in_the_right_panel_and_cmd_click_offers_the_browser_page_and_the_default_browser(string language)
    {
        var settingsPath = Path.Combine(TestProcesses.TempDir("chat-links-" + language), "ombra.local.json");
        var previous = Loc.Current;
        Loc.Load(language, settingsPath);
        var (w, vm) = await OpenAsync("markdown", height: 1400);
        var system = new List<string>();
        vm.OpenUrlRequested += system.Add;
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        try
        {
            await SendAsync(w, vm, "a link please");
            await Until(() => vm.Rows.OfType<AssistantRowViewModel>().Any(r => r.HasText), "reply");
            await Settle(400);
            HyperlinkButton Link() => w.GetVisualDescendants().OfType<HyperlinkButton>().First(l => l.NavigateUri?.AbsoluteUri == "https://example.com/docs");
            // As in the app: between the press and the release the reply's text lays out again, which takes the link out
            // of the tree and back (TextBlock's arrange) and hands its pointer capture to the text.
            async Task ClickLink(RawInputModifiers modifiers = RawInputModifiers.None)
            {
                var link = Link();
                var p = Center(link, w);
                w.MouseMove(p, modifiers);
                w.MouseDown(p, MouseButton.Left, modifiers);
                var lost = false;
                void Lost(object? s, PointerCaptureLostEventArgs e) => lost = true;
                link.PointerCaptureLost += Lost;
                ((TextBlock)link.GetVisualParent()!).InvalidateMeasure();
                await Settle();
                link.PointerCaptureLost -= Lost;
                Assert.True(lost);
                w.MouseUp(p, MouseButton.Left, modifiers);
                await Settle();
            }

            await ClickLink();
            Assert.Equal(SidePane.Browser, vm.ActivePane);
            Assert.Equal("https://example.com/docs", vm.Preview.CurrentUrl!.AbsoluteUri);
            Assert.False(vm.ShowBrowserPage);
            Assert.True(Link().IsEffectivelyVisible);
            Assert.Empty(system);
            Save(w, "chat-link-in-panel-" + language);

            await ClickLink(command);
            var menu = OpenMenu(Link())!;
            Assert.Same(Link().GetVisualParent(), ((PopupFlyoutBase)menu).Target);
            Save(w, "chat-link-cmd-menu-" + language);
            Assert.Empty(system);
            await Choose(Item(menu, "Open in default browser"));
            Assert.Equal(["https://example.com/docs"], system);

            Assert.True(vm.Preview.IsEngineUnavailable);
            await ClickLink();
            Assert.Equal(2, system.Count);

            await ClickLink(command);
            await Choose(Item(OpenMenu(Link())!, "Open in Browser page"));
            Assert.True(vm.ShowBrowserPage);
            Assert.Equal("https://example.com/docs", vm.Browser.CurrentUrl!.AbsoluteUri);
            Assert.Equal(2, system.Count);
        }
        finally
        {
            w.Close();
            Loc.Load(previous, settingsPath);
        }
    }

    [AvaloniaFact]
    public async Task Queue_context_edit_withdraws_the_clicked_item_not_the_last_item()
    {
        var (w, vm) = await OpenAsync("queue-stream-3");
        try
        {
            vm.ComposerText = "long task";
            vm.SendCommand.Execute(null);
            await Until(() => vm.Phase == SessionPhase.Running, "running");
            foreach (var text in new[] { "edit the first", "keep the second" })
            {
                vm.ComposerText = text;
                await vm.SendCommand.ExecuteAsync(null);
            }
            await Until(() => vm.QueuedMessages.Count == 2, "two queued messages");
            await Settle();
            var first = vm.QueuedMessages[0];
            var target = Within<Grid>(w, c => c.DataContext == first && ConversationMenus.GetKind(c) == ConversationMenuKind.Queue);
            await RightClick(w, target);
            await Choose(Item(OpenMenu(target)!, "Edit"));
            await Until(() => vm.ComposerText == "edit the first" && vm.QueuedMessages.Count == 1, "clicked message recovered");
            Assert.Equal("keep the second", vm.QueuedMessages[0].Text);
        }
        finally { await vm.DisposeAsync(); w.Close(); }
    }

    [AvaloniaFact]
    public async Task An_inactive_window_copies_its_reply_once_and_reports_clipboard_failures()
    {
        var (w, vm) = await OpenAsync("normal");
        var other = new Window { Width = 200, Height = 100 };
        var write = ContextMenus.WriteClipboard;
        try
        {
            await SendAsync(w, vm, "copy this");
            var reply = vm.Rows.OfType<AssistantRowViewModel>().Last();
            other.Show();
            other.Activate();
            var writes = 0;
            ContextMenus.WriteClipboard = (_, _) => { writes++; return Task.CompletedTask; };
            reply.CopyCommand.Execute(null);
            Assert.Equal(1, writes);
            Assert.Equal(Loc.T("Copied"), vm.SessionCard!.Title);
            ContextMenus.WriteClipboard = (_, _) => Task.FromException(new IOException("clipboard unavailable"));
            reply.CopyCommand.Execute(null);
            Assert.Equal(Loc.T("Couldn't copy"), vm.SessionCard!.Title);
            Assert.Equal("clipboard unavailable", vm.SessionCard.Message);
        }
        finally { ContextMenus.WriteClipboard = write; other.Close(); await vm.DisposeAsync(); w.Close(); }
    }
}
