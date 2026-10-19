using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>
/// The approval card over the engine's typed request (<c>extension_ui_request.approval</c>): risk tag, reason, details,
/// provider safety checks, the complete command in a bounded selectable box, and only the scopes the engine offers.
/// </summary>
public sealed class ApprovalCardUiTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMBRA_SHOT_DIR") is { } d
        ? Path.Combine(d, "ApprovalCard")
        : Path.Combine(AppContext.BaseDirectory, "screenshots", "ApprovalCard");

    /// <summary>The dialog exactly as ConversationState builds it from the engine's frame.</summary>
    private static PendingDialog FromFrame(string title, string approvalJson, string id = "ap1")
    {
        var s = new ConversationState();
        var frame = JsonSerializer.Serialize(new { type = "extension_ui_request", id, method = "select", title, options = new[] { "Approve", "Deny" } });
        frame = frame[..^1] + ",\"approval\":" + approvalJson + "}";
        var e = JsonDocument.Parse(frame).RootElement.Clone();
        s.Apply(new Ombra.Rpc.RpcFrame("extension_ui_request", null, e, System.Diagnostics.Stopwatch.GetTimestamp(), frame.Length + 1), DateTimeOffset.UtcNow);
        return Assert.Single(s.Dialogs);
    }

    private static DialogViewModel Card(PendingDialog d, Action<DialogAnswer>? answered = null, Action<string>? feedback = null) =>
        new(d, (_, a) => { answered?.Invoke(a); return Task.CompletedTask; },
            new ApprovalActions((_, _, _) => Task.CompletedTask, (_, words) => { feedback?.Invoke(words); return Task.CompletedTask; }));

    private static void Settle(Window w)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
        }
    }

    private static void Shot(Window w, string name)
    {
        Settle(w);
        Directory.CreateDirectory(ShotDir);
        using var frame = w.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(ShotDir, name + ".png"));
        frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static T Named<T>(Window w, string name) where T : Control =>
        w.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"no {typeof(T).Name} named {name}");

    private static (MainWindow W, MainViewModel Vm) Window(double width, double height)
    {
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs(),
            settings: new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("approval-card"), "settings.json")));
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        return (w, vm);
    }

    [Fact]
    public void The_typed_request_is_read_from_the_frame()
    {
        var d = FromFrame("Allow tool: bash\nReason: cleans the build\nCommand: rm -rf build",
            """{"tool":"bash","toolCallId":"t7","risk":"exec","reason":"cleans the build","origin":"mcp","details":["Command: rm -rf build"],"command":"rm -rf build","safetyChecks":["malicious_code: check it"],"scopes":["once"]}""");
        var r = Assert.IsType<ApprovalRequest>(d.Approval);
        Assert.Equal(("bash", "t7", ApprovalRisk.Exec, "cleans the build", true), (r.Tool, r.ToolCallId, r.Risk, r.Reason, r.FromMcp));
        Assert.Equal("rm -rf build", r.Command);
        Assert.Equal(["Command: rm -rf build"], r.Details);
        Assert.Equal(["malicious_code: check it"], r.SafetyCheckLines);
        Assert.True(r.SafetyChecks);
        Assert.False(r.Allows(ApprovalScope.Session));

        var write = FromFrame("Allow tool: write", """{"tool":"write","risk":"write","details":["Path: a.txt"],"scopes":["once","session"]}""").Approval!;
        Assert.Equal(ApprovalRisk.Write, write.Risk);
        Assert.False(write.SafetyChecks);
        Assert.True(write.Allows(ApprovalScope.Session));
        Assert.False(write.Allows(ApprovalScope.Project));
        Assert.False(write.Allows(ApprovalScope.Always));

        Assert.Null(FromFrame("Allow tool: x", """{"tool":"x","risk":"bogus"}""").Approval); // unusable: text-only card
    }

    [Theory]
    [InlineData("read", "Reads only")]
    [InlineData("write", "Changes files")]
    [InlineData("exec", "Runs programs")]
    public void Risk_reads_as_words(string risk, string text)
    {
        var vm = Card(FromFrame("Allow tool: x", $$"""{"tool":"x","risk":"{{risk}}","details":["Thing: y"],"scopes":["once","session","always"]}"""));
        Assert.True(vm.HasRisk);
        Assert.Equal(Loc.T(text), vm.RiskText);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_card_shows_risk_reason_details_and_safety_checks(string theme)
    {
        MainViewModel.ApplyTheme(theme);
        var (w, vm) = Window(1000, 760);
        try
        {
            var d = Card(FromFrame("Allow tool: write\nReason: writes the release notes",
                """{"tool":"write","toolCallId":"t1","risk":"write","reason":"writes the release notes","origin":"mcp","details":["Path: docs/RELEASE.md","Content:","# 2.0"],"safetyChecks":["Writes outside the project? No","Overwrites a file? Yes"],"scopes":["once"]}"""));
            vm.CurrentDialog = d;
            Settle(w);
            Assert.Equal(Loc.T("omp wants to write a file"), Named<SelectableTextBlock>(w, "DialogTitle").Text);
            var risk = Named<Border>(w, "DialogRisk");
            Assert.True(risk.IsEffectivelyVisible);
            Assert.Contains(risk.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("Changes files"));
            Assert.Equal("Path: docs/RELEASE.md\nContent:\n# 2.0", Named<SelectableTextBlock>(w, "DialogPreview").Text);
            var note = Named<TextBlock>(w, "DialogNote");
            Assert.True(note.IsEffectivelyVisible);
            Assert.Equal("writes the release notes · " + Loc.T("MCP server tool") + " · " + Loc.T("Provider safety checks:") + " Writes outside the project? No; Overwrites a file? Yes", note.Text);
            // Safety checks: only "once" — no rule may keep the answer
            Assert.False(d.CanAllowWithRule);
            Assert.False(Named<WrapPanel>(w, "AllowRuleRow").IsEffectivelyVisible);
            Assert.True(Named<Button>(w, "AllowButton").IsEffectivelyVisible);
            Assert.True(Named<Button>(w, "DenyButton").IsEffectivelyVisible);
            Shot(w, $"card-risk-reason-checks-{theme}");
        }
        finally
        {
            w.Close();
            MainViewModel.ApplyTheme("light");
        }
    }

    [AvaloniaFact]
    public void The_full_command_is_shown_bounded_and_selectable()
    {
        var (w, vm) = Window(1000, 760);
        try
        {
            var command = "for f in $(seq 1 200); do echo \"line $f\" >> out.txt; done && " + string.Join(" && ", Enumerable.Range(1, 120).Select(i => $"echo step{i}"));
            var d = Card(FromFrame("Allow tool: bash\nCommand: " + command,
                JsonSerializer.Serialize(new { tool = "bash", risk = "exec", details = new[] { "Command: " + command[..80] + "…" }, command, scopes = new[] { "once", "session", "always" } })));
            vm.CurrentDialog = d;
            Settle(w);
            var preview = Named<SelectableTextBlock>(w, "DialogPreview");
            Assert.Equal(command, preview.Text); // the complete command, never the shortened details line
            var scroll = Named<ScrollViewer>(w, "DialogPreviewScroll");
            Assert.True(scroll.Bounds.Height <= 160.5, $"preview box grew to {scroll.Bounds.Height}");
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "a long command scrolls inside its box");
            preview.SelectAll();
            Assert.Equal(command, preview.SelectedText); // what Copy takes
            Shot(w, "card-long-command");
        }
        finally { w.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("""["once","session","always"]""", true, true)]
    [InlineData("""["once","session"]""", true, false)]
    [InlineData("""["once","always"]""", false, true)]
    [InlineData("""["once"]""", false, false)]
    public void Scope_buttons_follow_the_offered_scopes(string scopes, bool session, bool saved)
    {
        var (w, vm) = Window(1000, 760);
        try
        {
            var d = Card(FromFrame("Allow tool: bash\nCommand: npm test",
                $$"""{"tool":"bash","risk":"exec","details":["Command: npm test"],"command":"npm test","scopes":{{scopes}}}"""));
            vm.CurrentDialog = d;
            Settle(w);
            Assert.Equal(session || saved, Named<WrapPanel>(w, "AllowRuleRow").IsEffectivelyVisible);
            Assert.Equal(session, Named<Button>(w, "AllowSessionButton").IsEffectivelyVisible);
            Assert.Equal(saved, Named<Button>(w, "AllowProjectButton").IsEffectivelyVisible);
            Assert.Equal(saved, Named<Button>(w, "AllowAlwaysButton").IsEffectivelyVisible);
            if (session && !saved) Shot(w, "card-session-scope-only");
        }
        finally { w.Close(); }
    }

    [Fact]
    public async Task A_rule_outside_the_offered_scopes_neither_answers_nor_is_kept()
    {
        var request = FromFrame("Allow tool: bash\nCommand: npm test",
            """{"tool":"bash","risk":"exec","details":["Command: npm test"],"command":"npm test","scopes":["once","session"]}""").Approval!;
        var rules = new ApprovalRuleSet();
        rules.Add(ApprovalRule.Parse("bash(npm test:*)")!, ApprovalScope.Always, null, null);
        Assert.Null(rules.Match(request, "/p", "s1")); // a saved rule may not answer a session-only request
        rules.Add(ApprovalRule.Parse("bash(npm test:*)")!, ApprovalScope.Session, null, "s1");
        Assert.Equal(ApprovalScope.Session, rules.Match(request, "/p", "s1")!.Scope);

        // The card ignores a scope the engine did not offer
        var kept = new List<ApprovalScope>();
        var vm = new DialogViewModel(FromFrame("Allow tool: bash\nCommand: npm test",
                """{"tool":"bash","risk":"exec","details":["Command: npm test"],"command":"npm test","scopes":["once","session"]}"""),
            (_, _) => Task.CompletedTask, new ApprovalActions((_, _, scope) => { kept.Add(scope); return Task.CompletedTask; }, (_, _) => Task.CompletedTask));
        await vm.AllowWithRuleCommand.ExecuteAsync("always");
        Assert.Empty(kept);
        await vm.AllowWithRuleCommand.ExecuteAsync("session");
        Assert.Equal([ApprovalScope.Session], kept);
    }

    [AvaloniaFact]
    public async Task Deny_with_reason_sends_the_words()
    {
        var (w, vm) = Window(1000, 760);
        try
        {
            string? sent = null;
            DialogAnswer? plain = null;
            var d = Card(FromFrame("Allow tool: bash\nCommand: rm -rf build",
                """{"tool":"bash","risk":"exec","details":["Command: rm -rf build"],"command":"rm -rf build","scopes":["once","session","always"]}"""),
                a => plain = a, words => sent = words);
            vm.CurrentDialog = d;
            Settle(w);
            Named<Button>(w, "FeedbackButton").Command!.Execute(null);
            Settle(w);
            var box = Named<TextBox>(w, "FeedbackBox");
            Assert.True(box.IsEffectivelyVisible);
            box.Text = "use git clean -fdx instead";
            Shot(w, "card-deny-with-reason");
            await d.SendFeedbackCommand.ExecuteAsync(null);
            Assert.Equal("use git clean -fdx instead", sent);
            Assert.Null(plain); // not a plain Deny
        }
        finally { w.Close(); }
    }
}
