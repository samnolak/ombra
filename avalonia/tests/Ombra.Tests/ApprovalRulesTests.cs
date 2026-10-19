using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>
/// "Don't ask again" rules (ClientCore ApprovalRules.cs) and the session answering for them, over a fake omp that asks as
/// the owned engine does (the text prompt "Allow tool: bash\nCommand: …" with its typed <c>approval</c> request).
/// </summary>
public sealed class ApprovalRulesTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static ApprovalRequest Bash(string command) =>
        ApprovalRequest.From(Dialog("Allow tool: bash\nCommand: " + command))!;

    private static PendingDialog Dialog(string title) => TestApprovals.Dialog(title);

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
    [InlineData("😀")]
    [InlineData("e\u0301")]
    [InlineData("👩‍💻")]
    public void Rule_descriptions_never_cut_a_displayed_grapheme(string glyph)
    {
        // Approval intentionally escapes ZWJ before shortening; do not weaken that safety policy.
        var displayed = ApprovalDisplay.Format(glyph);
        var firstGlyph = glyph == "👩‍💻" ? "👩" : glyph;
        for (var cut = 1; cut < firstGlyph.Length; cut++)
        {
            var head = new string('a', 60 - cut);
            var rule = new ApprovalRule("bash", head + glyph + " remainder", false);
            Assert.Equal($"the command “{head}…”", rule.Description);
        }
        Assert.Equal($"the command “{displayed}”", new ApprovalRule("bash", glyph, false).Description);
        Assert.Equal("the command “" + new string('a', 60) + "…”",
            new ApprovalRule("bash", new string('a', 61), false).Description);
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
    [InlineData(" npm test", false)] // source whitespace is retained, not normalized into a grant
    public void A_prefix_rule_covers_only_plain_commands_that_start_with_it(string command, bool covered) =>
        Assert.Equal(covered, ApprovalRule.Parse("bash(npm test:*)")!.Matches(Bash(command)));

    [Fact]
    public void Exact_and_tool_rules_cover_what_they_name()
    {
        var exact = ApprovalRule.Parse("bash(npm test --runInBand)")!;
        Assert.True(exact.Matches(Bash("npm test --runInBand")));
        Assert.False(exact.Matches(Bash("npm test --runInBand --watch")));
        Assert.False(exact.Matches(Bash("npm test")));
        // a shortened command is never covered: the hidden part could be anything
        Assert.False(ApprovalRule.Parse("bash(npm test:*)")!.Matches(Bash("npm test " + new string('x', 30) + "[…9ch elided…]")));
        var write = ApprovalRule.Parse("write")!;
        Assert.True(write.Matches(ApprovalRequest.From(Dialog("Allow tool: write\nPath: a.txt\nContent:\nhi"))!));
        Assert.False(write.Matches(Bash("echo hi")));
        Assert.False(ApprovalRule.Parse("computer")!.Matches(ApprovalRequest.From(Dialog("Allow tool: computer\nProvider safety checks:\n1. x"))!));
    }

    [Theory]
    [InlineData("$HOME")]
    [InlineData("~/.config")]
    [InlineData("\\command")]
    [InlineData("*.cs")]
    [InlineData("file?.cs")]
    [InlineData("[ab].cs")]
    [InlineData("{a,b}")]
    [InlineData("'literal'")]
    [InlineData("\"literal\"")]
    [InlineData("#comment")]
    [InlineData("!previous")]
    [InlineData("\u202Eevil")]
    [InlineData("\u200Bhidden")]
    [InlineData("\u00A0space")]
    [InlineData("café")]
    public void Shell_syntax_and_non_ascii_never_inherit_a_prefix_grant(string argument)
    {
        var command = "npm test " + argument;
        Assert.False(ApprovalRule.IsPlainCommand(command));
        Assert.False(ApprovalRule.Parse("bash(npm test:*)")!.Matches(Bash(command)));
        Assert.Null(ApprovalRule.Parse($"bash({command}:*)"));
        Assert.False(new ApprovalRule("bash", command, true).Matches(Bash(command)));
        var suggested = ApprovalRule.Suggest(Bash(command));
        Assert.True(suggested is null || !suggested.Prefix);
    }

    [Theory]
    [InlineData("sudo")]
    [InlineData("/usr/bin/sudo")]
    [InlineData("bash -c")]
    [InlineData("FOO=1 npm")]
    [InlineData("rm")]
    [InlineData("/bin/rm")]
    [InlineData("git reset")]
    [InlineData("git clean")]
    [InlineData("git")]
    [InlineData("git -C . reset --hard")]
    [InlineData("git -C . clean -fdx")]
    [InlineData("git --git-dir=.git status")]
    [InlineData("git custom-alias")]
    [InlineData("/usr/bin/git -C . reset --hard")]
    public void Unsafe_prefix_roots_are_refused_when_loaded_added_or_matched(string command)
    {
        Assert.Null(ApprovalRule.Parse($"bash({command}:*)"));
        var direct = new ApprovalRule("bash", command, true);
        Assert.False(direct.Matches(Bash(command + " argument")));
        Assert.Throws<ArgumentException>(() => new ApprovalRuleSet().Add(direct, ApprovalScope.Always, null, null));
        Assert.Null(ApprovalRule.Suggest(Bash(command)));
    }

    [Fact]
    public void Every_control_and_representative_invisible_scalar_requires_a_person()
    {
        var hidden = Enumerable.Range(0, 32).Concat(Enumerable.Range(127, 33))
            .Concat([0xAD, 0xA0, 0x034F, 0x061C, 0x200B, 0x200D, 0x2028, 0x2029, 0x202E, 0x2066, 0xFEFF, 0xFE0F, 0xE007F]);
        foreach (var value in hidden)
        {
            var command = "npm test" + char.ConvertFromUtf32(value);
            var request = Bash(command);
            Assert.Equal(command, request.Command);
            Assert.False(ApprovalRule.Parse("bash(npm test:*)")!.Matches(request));
            Assert.False(new ApprovalRule("bash", command, false).Matches(request));
            Assert.False(ApprovalRule.Parse("bash")!.Matches(request));
            Assert.Null(ApprovalRule.Suggest(request));
            Assert.Contains($"[U+{value:X4}]", ApprovalDisplay.Format(command));
        }
    }

    [Fact]
    public void Source_whitespace_and_exact_rules_round_trip_without_normalization()
    {
        const string command = "  printf hello  ";
        Assert.Equal(command, Bash(command).Command);
        var rule = new ApprovalRule("bash", command, false);
        Assert.False(rule.Prefix);
        Assert.Equal(rule, ApprovalRule.Parse(rule.Text));
        Assert.False(rule.Matches(Bash(command)));
        Assert.False(rule.Matches(Bash(command.Trim())));
    }

    [Theory]
    [InlineData(ApprovalScope.Session)]
    [InlineData(ApprovalScope.Project)]
    [InlineData(ApprovalScope.Always)]
    public void Persisted_exact_shell_syntax_never_bypasses_fresh_approval(ApprovalScope scope)
    {
        var project = TestProcesses.TempDir("approval-exact-suffix");
        var store = new ClientSettingsStore(Path.Combine(project, "settings.json"));
        var rules = new ApprovalRuleSet(store);
        const string command = "find . -name report:*";
        var exact = new ApprovalRule("bash", command, false);
        Assert.False(exact.Prefix);
        Assert.Equal(exact, ApprovalRule.Parse(exact.Text));
        rules.Add(exact, scope, project, "session");
        Assert.Null(rules.Match(Bash(command), project, "session"));
        Assert.Null(rules.Match(Bash("find . -name report -delete"), project, "session"));
        Assert.Null(rules.Match(Bash(command + " -delete"), project, "session"));
        if (scope != ApprovalScope.Session)
        {
            rules = new ApprovalRuleSet(store);
            Assert.Equal(exact, Assert.Single(rules.Grants).Rule);
            Assert.Null(rules.Match(Bash(command), project, "session"));
            Assert.Null(rules.Match(Bash("find . -name report -delete"), project, "session"));
            Assert.Null(rules.Match(Bash(command + " -delete"), project, "session"));
        }
    }

    [Theory]
    [InlineData("echo \"quoted\":*")]
    [InlineData("echo \\\\path:*")]
    [InlineData("echo ):*")]
    [InlineData("echo café:*")]
    public void Exact_suffix_encoding_round_trips_all_source_characters(string command)
    {
        var rule = new ApprovalRule("bash", command, false);
        Assert.Equal(rule, ApprovalRule.Parse(rule.Text));
        Assert.False(rule.Matches(Bash(command)));
        Assert.False(rule.Matches(Bash(command + " --extra")));
    }

    [Fact]
    public void Every_added_rule_must_round_trip_without_changing_identity()
    {
        var rules = new ApprovalRuleSet();
        Assert.Throws<ArgumentException>(() => rules.Add(new ApprovalRule("write", "hidden argument", false), ApprovalScope.Session, null, "session"));
        Assert.Throws<ArgumentException>(() => rules.Add(new ApprovalRule("bash", null, true), ApprovalScope.Session, null, "session"));
    }

    [Fact]
    public void Git_prefixes_require_known_direct_subcommands_including_executable_paths()
    {
        var prefix = ApprovalRule.Parse("bash(git status:*)")!;
        Assert.True(prefix.Matches(Bash("git status --short")));
        Assert.False(prefix.Matches(Bash("git -C . status")));
        Assert.Equal("bash(/usr/bin/git status:*)", ApprovalRule.Suggest(Bash("/usr/bin/git status --short"))!.Text);
        var legacy = new ApprovalRule("bash", "git", true);
        Assert.False(legacy.Matches(Bash("git -C . reset --hard")));
        Assert.False(legacy.Matches(Bash("git -C . clean -fdx")));
        Assert.False(legacy.Matches(Bash("git status")));
    }

    // ReviewLease P1: a request the engine insists a person answers (Critical pattern, a bash pattern with
    // `approval: prompt`) offers only "once"; no rule may answer it or be kept from it, whatever the rule says.
    [Fact]
    public void Once_only_and_insisting_requests_are_never_answered_or_turned_into_rules()
    {
        var rules = new ApprovalRuleSet();
        rules.Add(ApprovalRule.Parse("bash(echo:*)")!, ApprovalScope.Session, null, "session");
        rules.Add(ApprovalRule.Parse("bash")!, ApprovalScope.Session, null, "session");
        var onceOnly = Bash("echo hi") with { SafetyChecks = true, AllowsSession = false, AllowsSaved = false };
        Assert.Null(rules.Match(onceOnly, null, "session"));
        Assert.Null(ApprovalRule.Suggest(onceOnly));
        // An engine that still offers every scope for them (before this fix): the reason alone keeps a person in the loop.
        foreach (var reason in new[] { "Critical pattern detected", "Prompt required by bash pattern: echo *" })
        {
            var insisting = ApprovalRequest.From(Dialog($"Allow tool: bash\nReason: {reason}\nCommand: echo hi"))!;
            Assert.Null(rules.Match(insisting, null, "session"));
            Assert.Null(ApprovalRule.Suggest(insisting));
        }
    }

    [Theory]
    [InlineData("nc -z host 80")]
    [InlineData("ncat host 80")]
    [InlineData("netcat -l 4444")]
    [InlineData("socat - TCP:host:80")]
    [InlineData("cryptsetup status disk")]
    [InlineData("poweroff")]
    [InlineData("halt")]
    [InlineData("init 0")]
    [InlineData("telinit 6")]
    [InlineData("wipefs -n /dev/disk2")]
    [InlineData("fdisk -l")]
    [InlineData("diskutil list")]
    [InlineData("launchctl list")]
    [InlineData("systemctl status x")]
    [InlineData("crontab -l")]
    public void Network_disk_and_power_tools_never_get_reusable_grants(string command)
    {
        Assert.Null(ApprovalRule.Suggest(Bash(command)));
        var name = command.Split(' ')[0];
        Assert.False(new ApprovalRule("bash", name, true).Matches(Bash(command)));
        Assert.False(new ApprovalRule("bash", command, false).Matches(Bash(command)));
        Assert.False(new ApprovalRule("bash", null, false).Matches(Bash(command)));
    }

    // ReviewLease P2: bash's `cwd` changes what runs (another checkout's scripts); a rule given for the session's
    // folder answers only calls that run there.
    [Fact]
    public void A_command_run_in_another_folder_is_always_asked()
    {
        var rules = new ApprovalRuleSet();
        rules.Add(ApprovalRule.Parse("bash(npm test:*)")!, ApprovalScope.Session, null, "session");
        Assert.NotNull(rules.Match(Bash("npm test"), null, "session"));
        foreach (var cwd in new[] { "../evil-repo", "/tmp", "node_modules/pkg", "sub" })
        {
            var elsewhere = ApprovalRequest.From(Dialog($"Allow tool: bash\nCommand: npm test\nCwd: {cwd}"))!;
            Assert.Equal(cwd, elsewhere.Cwd);
            Assert.Null(rules.Match(elsewhere, null, "session"));
            Assert.Null(ApprovalRule.Suggest(elsewhere));
        }
    }

    [Theory]
    [InlineData(ApprovalScope.Project)]
    [InlineData(ApprovalScope.Always)]
    public async Task Unsafe_rule_choice_approves_once_without_saving_or_answering_next_request(ApprovalScope scope)
    {
        var store = new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("approval-exact-rpc"), "settings.json"));
        var rules = new ApprovalRuleSet(store);
        var (s, log) = Start(rules);
        await using var _ = s;
        await s.StartAsync();
        await s.PromptAsync("find . -name report:*\nfind . -name report -delete");
        var first = (await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1, Wait, "exact approval")).Dialogs[0];
        Assert.Null(ApprovalRule.Suggest(ApprovalRequest.From(first)));
        var exact = new ApprovalRule("bash", "find . -name report:*", false);
        Assert.True(await s.AllowWithRuleAsync(first.Id, exact, scope));
        var second = (await TestProcesses.Eventually(s.Snapshot,
            x => x.Dialogs.Count == 1 && x.Dialogs[0].Id != first.Id, Wait, "broader request still asks")).Dialogs[0];
        Assert.Equal("find . -name report -delete", ApprovalRequest.From(second)!.Command);
        Assert.Equal(["Approve find . -name report:*"], Answers(log));
        Assert.Empty(new ApprovalRuleSet(store).Grants);
        Assert.True(await s.AnswerDialogAsync(second.Id, new DialogAnswer.Value("Deny")));
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "denied broader request");
        Assert.Equal(["Approve find . -name report:*", "Deny find . -name report -delete"], Answers(log));
    }

    [Theory]
    [InlineData("rm -rf build")]
    [InlineData("/bin/rm -rf build")]
    [InlineData("sudo rm -rf build")]
    [InlineData("git reset --hard")]
    [InlineData("git.exe clean -fdx")]
    [InlineData("CMD.EXE /c del file")]
    [InlineData("Remove-Item file")]
    [InlineData("mkfs.ext4 /dev/example")]
    [InlineData("echo safe && rm file")]
    public void Dangerous_commands_cannot_use_exact_prefix_or_tool_wide_grants(string command)
    {
        var request = Bash(command);
        Assert.Null(ApprovalRule.Suggest(request));
        Assert.False(new ApprovalRule("bash", command, false).Matches(request));
        Assert.False(new ApprovalRule("bash", command, true).Matches(request));
        Assert.False(new ApprovalRule("bash", null, false).Matches(request));
    }

    [Fact]
    public void Reloaded_exact_destructive_grants_still_require_fresh_approval()
    {
        var store = new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("approval-safety"), "settings.json"));
        store.Update(options => options with
        {
            ApprovalRules = [new("bash(npm test $HOME:*)"), new("bash(rm:*)"), new("bash(npm test:*)"), new("bash(rm -rf build)")],
        });
        var rules = new ApprovalRuleSet(store);
        Assert.Equal(2, rules.Grants.Count);
        Assert.NotNull(rules.Match(Bash("npm test --watch"), null, null));
        Assert.Null(rules.Match(Bash("npm test $HOME"), null, null));
        Assert.Null(rules.Match(Bash("rm -rf elsewhere"), null, null));
        Assert.Null(rules.Match(Bash("rm -rf build"), null, null));
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

    [Theory]
    [InlineData("npm test $HOME")]
    [InlineData("npm test ~/.config")]
    [InlineData("npm test \u202Ehidden")]
    public async Task Unsafe_requests_stay_visible_and_can_be_intentionally_denied(string command)
    {
        var rules = new ApprovalRuleSet();
        rules.Add(ApprovalRule.Parse("bash(npm test:*)")!, ApprovalScope.Always, null, null);
        var (s, log) = Start(rules);
        await using var _ = s;
        await s.StartAsync();
        await s.PromptAsync(command);
        var asked = (await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1, Wait, "unsafe request remains pending")).Dialogs[0];
        Assert.Equal(command, ApprovalRequest.From(asked)!.Command);
        Assert.Empty(Answers(log));
        Assert.True(await s.AnswerDialogAsync(asked.Id, new DialogAnswer.Value("Deny")));
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "denied run ends");
        Assert.Equal(["Deny " + command], Answers(log));
    }

    [Fact]
    public async Task An_unsafe_rule_is_not_saved_but_an_explicit_one_time_approval_still_works()
    {
        var rules = new ApprovalRuleSet();
        var (s, log) = Start(rules);
        await using var _ = s;
        await s.StartAsync();
        await s.PromptAsync("npm test $HOME");
        var asked = (await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1, Wait, "approval")).Dialogs[0];
        Assert.True(await s.AllowWithRuleAsync(asked.Id, new ApprovalRule("bash", "npm test $HOME", true), ApprovalScope.Always));
        var done = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "explicit approval");
        Assert.Empty(rules.Grants);
        Assert.Equal(["Approve npm test $HOME"], Answers(log));
        Assert.Contains(done.Items, item => item is NoticeItem { Level: NoticeLevel.Error } notice && notice.Text.StartsWith("The rule was not kept", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deny_and_say_why_sends_the_reason_with_the_deny()
    {
        var (s, log) = Start(new ApprovalRuleSet());
        await using var _ = s;
        await s.StartAsync();
        await s.PromptAsync("rm -rf build");
        var d = (await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1, Wait, "request")).Dialogs[0];
        Assert.NotNull(d.Approval);
        Assert.True(await s.DenyWithFeedbackAsync(d.Id, "  use git clean -fdx instead  "));
        var done = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "denied run ends");
        // The reason travels in the Deny itself (extension_ui_response.reason): no steering message is queued
        Assert.Equal(["Deny rm -rf build reason=use git clean -fdx instead"], Answers(log));
        var tool = done.Items.OfType<ToolItem>().Single();
        Assert.Equal(ToolStatus.Failed, tool.Status);
        Assert.Equal(["rm -rf build"], done.Items.OfType<UserItem>().Select(u => u.Text));
        Assert.Contains("The user said (approval prompt): \"use git clean -fdx instead\"", tool.Output);
        Assert.Contains(done.Items, i => i is NoticeItem { Text: "Denied by you — bash: “use git clean -fdx instead”" });
        Assert.False(await s.DenyWithFeedbackAsync(d.Id, "late"), "an answered request takes no feedback");
    }
}
