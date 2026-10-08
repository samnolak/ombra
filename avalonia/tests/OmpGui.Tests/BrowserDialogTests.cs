using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App.Controls;
using OmpGui.App.ViewModels;
using OmpGui.ClientCore.Browser;

namespace OmpGui.Tests;

/// <summary>
/// The browser pane's dialog broker (ClientCore/Browser/DialogBroker) and its cards (BrowserDialogsViewModel,
/// PreviewPanel): one card at a time in order, each question answered exactly once, every cancel path, the agent's
/// questions kept from the user, the keyboard, and screenshots in both themes.
/// </summary>
public sealed class BrowserDialogTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("OMPGUI_REVIEW_DIR");
    private static readonly Uri Studio = new("https://studio.example.com/");
    private static readonly Uri Local = new("http://localhost:3000/app");

    // ── The broker ──

    [Fact]
    public async Task Questions_are_shown_one_at_a_time_in_the_order_they_came()
    {
        using var broker = new DialogBroker();
        var changes = 0;
        broker.Changed += (_, _) => changes++;
        var first = broker.ConfirmAsync(new ConfirmRequest("Delete?") { PageUrl = Local }, CancellationToken.None);
        var second = broker.PromptAsync(new PromptRequest("Name?", "Ann") { PageUrl = Local }, CancellationToken.None);
        var third = broker.AlertAsync(new AlertRequest("Saved") { PageUrl = Local }, CancellationToken.None);

        Assert.Equal(3, changes);
        Assert.Equal("Delete?", broker.Current!.Message);
        Assert.Equal(2, broker.QueuedBehindCurrent);
        Assert.True(broker.Current.Complete(true));
        Assert.True(await first);
        Assert.Equal("Name?", broker.Current!.Message);
        Assert.Equal(1, broker.QueuedBehindCurrent);
        broker.Current.Complete("Bob");
        Assert.Equal("Bob", await second);
        Assert.Equal(BrowserDialogKind.Alert, broker.Current!.Kind);
        broker.Current.Complete(null);
        await third;
        Assert.Null(broker.Current);
        Assert.Empty(broker.Pending);
    }

    [Fact]
    public async Task A_question_is_answered_exactly_once()
    {
        using var broker = new DialogBroker();
        var task = broker.ConfirmAsync(new ConfirmRequest("Sure?"), CancellationToken.None);
        var dialog = broker.Current!;
        Assert.True(dialog.Complete(true));
        Assert.False(dialog.Complete(false));
        Assert.False(dialog.Cancel());
        broker.CancelAll();
        Assert.True(await task);
        Assert.True(dialog.IsCompleted);

        // Many threads racing to answer: one wins, the rest change nothing
        var race = broker.PromptAsync(new PromptRequest("Who?", null), CancellationToken.None);
        var raced = broker.Current!;
        var wins = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => i % 3 == 0 ? raced.Cancel() : raced.Complete($"t{i}"))));
        Assert.Single(wins, w => w);
        var answer = await race;
        Assert.True(answer is null || answer.StartsWith('t'));
    }

    [Fact]
    public void The_wrong_kind_of_answer_is_refused()
    {
        using var broker = new DialogBroker();
        _ = broker.ConfirmAsync(new ConfirmRequest("Sure?"), CancellationToken.None);
        Assert.Throws<ArgumentException>(() => broker.Current!.Complete("yes"));
        Assert.False(broker.Current!.IsCompleted);
        _ = broker.PermissionAsync(new PermissionRequest(PermissionKind.Camera, "https://meet.example"), CancellationToken.None);
        broker.Current.Complete(false);
        Assert.Throws<ArgumentException>(() => broker.Current!.Complete(true));
    }

    [Fact]
    public async Task Cancelling_gives_each_kind_the_answer_the_engine_gives_when_nobody_answers()
    {
        using var broker = new DialogBroker();
        using var cts = new CancellationTokenSource();
        var creds = broker.RequestCredentialsAsync(new CredentialRequest("studio.example.com", 443, "Studio", false, false), cts.Token);
        var alert = broker.AlertAsync(new AlertRequest("Hi"), cts.Token);
        var confirm = broker.ConfirmAsync(new ConfirmRequest("Sure?"), cts.Token);
        var prompt = broker.PromptAsync(new PromptRequest("Name?", "Ann"), cts.Token);
        var leave = broker.BeforeUnloadAsync(new BeforeUnloadRequest(null), cts.Token);
        var files = broker.ChooseFilesAsync(new FileChooserRequest(true, [".png"]), cts.Token);
        var download = broker.DownloadAsync(new DownloadRequest("report.pdf", 2048, null), cts.Token);
        var permission = broker.PermissionAsync(new PermissionRequest(PermissionKind.Microphone, "https://meet.example"), cts.Token);
        var popup = broker.PopupNoticeAsync(new PopupNoticeRequest(new Uri("https://accounts.example/")), cts.Token);
        Assert.Equal(9, broker.Pending.Count);

        cts.Cancel();
        Assert.Null(await creds);
        await alert;
        Assert.False(await confirm);
        Assert.Null(await prompt);
        Assert.True(await leave);
        Assert.Null(await files);
        Assert.Equal(DownloadAnswer.Cancel, await download);
        Assert.Equal(PermissionAnswer.Deny, await permission);
        Assert.False(await popup);
        Assert.Empty(broker.Pending);
        Assert.Null(broker.Current);
    }

    [Fact]
    public async Task Navigation_page_close_and_a_cancelled_token_close_the_questions()
    {
        // Navigation away (or a crash): CancelAll
        using var broker = new DialogBroker();
        var a = broker.ConfirmAsync(new ConfirmRequest("1"), CancellationToken.None);
        var b = broker.RequestCredentialsAsync(new CredentialRequest("h", 443, null, false, false), CancellationToken.None);
        broker.CancelAll();
        Assert.False(await a);
        Assert.Null(await b);

        // A token already cancelled: answered at once, nothing shown
        var changes = 0;
        broker.Changed += (_, _) => changes++;
        Assert.False(await broker.ConfirmAsync(new ConfirmRequest("late"), new CancellationToken(true)));
        Assert.Equal(0, changes);

        // The page closed: pending ones cancelled, later ones never shown
        var pending = broker.PromptAsync(new PromptRequest("x", "y"), CancellationToken.None);
        broker.Dispose();
        Assert.Null(await pending);
        Assert.Equal(PermissionAnswer.Deny, await broker.PermissionAsync(new PermissionRequest(PermissionKind.Geolocation, "https://a.example"), CancellationToken.None));
        Assert.Null(broker.Current);
    }

    [Fact]
    public async Task What_the_agent_caused_waits_for_the_agent_but_sign_in_is_always_the_users()
    {
        using var broker = new DialogBroker();
        var alert = broker.AlertAsync(new AlertRequest("From the agent's click") { CausedByAgent = true }, CancellationToken.None);
        Assert.Null(broker.Current); // no card for the user
        Assert.Equal("alert", broker.CurrentModal!.KindName);
        Assert.Equal("From the agent's click", broker.CurrentModal.Message);
        Assert.Single(broker.AgentPending);

        var creds = broker.RequestCredentialsAsync(new CredentialRequest("studio.example.com", 443, "Studio", false, false) { CausedByAgent = true }, CancellationToken.None);
        Assert.Equal(BrowserDialogKind.Credentials, broker.Current!.Kind);
        Assert.Single(broker.AgentPending);

        Assert.True(broker.TryAnswerModal(null));
        await alert;
        Assert.Null(broker.CurrentModal);

        // A dialog the user caused also blocks the page's scripts: the agent sees it as modal state too
        var confirm = broker.ConfirmAsync(new ConfirmRequest("User's confirm"), CancellationToken.None);
        Assert.Equal("User's confirm", broker.CurrentModal!.Message);
        Assert.False(broker.CurrentModal.RoutedToAgent);

        // The agent hands one it can't answer to the user: it joins the user's line
        var popup = broker.PopupNoticeAsync(new PopupNoticeRequest(new Uri("https://x.example")) { CausedByAgent = true }, CancellationToken.None);
        var agentPopup = broker.AgentPending.Single();
        Assert.True(broker.HandToUser(agentPopup));
        Assert.False(broker.HandToUser(agentPopup));
        Assert.Equal(2, broker.QueuedBehindCurrent);
        broker.CancelAll();
        Assert.Null(await creds);
        Assert.False(await confirm);
        Assert.False(await popup);
    }

    [AvaloniaFact]
    public async Task Navigating_the_pane_closes_the_questions_of_the_page_left()
    {
        var vm = new PreviewViewModel();
        var confirm = vm.Dialogs.Broker.ConfirmAsync(new ConfirmRequest("Stay?"), CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Dialogs.IsOpen);
        vm.NavigateCommand.Execute("localhost:5173");
        Assert.False(await confirm);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.Dialogs.IsOpen);

        var leave = vm.Dialogs.Broker.BeforeUnloadAsync(new BeforeUnloadRequest(null), CancellationToken.None);
        vm.ReloadCommand.Execute(null);
        Assert.True(await leave);
    }

    // ── The model: the page's picture, the web view hidden ──

    [AvaloniaFact]
    public async Task While_a_card_is_open_the_web_view_is_hidden_behind_the_pages_picture()
    {
        var vm = new PreviewViewModel { CurrentUrl = Local };
        var captured = 0;
        vm.Dialogs.CaptureSnapshot = _ =>
        {
            captured++;
            return Task.FromResult<Bitmap?>(FakePage(400, 300));
        };
        Assert.True(vm.ShowWebView);
        var task = vm.Dialogs.Broker.ConfirmAsync(new ConfirmRequest("Delete 3 files?") { PageUrl = Local }, CancellationToken.None);
        await Settle();
        Assert.Equal(1, captured);
        Assert.True(vm.Dialogs.IsOpen);
        Assert.True(vm.Dialogs.CoversPage);
        Assert.True(vm.Dialogs.HasSnapshot);
        Assert.False(vm.ShowWebView);

        // The next card in line keeps the same picture (the page did not change underneath)
        _ = vm.Dialogs.Broker.AlertAsync(new AlertRequest("Done") { PageUrl = Local }, CancellationToken.None);
        await Settle();
        Assert.Equal("1 more", vm.Dialogs.Card!.QueueText);
        vm.Dialogs.Card.PrimaryCommand.Execute(null);
        Assert.True(await task);
        await Settle();
        Assert.Equal(1, captured);
        Assert.Equal(BrowserDialogKind.Alert, vm.Dialogs.Card!.Kind);
        Assert.False(vm.Dialogs.Card.HasQueue);

        vm.Dialogs.Card.PrimaryCommand.Execute(null);
        await Settle();
        Assert.False(vm.Dialogs.IsOpen);
        Assert.False(vm.Dialogs.CoversPage);
        Assert.Null(vm.Dialogs.Snapshot);
        Assert.True(vm.ShowWebView);
    }

    [AvaloniaFact]
    public async Task A_picture_that_fails_or_never_comes_leaves_the_plain_surface()
    {
        var vm = new PreviewViewModel { CurrentUrl = Local };
        vm.Dialogs.CaptureSnapshot = _ => Task.FromException<Bitmap?>(new InvalidOperationException("engine refused"));
        _ = vm.Dialogs.Broker.AlertAsync(new AlertRequest("a"), CancellationToken.None);
        await Settle();
        Assert.True(vm.Dialogs.CoversPage);
        Assert.False(vm.Dialogs.HasSnapshot);
        vm.Dialogs.Broker.CancelAll();
        await Settle();

        vm.Dialogs.CaptureSnapshot = _ => new TaskCompletionSource<Bitmap?>().Task; // the engine never answers
        _ = vm.Dialogs.Broker.AlertAsync(new AlertRequest("b"), CancellationToken.None);
        await Settle(BrowserDialogsViewModel.SnapshotTimeout.TotalMilliseconds + 300);
        Assert.True(vm.Dialogs.CoversPage);
        Assert.False(vm.Dialogs.HasSnapshot);
    }

    [AvaloniaTheory]
    [InlineData(false, false, "studio.example.com wants you to sign in", null, null)]
    [InlineData(true, false, "studio.example.com wants you to sign in", "That username or password didn't work", null)]
    [InlineData(false, true, "studio.example.com:8080 wants you to sign in", null, "This site doesn't use a secure connection.")]
    public void The_sign_in_card_says_who_asks_and_why(bool failedBefore, bool http, string title, string? error, string? warning)
    {
        using var vm = new BrowserDialogsViewModel();
        var page = http ? new Uri("http://studio.example.com:8080/") : Studio;
        _ = vm.Broker.RequestCredentialsAsync(new CredentialRequest("studio.example.com", page.Port, "Studio", false, failedBefore, "ada") { PageUrl = page }, CancellationToken.None);
        var card = new BrowserDialogCardViewModel(vm.Broker.Current!, vm);
        Assert.Equal(title, card.Title);
        Assert.Equal("Realm: Studio", card.Detail);
        Assert.Equal(error, card.Error);
        Assert.Equal(warning, card.Warning);
        Assert.Equal("ada", card.UserName);
        Assert.Equal("Password", card.FirstFocus); // the user name is filled in already
        Assert.Equal(("Sign in", "Cancel"), (card.PrimaryLabel, card.SecondaryLabel));
    }

    [AvaloniaFact]
    public void Cards_word_each_question_in_sentence_case()
    {
        using var vm = new BrowserDialogsViewModel();
        BrowserDialogCardViewModel Card(BrowserDialogRequest r)
        {
            vm.Broker.CancelAll();
            switch (r)
            {
                case FileChooserRequest f: _ = vm.Broker.ChooseFilesAsync(f, CancellationToken.None); break;
                case DownloadRequest d: _ = vm.Broker.DownloadAsync(d, CancellationToken.None); break;
                case PermissionRequest p: _ = vm.Broker.PermissionAsync(p, CancellationToken.None); break;
                case PopupNoticeRequest p: _ = vm.Broker.PopupNoticeAsync(p, CancellationToken.None); break;
                case BeforeUnloadRequest b: _ = vm.Broker.BeforeUnloadAsync(b, CancellationToken.None); break;
                case AlertRequest a: _ = vm.Broker.AlertAsync(a, CancellationToken.None); break;
            }
            return new BrowserDialogCardViewModel(vm.Broker.Current!, vm);
        }

        var alert = Card(new AlertRequest("Saved") { PageUrl = Local });
        Assert.Equal("localhost:3000 says", alert.Title);
        Assert.False(alert.HasSecondary);
        var leave = Card(new BeforeUnloadRequest("page text is never shown") { PageUrl = Local });
        Assert.Equal(("Leave this page?", "Changes you made may not be saved.", "Leave", "Stay"), (leave.Title, leave.Message, leave.PrimaryLabel, leave.SecondaryLabel));
        var file = Card(new FileChooserRequest(true, [".png", "image/jpeg"]) { PageUrl = Local });
        Assert.Equal(("localhost:3000 wants you to choose files", "Accepted types: .png, image/jpeg", "Choose files…"), (file.Title, file.Detail, file.PrimaryLabel));
        var download = Card(new DownloadRequest("report.pdf", 1536 * 1024, new Uri("https://files.example/r.pdf")) { PageUrl = Local });
        Assert.Equal(("Download report.pdf?", "Save", "Save as…", "Cancel"), (download.Title, download.PrimaryLabel, download.TertiaryLabel, download.SecondaryLabel));
        Assert.StartsWith("1.5 MB from files.example", download.Detail!.Replace(',', '.'));
        var cam = Card(new PermissionRequest(PermissionKind.CameraAndMicrophone, "https://meet.example"));
        Assert.Equal(("meet.example wants to use your camera and microphone", "Allow once", "Always allow on this site", "Don't allow"),
            (cam.Title, cam.PrimaryLabel, cam.TertiaryLabel, cam.SecondaryLabel));
        var popup = Card(new PopupNoticeRequest(new Uri("https://accounts.example/o/auth")) { PageUrl = Studio });
        Assert.Equal(("studio.example.com wants to open a new window", "Open", "Don't open"), (popup.Title, popup.PrimaryLabel, popup.SecondaryLabel));
    }

    [AvaloniaFact]
    public async Task Each_button_gives_its_answer()
    {
        using var vm = new BrowserDialogsViewModel();
        var download = vm.Broker.DownloadAsync(new DownloadRequest("a.zip", null, null), CancellationToken.None);
        new BrowserDialogCardViewModel(vm.Broker.Current!, vm).TertiaryCommand.Execute(null);
        Assert.Equal(DownloadAnswer.SaveAs, await download);

        var perm = vm.Broker.PermissionAsync(new PermissionRequest(PermissionKind.Camera, "https://m.example"), CancellationToken.None);
        new BrowserDialogCardViewModel(vm.Broker.Current!, vm).TertiaryCommand.Execute(null);
        Assert.Equal(PermissionAnswer.AlwaysForSite, await perm);

        var popup = vm.Broker.PopupNoticeAsync(new PopupNoticeRequest(null), CancellationToken.None);
        new BrowserDialogCardViewModel(vm.Broker.Current!, vm).SecondaryCommand.Execute(null);
        Assert.False(await popup);

        // Esc on beforeunload stays on the page (not the engine's leave default)
        var leave = vm.Broker.BeforeUnloadAsync(new BeforeUnloadRequest(null), CancellationToken.None);
        new BrowserDialogCardViewModel(vm.Broker.Current!, vm).CancelCommand.Execute(null);
        Assert.False(await leave);

        // Files: the picker's choice; nothing chosen keeps the card
        var picks = new Queue<IReadOnlyList<string>?>([null, ["/tmp/a.png", "/tmp/b.png"]]);
        vm.PickFiles = _ => Task.FromResult(picks.Dequeue());
        var files = vm.Broker.ChooseFilesAsync(new FileChooserRequest(true, []), CancellationToken.None);
        var card = new BrowserDialogCardViewModel(vm.Broker.Current!, vm);
        await card.PrimaryCommand.ExecuteAsync(null);
        Assert.False(card.Dialog.IsCompleted);
        await card.PrimaryCommand.ExecuteAsync(null);
        Assert.Equal(["/tmp/a.png", "/tmp/b.png"], await files);
    }

    // ── The panel: keyboard and pictures ──

    private static (Window w, PreviewPanel panel, PreviewViewModel vm) Show(double width, double height)
    {
        var vm = new PreviewViewModel();
        var panel = new PreviewPanel { DataContext = vm };
        var w = new Window { Content = panel, Width = width, Height = height };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (w, panel, vm);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [AvaloniaFact]
    public async Task The_sign_in_card_takes_the_keyboard_and_Enter_signs_in()
    {
        var (w, panel, vm) = Show(560, 640);
        var task = vm.Dialogs.Broker.RequestCredentialsAsync(new CredentialRequest("studio.example.com", 443, "Studio", false, false) { PageUrl = Studio }, CancellationToken.None);
        await Settle();
        Assert.True(Named<Panel>(panel, "BrowserDialogLayer").IsEffectivelyVisible);
        Assert.Equal("studio.example.com wants you to sign in", Named<SelectableTextBlock>(panel, "BrowserDialogTitle").Text);
        var user = Named<TextBox>(panel, "BrowserDialogUser");
        Assert.True(user.IsFocused);
        w.KeyTextInput("ada");
        w.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<TextBox>(panel, "BrowserDialogPassword").IsFocused);
        w.KeyTextInput("s3cret");
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(new CredentialAnswer("ada", "s3cret"), await task);
        await Settle();
        Assert.False(Named<Panel>(panel, "BrowserDialogLayer").IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Esc_cancels_and_Enter_answers_with_the_main_button()
    {
        var (w, panel, vm) = Show(560, 640);
        var confirm = vm.Dialogs.Broker.ConfirmAsync(new ConfirmRequest("Discard the draft?") { PageUrl = Local }, CancellationToken.None);
        var prompt = vm.Dialogs.Broker.PromptAsync(new PromptRequest("Your name?", "Ann") { PageUrl = Local }, CancellationToken.None);
        await Settle();
        Assert.True(Named<Button>(panel, "BrowserDialogPrimary").IsFocused);
        Assert.Equal("1 more", vm.Dialogs.Card!.QueueText);
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(await confirm);
        await Settle();

        // The next card: the prompt, its box focused with the page's default text
        var box = Named<TextBox>(panel, "BrowserDialogPrompt");
        Assert.True(box.IsFocused);
        Assert.Equal("Ann", box.Text);
        box.SelectAll();
        w.KeyTextInput("Bob");
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal("Bob", await prompt);

        var alert = vm.Dialogs.Broker.AlertAsync(new AlertRequest("Saved") { PageUrl = Local }, CancellationToken.None);
        await Settle();
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await alert;
        w.Close();
    }

    public static IEnumerable<object[]> Shots()
    {
        foreach (var theme in new[] { "light", "dark" })
            foreach (var width in new[] { 560.0, 1180.0 })
                yield return [theme, width];
    }

    [AvaloniaTheory]
    [MemberData(nameof(Shots))]
    public async Task Cards_render_over_the_frozen_page_in_both_themes(string theme, double width)
    {
        MainViewModel.ApplyTheme(theme);
        try
        {
            var (w, panel, vm) = Show(width, 720);
            vm.OnNavigationStarted(Studio);
            vm.OnNavigationCompleted(Studio, true, false, false);
            vm.Dialogs.CaptureSnapshot = _ => Task.FromResult<Bitmap?>(FakePage((int)width, 670));
            BrowserDialogRequest[] requests =
            [
                new CredentialRequest("studio.example.com", 80, "Studio", false, true) { PageUrl = new Uri("http://studio.example.com/") },
                new ConfirmRequest("Discard the changes to “Landing page”? This can't be undone.") { PageUrl = Local },
                new PermissionRequest(PermissionKind.CameraAndMicrophone, "https://meet.example"),
                new DownloadRequest("quarterly-report.pdf", 3_400_000, new Uri("https://files.example/q.pdf")) { PageUrl = Local },
                new FileChooserRequest(true, [".png", ".jpg"]) { PageUrl = Local },
            ];
            foreach (var r in requests)
            {
                _ = r switch
                {
                    CredentialRequest c => vm.Dialogs.Broker.RequestCredentialsAsync(c, CancellationToken.None),
                    ConfirmRequest c => vm.Dialogs.Broker.ConfirmAsync(c, CancellationToken.None),
                    PermissionRequest p => vm.Dialogs.Broker.PermissionAsync(p, CancellationToken.None),
                    DownloadRequest d => vm.Dialogs.Broker.DownloadAsync(d, CancellationToken.None),
                    FileChooserRequest f => (Task)vm.Dialogs.Broker.ChooseFilesAsync(f, CancellationToken.None),
                    _ => Task.CompletedTask,
                };
                await Settle();
                Assert.True(vm.Dialogs.HasSnapshot);
                Assert.True(Named<Image>(panel, "BrowserDialogSnapshot").IsEffectivelyVisible);
                // The main answer takes the keyboard, in the row or (three answers) at the top of the stack
                if (r is not CredentialRequest)
                    Assert.True(Named<Button>(panel, r is PermissionRequest or DownloadRequest ? "BrowserDialogPrimaryStacked" : "BrowserDialogPrimary").IsFocused);
                var card = Named<Border>(panel, "BrowserDialogCard");
                Assert.True(card.Bounds.Width <= 400 + 0.5, $"card {card.Bounds.Width} wide");
                Assert.True(card.Bounds.Width >= Math.Min(400, width - 32) - 0.5);
                using (var frame = w.CaptureRenderedFrame())
                {
                    Assert.NotNull(frame);
                    if (Dir is not null)
                    {
                        Directory.CreateDirectory(Dir);
                        using var f = File.Create(Path.Combine(Dir, $"{theme}-{width}-{r.Kind.ToString().ToLowerInvariant()}.png"));
                        frame!.Save(f, new PngBitmapEncoderOptions());
                    }
                }
                vm.Dialogs.Card!.CancelCommand.Execute(null);
                await Settle();
            }
            w.Close();
        }
        finally
        {
            MainViewModel.ApplyTheme("light");
        }
    }

    /// <summary>A stand-in for the page's picture: a header bar and some text blocks.</summary>
    private static Bitmap FakePage(int width, int height)
    {
        var page = new Border
        {
            Width = width,
            Height = height,
            Background = Brushes.White,
            Child = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new Border { Height = 56, Background = new SolidColorBrush(Color.Parse("#1E3A8A")) },
                    new TextBlock { Text = "Studio dashboard", FontSize = 28, Margin = new Thickness(24, 8), Foreground = Brushes.Black },
                    new Border { Height = 120, Margin = new Thickness(24, 0), Background = new SolidColorBrush(Color.Parse("#E5E7EB")), CornerRadius = new CornerRadius(8) },
                    new Border { Height = 120, Margin = new Thickness(24, 0), Background = new SolidColorBrush(Color.Parse("#DBEAFE")), CornerRadius = new CornerRadius(8) },
                },
            },
        };
        page.Measure(new Size(width, height));
        page.Arrange(new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        bitmap.Render(page);
        return bitmap;
    }

    private static async Task Settle(double ms = 120)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        } while (DateTime.UtcNow < until);
        Dispatcher.UIThread.RunJobs();
    }
}
