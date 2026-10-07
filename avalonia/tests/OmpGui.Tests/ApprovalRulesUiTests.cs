using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// The approval card's "Don't ask again" (this session / this project / always) and "Deny and say why", and Settings →
/// Permissions, in the real window over the fake omp (scenario approval-rules: one request per prompt line).
/// </summary>
public sealed class ApprovalRulesUiTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMPGUI_SHOT_DIR") is { } d
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
            Assert.Equal(("omp wants to run a command", "npm test"), (d.Title, d.Preview));
            Assert.Equal("Don't ask again for commands starting with “npm test”", d.AllowWithRuleText);
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
            Assert.Contains("Approved by you — bash", notices);
            Assert.Contains("Won't ask again for commands starting with “npm test” (this project) — the rules are in Settings → Permissions", notices);
            Assert.Contains("Allowed automatically — commands starting with “npm test” (this project)", notices);
            Assert.Equal(2, ui.Vm.Rows.OfType<ToolRowViewModel>().Count(t => t.StatusText == "done"));
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
            Assert.Equal(("Commands starting with “npm test”", "This project", "bash(npm test:*)"), (listed.Description, listed.ScopeLabel, listed.RuleText));
            Find<ItemsControl>(ui.W, c => c.Name == "PermissionRuleList");
            Shot(ui.W, $"permissions-{theme}");
            Click(Find<Button>(ui.W, b => Avalonia.Automation.AutomationProperties.GetName(b) == "Remove rule: Commands starting with “npm test”"));
            await Until(() => ui.Vm.Permissions.IsEmpty, "rule removed");
            Assert.Null(ui.Store.Load().ApprovalRules);
            Assert.Equal("Removed. omp asks again for commands starting with “npm test”.", ui.Vm.Permissions.Message);
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
            await Until(() => ui.Vm.Phase == SessionPhase.Ready
                && ui.Vm.Rows.OfType<UserRowViewModel>().Any(u => u.Text == "use git clean -fdx, keep 2 caches"), "feedback delivered");
            Assert.True(ui.Vm.Rows.OfType<ToolRowViewModel>().Single().IsFailed);
            Assert.Contains(ui.Vm.Rows.OfType<NoticeRowViewModel>(), n => n.Text == "Denied by you — bash");
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

        var dialog = new PendingDialog("x", DialogKind.Approval, "Allow tool: bash\nCommand: echo " + new string('x', 40) + "[…9ch elided…]", null,
            [new DialogOption("Approve", null, false), new DialogOption("Deny", null, false)], null, null, DateTimeOffset.UtcNow, null);
        var vm = new DialogViewModel(dialog, (_, _) => Task.CompletedTask, new ApprovalActions((_, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask));
        Assert.False(vm.CanAllowWithRule);
        Assert.True(vm.CanGiveFeedback);
        Assert.False(new DialogViewModel(dialog, (_, _) => Task.CompletedTask).CanGiveFeedback);
    }
}
