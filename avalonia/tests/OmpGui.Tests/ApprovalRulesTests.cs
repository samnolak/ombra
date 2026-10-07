using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// "Don't ask again" rules (ClientCore ApprovalRules.cs) and the session answering for them, over a fake omp that asks as
/// omp 18.8 does ("Allow tool: bash\nCommand: …").
/// </summary>
public sealed class ApprovalRulesTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static ApprovalRequest Bash(string command) =>
        ApprovalRequest.From(Dialog("Allow tool: bash\nCommand: " + command))!;

    private static PendingDialog Dialog(string title) =>
        new("d", DialogKind.Approval, title, null, [new DialogOption("Approve", null, false), new DialogOption("Deny", null, false)], null, null, DateTimeOffset.UtcNow, null);

    [Theory]
    [InlineData("bash(npm test:*)", "bash", "npm test", true)]
    [InlineData("bash(npm test)", "bash", "npm test", false)]
    [InlineData("write", "write", null, false)]
    [InlineData("mcp__docs__lookup", "mcp__docs__lookup", null, false)]
    public void Rules_read_back_as_written(string text, string tool, string? command, bool prefix)
    {
        var rule = ApprovalRule.Parse(text)!;
        Assert.Equal(new ApprovalRule(tool, command, prefix), rule);
        Assert.Equal(text, rule.Text);
    }

    [Theory]
    [InlineData("write(a.txt)")] // only bash takes a pattern
    [InlineData("bash()")]
    [InlineData("bash(:*)")]
    [InlineData("not a rule")]
    [InlineData("")]
    public void Malformed_rules_are_refused(string text) => Assert.Null(ApprovalRule.Parse(text));

    [Fact]
    public void The_request_is_read_from_omps_prompt()
    {
        // tools/approval.ts: "Allow tool", then omp's Reason line, then bash's "Command: …" (a command may span lines)
        var r = ApprovalRequest.From(Dialog("Allow tool: bash\nReason: outside the project\nCommand: printf 'a\nb'"))!;
        Assert.Equal(("bash", "printf 'a\nb'", false, false), (r.Tool, r.Command, r.Truncated, r.SafetyChecks));
        Assert.True(ApprovalRequest.From(Dialog("Allow tool: bash\nCommand: " + new string('x', 2000) + "[…12ch elided…]"))!.Truncated);
        Assert.True(ApprovalRequest.From(Dialog("Allow tool: computer\nProvider safety checks:\n1. sensitive"))!.SafetyChecks);
        var write = ApprovalRequest.From(Dialog("Allow tool: write\nPath: a.txt\nContent:\nhello"))!;
        Assert.Equal(("write", null), (write.Tool, write.Command));
        Assert.Null(ApprovalRequest.From(Dialog("Pick one") with { Kind = DialogKind.Select }));
    }

    [Theory]
    [InlineData("npm test", "bash(npm test:*)")]
    [InlineData("npm test -- --watch", "bash(npm test:*)")]
    [InlineData("npm run build --prod", "bash(npm run build:*)")]
    [InlineData("git status --short", "bash(git status:*)")]
    [InlineData("ls -la src", "bash(ls:*)")] // no subcommands: the first word
    [InlineData("./gradlew build", "bash(./gradlew:*)")]
    [InlineData("npm test && rm -rf build", "bash(npm test && rm -rf build)")] // chained: that exact command only
    [InlineData("FOO=1 npm test", "bash(FOO=1 npm test)")]
    [InlineData("sudo apt install x", "bash(sudo apt install x)")] // a wrapper runs whatever follows
    [InlineData("bash -c 'echo hi'", "bash(bash -c 'echo hi')")]
    [InlineData("rm -rf build", "bash(rm -rf build)")] // destructive: "rm" would also cover rm -rf ~
    [InlineData("chmod -R 777 .", "bash(chmod -R 777 .)")]
    public void A_command_offers_a_rule_no_wider_than_it(string command, string expected) =>
        Assert.Equal(expected, ApprovalRule.Suggest(Bash(command))!.Text);

    [Fact]
    public void Nothing_is_offered_when_the_request_is_not_all_in_view()
    {
        Assert.Null(ApprovalRule.Suggest(Bash("echo " + new string('x', 30) + "[…5ch elided…]")));
        Assert.Null(ApprovalRule.Suggest(ApprovalRequest.From(Dialog("Allow tool: computer\nAction: click\nProvider safety checks:\n1. x"))));
        Assert.Equal("write", ApprovalRule.Suggest(ApprovalRequest.From(Dialog("Allow tool: write\nPath: a.txt\nContent:\nhi")))!.Text);
    }

    [Theory]
    [InlineData("npm test", true)]
    [InlineData("npm test --watch", true)]
    [InlineData("npm test -- src/a.test.ts", true)]
    [InlineData("npm testing", false)] // a word boundary, not a string prefix
    [InlineData("npm tes", false)]
    [InlineData("npm  test", false)]
    [InlineData("npm test; rm -rf ~", false)] // anything chained, piped, redirected or substituted is asked
    [InlineData("npm test && rm -rf ~", false)]
    [InlineData("npm test || rm -rf ~", false)]
    [InlineData("npm test | tee log", false)]
    [InlineData("npm test & rm -rf ~", false)]
    [InlineData("npm test > out.txt", false)]
    [InlineData("npm test < in.txt", false)]
    [InlineData("npm test $(rm -rf ~)", false)]
    [InlineData("npm test `rm -rf ~`", false)]
    [InlineData("npm test ${HOME:=x}", false)]
    [InlineData("npm test\nrm -rf ~", false)]
    [InlineData("npm test (x)", false)]
    [InlineData(" npm test", true)] // omp's details are trimmed
    public void A_prefix_rule_covers_only_plain_commands_that_start_with_it(string command, bool covered) =>
        Assert.Equal(covered, ApprovalRule.Parse("bash(npm test:*)")!.Matches(Bash(command)));

    [Fact]
    public void Exact_and_tool_rules_cover_what_they_name()
    {
        var exact = ApprovalRule.Parse("bash(npm test && npm run lint)")!;
        Assert.True(exact.Matches(Bash("npm test && npm run lint")));
        Assert.False(exact.Matches(Bash("npm test && npm run lint; rm -rf ~")));
        Assert.False(exact.Matches(Bash("npm test")));
        // a shortened command is never covered: the hidden part could be anything
        Assert.False(ApprovalRule.Parse("bash(npm test:*)")!.Matches(Bash("npm test " + new string('x', 30) + "[…9ch elided…]")));
        var write = ApprovalRule.Parse("write")!;
        Assert.True(write.Matches(ApprovalRequest.From(Dialog("Allow tool: write\nPath: a.txt\nContent:\nhi"))!));
        Assert.False(write.Matches(Bash("echo hi")));
        Assert.False(ApprovalRule.Parse("computer")!.Matches(ApprovalRequest.From(Dialog("Allow tool: computer\nProvider safety checks:\n1. x"))!));
    }

    [Fact]
    public void Scopes_apply_where_they_were_given_and_persist_as_chosen()
    {
        var dir = TestProcesses.TempDir("approval-rules");
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, """{ "theme": "dark" }""");
        var store = new ClientSettingsStore(path);
        var projectA = Path.Combine(dir, "a");
        var projectB = Path.Combine(dir, "b");
        var set = new ApprovalRuleSet(store);
        var changes = 0;
        set.Changed += () => changes++;

        var session = set.Add(ApprovalRule.Parse("bash(git status:*)")!, ApprovalScope.Session, projectA, "s1");
        var project = set.Add(ApprovalRule.Parse("bash(npm test:*)")!, ApprovalScope.Project, projectA, "s1");
        var always = set.Add(ApprovalRule.Parse("write")!, ApprovalScope.Always, projectA, "s1");
        Assert.Equal(3, changes);

        Assert.Equal(session, set.Match(Bash("git status"), projectA, "s1"));
        Assert.Null(set.Match(Bash("git status"), projectA, "s2")); // another session
        Assert.Equal(project, set.Match(Bash("npm test"), projectA + Path.DirectorySeparatorChar, "s2"));
        Assert.Null(set.Match(Bash("npm test"), projectB, "s1")); // another project
        Assert.Equal(always, set.Match(ApprovalRequest.From(Dialog("Allow tool: write\nPath: x\nContent:\n")), projectB, null));
        Assert.Equal("commands starting with “npm test” (this project)", project.Describe());

        // Saved: project and always rules, next to the file's other settings; never the session rule
        var saved = store.Load();
        Assert.Equal("dark", saved.Theme);
        Assert.Equal([new SavedApprovalRule("bash(npm test:*)", projectA), new SavedApprovalRule("write")], saved.ApprovalRules!);
        var reread = new ApprovalRuleSet(store);
        Assert.Equal([project, always], reread.Grants);
        Assert.Null(reread.Match(Bash("git status"), projectA, "s1"));

        // Revoke: out of force at once, and out of the file
        Assert.True(set.Remove(project));
        Assert.Null(set.Match(Bash("npm test"), projectA, "s1"));
        Assert.True(set.Remove(session));
        Assert.Null(set.Match(Bash("git status"), projectA, "s1"));
        Assert.Equal([new SavedApprovalRule("write")], store.Load().ApprovalRules!);
        Assert.False(set.Remove(project));
        Assert.True(set.Remove(always));
        Assert.Null(store.Load().ApprovalRules);
        Assert.Equal("dark", store.Load().Theme);
    }

    [Fact]
    public void A_rule_given_in_another_window_is_kept_when_this_one_saves()
    {
        var path = Path.Combine(TestProcesses.TempDir("approval-rules"), "settings.json");
        var store = new ClientSettingsStore(path);
        var a = new ApprovalRuleSet(store);
        var b = new ApprovalRuleSet(store);
        a.Add(ApprovalRule.Parse("bash(npm test:*)")!, ApprovalScope.Always, null, null);
        b.Add(ApprovalRule.Parse("write")!, ApprovalScope.Always, null, null);
        Assert.Equal(["bash(npm test:*)", "write"], store.Load().ApprovalRules!.Select(r => r.Rule));
        Assert.Equal(2, b.Grants.Count);
        a.Reload();
        Assert.Equal(2, a.Grants.Count);
    }

    private static (SessionController s, string log) Start(ApprovalRuleSet rules)
    {
        var log = Path.Combine(TestProcesses.TempDir("approval-log"), "answers.txt");
        var spec = TestProcesses.Fake("approval-rules");
        var s = new SessionController(spec with { Environment = new Dictionary<string, string?> { ["FAKE_OMP_APPROVAL_LOG"] = log } }) { ApprovalRules = rules };
        return (s, log);
    }

    private static string[] Answers(string log) => File.Exists(log) ? File.ReadAllLines(log) : [];

    [Fact]
    public async Task Covered_requests_are_answered_for_the_user_and_the_rest_are_asked()
    {
        var rules = new ApprovalRuleSet();
        rules.Add(ApprovalRule.Parse("bash(npm test:*)")!, ApprovalScope.Always, null, null);
        var (s, log) = Start(rules);
        await using var _ = s;
        await s.StartAsync();
        await s.PromptAsync("npm test --watch\nnpm testing\nnpm test && echo boom");

        // 1: covered, answered without a card; 2 and 3: asked (not the same command; chained)
        var asked = await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1, Wait, "second request asked");
        Assert.Equal("Command: npm testing", asked.Dialogs[0].Details);
        Assert.Contains(asked.Items, i => i is NoticeItem { Text: "Allowed automatically — commands starting with “npm test” (always)" });
        Assert.True(await s.AnswerDialogAsync(asked.Dialogs[0].Id, new DialogAnswer.Value("Deny")));
        var third = await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1 && x.Dialogs[0].Details.Contains("boom"), Wait, "third request asked");
        Assert.True(await s.AnswerDialogAsync(third.Dialogs[0].Id, new DialogAnswer.Value("Deny")));
        var done = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "run end");

        Assert.Equal(["Approve npm test --watch", "Deny npm testing", "Deny npm test && echo boom"], Answers(log));
        Assert.Equal([ToolStatus.Succeeded, ToolStatus.Failed, ToolStatus.Failed], done.Items.OfType<ToolItem>().Select(t => t.Status));
        Assert.Single(done.Items.OfType<NoticeItem>(), n => n.Text.StartsWith("Allowed automatically", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dont_ask_again_keeps_the_rule_and_removing_it_asks_again()
    {
        var rules = new ApprovalRuleSet();
        var (s, log) = Start(rules);
        await using var _ = s;
        await s.StartAsync();
        await s.PromptAsync("git status\ngit status --short\ngit log");

        var first = (await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1, Wait, "first request")).Dialogs[0];
        var rule = ApprovalRule.Suggest(ApprovalRequest.From(first))!;
        Assert.Equal("bash(git status:*)", rule.Text);
        Assert.True(await s.AllowWithRuleAsync(first.Id, rule, ApprovalScope.Session));
        var grant = Assert.Single(rules.Grants);
        Assert.Equal((ApprovalScope.Session, s.RuleSessionKey), (grant.Scope, grant.Session));

        // "git status --short" is covered; "git log" is not
        var asked = await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1, Wait, "git log asked");
        Assert.Equal("Command: git log", asked.Dialogs[0].Details);
        Assert.Contains(asked.Items, i => i is NoticeItem { Text: "Won't ask again for commands starting with “git status” (this session) — the rules are in Settings → Permissions" });
        Assert.Contains(asked.Items, i => i is NoticeItem { Text: "Allowed automatically — commands starting with “git status” (this session)" });
        await s.AnswerDialogAsync(asked.Dialogs[0].Id, new DialogAnswer.Value("Approve"));
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "run end");
        Assert.Equal(["Approve git status", "Approve git status --short", "Approve git log"], Answers(log));

        // Revoked: the same command is asked again
        Assert.True(rules.Remove(grant));
        await s.PromptAsync("git status");
        var again = await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1, Wait, "asked again after removal");
        Assert.Equal("Command: git status", again.Dialogs[0].Details);
        await s.AnswerDialogAsync(again.Dialogs[0].Id, new DialogAnswer.Value("Deny"));
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "second run end");
    }

    [Fact]
    public async Task A_project_rule_answers_in_its_project_only()
    {
        var rules = new ApprovalRuleSet();
        var (s, log) = Start(rules);
        await using var _ = s;
        await s.StartAsync();
        rules.Add(ApprovalRule.Parse("bash(make:*)")!, ApprovalScope.Project, Path.Combine(TestProcesses.TempDir("elsewhere"), "p"), null);
        await s.PromptAsync("make all");
        var asked = await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1, Wait, "asked: the rule is another project's");
        await s.AnswerDialogAsync(asked.Dialogs[0].Id, new DialogAnswer.Value("Deny"));
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "run end");

        rules.Add(ApprovalRule.Parse("bash(make:*)")!, ApprovalScope.Project, s.Snapshot().Cwd, null);
        await s.PromptAsync("make all");
        var done = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.Items.OfType<ToolItem>().Count() == 2, Wait, "covered run end");
        Assert.Empty(done.Dialogs);
        Assert.Equal(["Deny make all", "Approve make all"], Answers(log));
    }

    [Fact]
    public async Task Deny_and_say_why_tells_omp_what_to_do_instead()
    {
        var (s, log) = Start(new ApprovalRuleSet());
        await using var _ = s;
        await s.StartAsync();
        await s.PromptAsync("rm -rf build");
        var d = (await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1, Wait, "request")).Dialogs[0];
        Assert.True(await s.DenyWithFeedbackAsync(d.Id, "  use git clean -fdx instead  "));
        var done = await TestProcesses.Eventually(s.Snapshot,
            x => x.Phase == SessionPhase.Ready && x.Items.OfType<AssistantItem>().Any(a => a.Text == "followed: use git clean -fdx instead"), Wait, "feedback delivered");
        Assert.Equal(["Deny rm -rf build"], Answers(log));
        Assert.Equal(ToolStatus.Failed, done.Items.OfType<ToolItem>().Single().Status);
        Assert.Contains(done.Items, i => i is UserItem { Text: "use git clean -fdx instead" });
        Assert.Contains(done.Items, i => i is NoticeItem { Text: "Denied by you — bash" });
        Assert.False(await s.DenyWithFeedbackAsync(d.Id, "late"), "an answered request takes no feedback");
    }
}
