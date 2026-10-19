using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>
/// The approval card's "Don't ask again" (this session / this project / always) and "Deny and say why", and Settings →
/// Permissions, in the real window over the fake omp (scenario approval-rules: one request per prompt line).
/// </summary>
public sealed class ApprovalRulesUiTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMBRA_SHOT_DIR") is { } d
        ? Path.Combine(d, "AlwaysAllow")
        : Path.Combine(AppContext.BaseDirectory, "screenshots", "AlwaysAllow");

    private sealed record Ui(MainWindow W, MainViewModel Vm, SessionController S, ClientSettingsStore Store);

    private static async Task<Ui> OpenAsync(string prompt, double width = 1000, double height = 760)
    {
        var store = new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("approval-ui"), "settings.json"));
        var s = new SessionController(TestProcesses.Fake("approval-rules"));
        var vm = new MainViewModel(s, new AppArgs(), settings: store);
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        await SendAsync(vm, prompt);
        await Until(() => vm.HasDialog, "dialog shown");
        return new Ui(w, vm, s, store);
    }

    private static async Task SendAsync(MainViewModel vm, string prompt)
    {
        vm.ComposerText = prompt;
        await vm.SendCommand.ExecuteAsync(null);
    }

    private static async Task CloseAsync(Ui ui)
    {
        await ui.Vm.DisposeAsync();
        ui.W.Close();
    }

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

    private static void Shot(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Directory.CreateDirectory(ShotDir);
        using var frame = w.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(ShotDir, name + ".png"));
        frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static T Find<T>(Window w, Func<T, bool>? where = null) where T : Control
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var found = w.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.IsEffectivelyVisible && (where?.Invoke(c) ?? true));
            if (found is not null) return found;
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException($"no visible {typeof(T).Name} matched");
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            Thread.Sleep(10);
        }
    }

    private static Button Named(Window w, string name) => Find<Button>(w, b => b.Name == name);

    private static void Click(Button b)
    {
        Assert.True(b.IsEffectivelyVisible && b.IsEffectivelyEnabled, $"button {b.Name ?? b.Content?.ToString()} not clickable");
        b.Command!.Execute(b.CommandParameter);
    }

    [Theory]
    [InlineData("\u202E", "[U+202E]")]
    [InlineData("\u200B", "[U+200B]")]
    [InlineData("\u00A0", "[U+00A0]")]
    [InlineData("\r", "[U+000D]")]
    [InlineData("\t", "[U+0009]")]
    [InlineData("\n", "[U+000A]")]
    [InlineData("\U000E007F", "[U+E007F]")]
    public async Task Approval_display_exposes_hidden_characters_without_rewriting_the_source(string hidden, string marker)
    {
        var command = "echo visible" + hidden;
        var title = "Allow tool: bash\nReason: review" + hidden + "\nCommand: " + command;
        var model = new PendingDialog("hidden", DialogKind.Approval, title,
            "message" + hidden, [], null, null, DateTimeOffset.UtcNow, null) { Approval = TestApprovals.Request(title) };
        DialogAnswer? answer = null;
        var vm = new DialogViewModel(model, (_, value) => { answer = value; return Task.CompletedTask; },
            new ApprovalActions((_, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask));
        Assert.Equal(command, ApprovalRequest.From(vm.Model)!.Command);
        Assert.Equal(model.Title[(model.Title.IndexOf('\n') + 1)..], vm.SourceDetails);
        Assert.Contains(marker, vm.Preview);
        if (hidden != "\n")
        {
            Assert.Contains(marker, vm.Note);
            Assert.Contains(marker, vm.Message);
        }
        Assert.False(vm.CanAllowWithRule);
        await vm.AllowCommand.ExecuteAsync(null);
        Assert.Equal(new DialogAnswer.Value("Approve"), answer); // the prompt is still answerable explicitly
        Assert.Equal(command, ApprovalRequest.From(vm.Model)!.Command);
    }

    [Theory]
    [InlineData("\u00A0", "[U+00A0]")]
    [InlineData("\t", "[U+0009]")]
    [InlineData("\r", "[U+000D]")]
    public void Approval_headline_boundaries_keep_visible_markers(string hidden, string marker)
    {
        foreach (var headline in new[] { hidden + "Allow tool: bash", "Allow tool: bash" + hidden })
        {
            var model = new PendingDialog("headline", DialogKind.Approval, headline + "\nCommand: echo hello",
                null, [], null, null, DateTimeOffset.UtcNow, null);
            var vm = new DialogViewModel(model, (_, _) => Task.CompletedTask);
            Assert.Contains(marker, vm.Headline);
            Assert.Contains(marker, vm.Title); // the card renders Title, not the raw Headline property
            Assert.Equal(headline + "\nCommand: echo hello", vm.Model.Title);
        }
    }

    [AvaloniaFact]
    public async Task Pointer_approval_requires_a_fresh_press_on_a_settled_card()
    {
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs(),
            settings: new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("approval-pointer"), "settings.json")));
        var w = new MainWindow { DataContext = vm, Width = 1000, Height = 760 };
        var answers = 0;
        DialogViewModel Card(string id) => new(TestApprovals.Dialog("Allow tool: bash\nCommand: echo hello", id),
            (_, _) => { answers++; return Task.CompletedTask; });
        Point AllowPoint()
        {
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            var button = Named(w, "AllowButton");
            return button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), w)!.Value;
        }
        try
        {
            w.Show();
            vm.CurrentDialog = Card("first");
            var point = AllowPoint();
            w.MouseDown(point, MouseButton.Left);
            w.MouseUp(point, MouseButton.Left);
            Assert.Equal(0, answers);
            await Task.Delay(550);
            point = AllowPoint();
            w.MouseDown(point, MouseButton.Left);
            vm.CurrentDialog = Card("replacement");
            point = AllowPoint();
            w.MouseUp(point, MouseButton.Left);
            Assert.Equal(0, answers);
            await Task.Delay(550);
            point = AllowPoint();
            w.MouseDown(point, MouseButton.Left);
            vm.IsShortcutsOpen = true;
            Dispatcher.UIThread.RunJobs();
            w.MouseUp(point, MouseButton.Left);
            Assert.Equal(0, answers);
            vm.IsShortcutsOpen = false;
            await Task.Delay(550);
            point = AllowPoint();
            w.MouseDown(point, MouseButton.Left);
            w.MouseUp(point, MouseButton.Left);
            Assert.Equal(1, answers);
            vm.CurrentDialog = Card("deny-immediately");
            Dispatcher.UIThread.RunJobs();
            Named(w, "DenyButton").Focus();
            w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            w.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Assert.Equal(2, answers);
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("en", Key.A, PhysicalKey.A, "a")]
    [InlineData("de", Key.Z, PhysicalKey.Z, "z")]
    public async Task A_new_or_replaced_card_cannot_consume_approval_keys_in_flight(string language, Key key, PhysicalKey physicalKey, string symbol)
    {
        var settingsPath = Path.Combine(TestProcesses.TempDir("approval-keys"), "settings.json");
        var previousLanguage = Ombra.App.Localization.Loc.Current;
        if (language == "de")
        {
            Directory.CreateDirectory(Ombra.App.Localization.Loc.UserFolder(settingsPath));
            File.WriteAllText(Path.Combine(Ombra.App.Localization.Loc.UserFolder(settingsPath), "de.json"),
                """{ "name": "Deutsch", "strings": { "_Allow": "_Zulassen" } }""");
        }
        Ombra.App.Localization.Loc.Load(language, settingsPath);
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs(),
            settings: new ClientSettingsStore(settingsPath));
        var w = new MainWindow { DataContext = vm, Width = 1000, Height = 760 };
        var answers = 0;
        DialogViewModel Card(string id) => new(TestApprovals.Dialog("Allow tool: bash\nCommand: echo hello", id), (_, _) => { answers++; return Task.CompletedTask; });
        try
        {
            w.Show();
            vm.CurrentDialog = Card("first");
            Dispatcher.UIThread.RunJobs();
            w.Composer.Focus();
            w.KeyPress(Key.D1, RawInputModifiers.None, PhysicalKey.Digit1, "1");
            w.KeyRelease(Key.D1, RawInputModifiers.None, PhysicalKey.Digit1, "1");
            Assert.Equal(0, answers); // even an empty composer is not an approval shortcut target
            w.KeyPress(key, RawInputModifiers.Alt, physicalKey, symbol);
            Assert.Equal(0, answers);
            await Task.Delay(550);
            w.KeyPress(key, RawInputModifiers.Alt, physicalKey, symbol); // held, not a fresh approval
            Assert.Equal(0, answers);
            w.KeyRelease(key, RawInputModifiers.Alt, physicalKey, symbol);
            w.KeyPress(key, RawInputModifiers.Alt, physicalKey, symbol);
            w.KeyRelease(key, RawInputModifiers.Alt, physicalKey, symbol);
            Assert.Equal(1, answers); // deliberate accessible accelerator after the guard

            vm.CurrentDialog = Card("second");
            Dispatcher.UIThread.RunJobs();
            var allow = Named(w, "AllowButton");
            allow.Focus();
            w.KeyPress(Key.D1, RawInputModifiers.None, PhysicalKey.Digit1, "1");
            w.KeyRelease(Key.D1, RawInputModifiers.None, PhysicalKey.Digit1, "1");
            Assert.Equal(1, answers);
            await Task.Delay(550);
            w.KeyPress(Key.D1, RawInputModifiers.None, PhysicalKey.Digit1, "1");
            w.KeyRelease(Key.D1, RawInputModifiers.None, PhysicalKey.Digit1, "1");
            Assert.Equal(2, answers);

            vm.CurrentDialog = Card("third");
            Dispatcher.UIThread.RunJobs();
            Click(Named(w, "AllowButton")); // mouse/explicit button does not inherit a timing restriction
            Assert.Equal(3, answers);

            vm.CurrentDialog = Card("after-activation");
            Dispatcher.UIThread.RunJobs();
            w.Composer.Focus();
            // As with the OS callback used by DialogUiTests, no keydown is delivered while the window is inactive.
            var callbacks = w.PlatformImpl!.GetType().GetProperties(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            ((Action)callbacks.First(p => p.Name.EndsWith("Deactivated", StringComparison.Ordinal) && p.PropertyType == typeof(Action))
                .GetValue(w.PlatformImpl)!).Invoke();
            ((Action)callbacks.First(p => p.Name.EndsWith("Activated", StringComparison.Ordinal) && p.PropertyType == typeof(Action))
                .GetValue(w.PlatformImpl)!).Invoke();
            await Task.Delay(550); // first OS repeat may arrive later than the appearance guard
            w.KeyPress(key, RawInputModifiers.Alt, physicalKey, symbol);
            Assert.Equal(3, answers);
            Assert.NotNull(vm.SessionCard); // existing brief explains the required release, without pinning its copy
            w.KeyRelease(key, RawInputModifiers.Alt, physicalKey, symbol);
            w.KeyPress(key, RawInputModifiers.Alt, physicalKey, symbol);
            w.KeyRelease(key, RawInputModifiers.Alt, physicalKey, symbol);
            Assert.Equal(4, answers);
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
            Ombra.App.Localization.Loc.Load(previousLanguage, settingsPath);
        }
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Dont_ask_again_for_this_project_answers_the_next_matching_request(string theme)
    {
        MainViewModel.ApplyTheme(theme);
        try
        {
            var ui = await OpenAsync("npm test\nnpm test --watch\ngit log");
            var d = ui.Vm.CurrentDialog!;
            Assert.Equal((Loc.T("omp wants to run a command"), "npm test"), (d.Title, d.Preview));
            var npmTest = Loc.F("commands starting with “{0}”", "npm test");
            Assert.Equal(Loc.F("Don't ask again for {0}", npmTest), d.AllowWithRuleText);
            Assert.Equal("bash(npm test:*)", d.RuleText);
            var row = Find<WrapPanel>(ui.W, p => p.Name == "AllowRuleRow");
            Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == d.AllowWithRuleText);
            Assert.False(Named(ui.W, "AllowProjectButton").IsFocused, "the card must not take focus");
            Shot(ui.W, $"card-{theme}");

            Click(Named(ui.W, "AllowProjectButton"));
            // "npm test --watch" is answered by the rule; "git log" is asked
            await Until(() => ui.Vm.CurrentDialog is { Preview: "git log" }, "git log asked");
            Assert.Equal("bash(git log:*)", ui.Vm.CurrentDialog!.RuleText); // each request offers its own rule
            var notices = ui.Vm.Rows.OfType<NoticeRowViewModel>().Select(n => n.Text).ToList();
            Assert.Contains(Loc.F("Approved by you — {0}", "bash"), notices);
            Assert.Contains(Loc.F("Won't ask again for {0} ({1}) — the rules are in Settings → Permissions", npmTest, Loc.T("this project")), notices);
            Assert.Contains(Loc.F("Allowed automatically — {0} ({1})", npmTest, Loc.T("this project")), notices);
            Assert.Equal(2, ui.Vm.Rows.OfType<ToolRowViewModel>().Count(t => t.StatusText == Loc.T("done")));
            // Kept in the client's settings, for this project
            var saved = Assert.Single(ui.Store.Load().ApprovalRules!);
            Assert.Equal("bash(npm test:*)", saved.Rule);
            Assert.True(SessionCatalog.SamePath(ui.S.Snapshot().Cwd, saved.Project));
            Shot(ui.W, $"card-after-rule-{theme}");

            Click(Named(ui.W, "DenyButton"));
            await Until(() => ui.Vm.Phase == SessionPhase.Ready, "run end");

            // Settings → Permissions lists it; Remove takes it out of force and out of the file
            await ui.Vm.OpenSettingsAtAsync("permissions");
            await Until(() => ui.Vm.Permissions.Rules.Count == 1, "rule listed");
            var listed = ui.Vm.Permissions.Rules[0];
            var npmTestLabel = char.ToUpperInvariant(npmTest[0]) + npmTest[1..];
            Assert.Equal((npmTestLabel, Loc.T("This project"), "bash(npm test:*)"), (listed.Description, listed.ScopeLabel, listed.RuleText));
            Find<ItemsControl>(ui.W, c => c.Name == "PermissionRuleList");
            Shot(ui.W, $"permissions-{theme}");
            Click(Find<Button>(ui.W, b => Avalonia.Automation.AutomationProperties.GetName(b) == Loc.F("Remove rule: {0}", npmTestLabel)));
            await Until(() => ui.Vm.Permissions.IsEmpty, "rule removed");
            Assert.Null(ui.Store.Load().ApprovalRules);
            Assert.Equal(Loc.F("Removed. omp asks again for {0}.", npmTest), ui.Vm.Permissions.Message);
            Shot(ui.W, $"permissions-empty-{theme}");
            ui.Vm.CloseSettingsCommand.Execute(null);

            await SendAsync(ui.Vm, "npm test");
            await Until(() => ui.Vm.CurrentDialog is { Preview: "npm test" }, "asked again after removal");
            Click(Named(ui.W, "DenyButton"));
            await Until(() => ui.Vm.Phase == SessionPhase.Ready, "second run end");
            await CloseAsync(ui);
        }
        finally
        {
            MainViewModel.ApplyTheme("light");
        }
    }

    [AvaloniaFact]
    public void Rule_words_and_answer_notices_have_no_english_left_in_russian()
    {
        var settingsPath = Path.Combine(TestProcesses.TempDir("approval-ru"), "settings.json");
        var previous = Loc.Current;
        Loc.Load("ru", settingsPath);
        try
        {
            var project = new ApprovalGrant(ApprovalRule.Parse("bash(npm test:*)")!, ApprovalScope.Project, "/p").Describe();
            var always = new ApprovalGrant(ApprovalRule.Parse("write")!, ApprovalScope.Always).Describe();
            // As ClientCore's SessionController and ConversationState write them
            foreach (var (english, kept) in new[]
            {
                ("Allowed automatically — " + project, "npm test"),
                ($"Won't ask again for {always} — the rules are in Settings → Permissions", ""),
                ("Denied by you — bash: “use git clean”", "use git clean"),
                ("Answered by you: “Yes” — Continue?", "Yes"),
            })
            {
                var shown = AnswerNotices.Localize(english);
                Assert.Contains(kept, shown);
                foreach (var word in (string[])["Allowed", "Won't", "Denied", "Answered", "commands starting", "writing files", "this project", "always", "Settings"])
                    Assert.DoesNotContain(word, shown);
            }
            foreach (var rule in (string[])["bash", "bash(npm test:*)", "bash(npm test)", "write", "edit", "read", "eval", "fetch", "task", "mcp__github__create_issue", "lsp"])
            {
                var english = ApprovalRule.Parse(rule)!.Description;
                Assert.NotEqual(english, AnswerNotices.RuleDescription(english));
            }
        }
        finally
        {
            Loc.Load(previous, settingsPath);
        }
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Deny_and_say_why_sends_the_words_to_omp(string theme)
    {
        MainViewModel.ApplyTheme(theme);
        try
        {
            var ui = await OpenAsync("rm -rf build");
            // 3 opens the box and puts the caret in it; digits typed there are text, not answers
            ui.W.KeyPress(Key.D3, RawInputModifiers.None, PhysicalKey.Digit3, "3");
            ui.W.KeyRelease(Key.D3, RawInputModifiers.None, PhysicalKey.Digit3, "3");
            await Until(() => ui.Vm.CurrentDialog!.IsWritingFeedback, "feedback box open");
            var box = Find<TextBox>(ui.W, t => t.Name == "FeedbackBox");
            await Until(() => box.IsFocused, "feedback box focused");
            Assert.Equal("", box.Text);
            ui.W.KeyTextInput("use git clean -fdx, keep 2 caches");
            Assert.Equal("use git clean -fdx, keep 2 caches", ui.Vm.CurrentDialog!.FeedbackText);
            Assert.False(Named(ui.W, "FeedbackButton").IsEffectivelyEnabled);
            Shot(ui.W, $"card-feedback-{theme}");

            // Esc closes the box, not the request
            ui.W.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await Until(() => !ui.Vm.CurrentDialog!.IsWritingFeedback, "box closed");
            Assert.True(ui.Vm.HasDialog);
            Click(Named(ui.W, "FeedbackButton"));
            await Until(() => box.IsFocused, "focused again");
            ui.W.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            const string reason = "use git clean -fdx, keep 2 caches";
            await Until(() => ui.Vm.Phase == SessionPhase.Ready
                && ui.Vm.Rows.OfType<NoticeRowViewModel>().Any(n => n.Text == Loc.F("Denied by you — {0}: “{1}”", "bash", reason)), "feedback delivered");
            Assert.True(ui.Vm.Rows.OfType<ToolRowViewModel>().Single().IsFailed);
            // The words go with the Deny (extension_ui_response.reason) and the agent reads them in the tool result;
            // nothing is sent as a separate chat message
            Assert.DoesNotContain(ui.Vm.Rows, r => r is UserRowViewModel u && u.Text == reason);
            Assert.Contains("The user said (approval prompt): " + System.Text.Json.JsonSerializer.Serialize(reason), ui.S.Snapshot().Items.OfType<ToolItem>().Single().Output);
            await CloseAsync(ui);
        }
        finally
        {
            MainViewModel.ApplyTheme("light");
        }
    }

    [AvaloniaFact]
    public async Task A_shortened_or_safety_checked_request_offers_no_rule_and_narrow_cards_fit()
    {
        var ui = await OpenAsync("npm run build --prod", width: 560, height: 760);
        var d = ui.Vm.CurrentDialog!;
        Assert.Equal("bash(npm run build:*)", d.RuleText);
        var card = Find<Border>(ui.W, b => b.Name == "DialogCard");
        foreach (var name in (string[])["AllowButton", "DenyButton", "FeedbackButton", "AllowSessionButton", "AllowProjectButton", "AllowAlwaysButton"])
        {
            var b = Named(ui.W, name);
            var right = b.TranslatePoint(new Avalonia.Point(b.Bounds.Width, 0), card)!.Value.X;
            Assert.True(right <= card.Bounds.Width, $"{name} sticks out of the card at 560 px ({right} > {card.Bounds.Width})");
        }
        Shot(ui.W, "card-560-light");
        Click(Named(ui.W, "AllowSessionButton"));
        await Until(() => ui.Vm.Phase == SessionPhase.Ready, "run end");
        Assert.Equal(ApprovalScope.Session, Assert.Single(ui.Vm.ApprovalRules.Grants).Scope);
        Assert.Null(ui.Store.Load().ApprovalRules); // a session rule is never saved
        await CloseAsync(ui);

        var dialog = TestApprovals.Dialog("Allow tool: bash\nCommand: echo " + new string('x', 40) + "[…9ch elided…]", "x");
        var vm = new DialogViewModel(dialog, (_, _) => Task.CompletedTask, new ApprovalActions((_, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask));
        Assert.False(vm.CanAllowWithRule);
        Assert.True(vm.CanGiveFeedback);
        Assert.False(new DialogViewModel(dialog, (_, _) => Task.CompletedTask).CanGiveFeedback);
    }
}
