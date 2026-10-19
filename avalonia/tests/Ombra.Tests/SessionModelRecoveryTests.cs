using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

public sealed class SessionModelRecoveryTests
{
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Model recovery UI did not settle");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static string Saved(string root) =>
        ChatScrollTests.WriteSession(root, root, "Saved conversation", [
            new { role = "user", content = "saved question" },
            new { role = "assistant", content = new[] { new { type = "text", text = "saved answer" } }, stopReason = "stop" },
        ]);

    [AvaloniaTheory]
    [InlineData(1100)]
    [InlineData(480)]
    public async Task Missing_saved_model_has_explicit_recovery_and_retry_never_substitutes_or_sends(double width)
    {
        var root = TestProcesses.TempDir("missing-model-ui");
        var file = Saved(root);
        var original = File.ReadAllText(file);
        var launchLog = Path.Combine(root, "opens.jsonl");
        var inputLog = Path.Combine(root, "input.log");
        var factory = TestProcesses.FakeFactory("missing-saved-model", root);
        var controller = new SessionController(request => factory(request) with
        {
            Environment = new Dictionary<string, string?> { ["FAKE_SESSION_DIR"] = root,
                ["FAKE_OMP_SESSION_LOG"] = launchLog, ["FAKE_OMP_INPUT_LOG"] = inputLog },
        }, new LaunchRequest(root, file));
        var vm = new MainViewModel(controller, new AppArgs());
        var window = new MainWindow { DataContext = vm, Width = width, Height = 720 };
        window.Show();
        vm.OnWindowOpened();
        try
        {
            await Until(() => vm.CanOpenOnCurrentModel);
            Assert.Equal(StartProblem.SessionModelUnavailable, controller.Snapshot().StartProblem);
            Assert.Equal(Loc.T("Saved model unavailable"), vm.RecoverTitle);
            Assert.Contains("removed-provider/missing-model", vm.RecoverHint);
            Assert.DoesNotContain("Could not restore model", vm.ErrorDetail); // typed identity, unrelated error prose
            var button = window.FindControl<Button>("OpenOnCurrentModelButton")!;
            await Until(() => button.IsEffectivelyVisible);
            Assert.Equal(Loc.T("Open on current model"), button.Content);
            Assert.Same(vm.OpenOnCurrentModelCommand, button.Command);
            vm.ComposerText = "keep this unsent draft";

            await vm.RecoverCommand.ExecuteAsync(null);
            await Until(() => vm.CanOpenOnCurrentModel);
            Assert.Equal(file, controller.CurrentLaunch.ResumeSessionFile);
            Assert.Equal(original, File.ReadAllText(file));
            Assert.Equal("keep this unsent draft", vm.ComposerText);
            Assert.False(File.Exists(inputLog));

            await vm.OpenOnCurrentModelCommand.ExecuteAsync(null);
            await Until(() => vm.Phase == SessionPhase.Ready);
            Assert.Equal(file, controller.SessionFile);
            Assert.Equal("fake/model", controller.Snapshot().Model);
            Assert.Equal("keep this unsent draft", vm.ComposerText);
            Assert.Equal(["saved question"], controller.Snapshot().Items.OfType<UserItem>().Select(item => item.Text));
            Assert.False(File.Exists(inputLog));
            Assert.False(controller.CurrentLaunch.ResumeOnCurrentModel);
            var opens = File.ReadAllLines(launchLog).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            Assert.Equal(3, opens.Length);
            Assert.All(opens.Take(2), open => Assert.False(open.TryGetProperty("resumeOnCurrentModel", out _)));
            Assert.True(opens[2].GetProperty("resumeOnCurrentModel").GetBoolean());
            Assert.All(opens, open => Assert.Contains(file, open.GetProperty("arguments").EnumerateArray().Select(arg => arg.GetString())));
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Failed_reopen_keeps_loaded_history_draft_and_queue_without_automatic_submission()
    {
        var root = TestProcesses.TempDir("model-reopen-ui");
        var file = Saved(root);
        var missing = false;
        var inputLog = Path.Combine(root, "input.log");
        var controller = new SessionController(request =>
            TestProcesses.FakeFactory(missing ? "missing-saved-model" : "queue-stream-99", root)(request) with
            {
                Environment = new Dictionary<string, string?> { ["FAKE_SESSION_DIR"] = root, ["FAKE_OMP_INPUT_LOG"] = inputLog },
            }, new LaunchRequest(root, file));
        var vm = new MainViewModel(controller, new AppArgs());
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 720 };
        window.Show();
        vm.OnWindowOpened();
        try
        {
            await Until(() => vm.Phase == SessionPhase.Ready);
            await controller.PromptAsync("intentional running question", []);
            await Until(() => vm.Phase == SessionPhase.Running);
            await controller.QueueAsync(QueueKind.FollowUp, "unsent queued message", []);
            vm.ComposerText = "unsent draft";
            missing = true;
            await controller.RestartAsync(new LaunchRequest(root, file), force: true);
            await Until(() => vm.CanOpenOnCurrentModel);
            Assert.Contains(vm.Rows.OfType<UserRowViewModel>(), row => row.Text == "saved question");
            Assert.True(vm.ShowRecoverCard);
            await Until(() => vm.ComposerText.Contains("unsent queued message", StringComparison.Ordinal));
            Assert.Contains("unsent draft", vm.ComposerText);
            await vm.OpenOnCurrentModelCommand.ExecuteAsync(null);
            await Until(() => vm.Phase == SessionPhase.Ready);
            Assert.Contains("unsent queued message", vm.ComposerText);
            Assert.Contains("unsent draft", vm.ComposerText);
            Assert.DoesNotContain(controller.Snapshot().Items.OfType<UserItem>(), item => item.Text == "unsent queued message");
            Assert.False(File.Exists(inputLog));
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Sidebar_missing_model_keeps_its_failed_chat_for_recovery_and_leaves_peer_draft_alone()
    {
        var root = TestProcesses.TempDir("model-sidebar-ui");
        var original = Saved(root);
        var target = ChatScrollTests.WriteSession(root, root, "Unavailable saved model",
            ChatScrollTests.LongConversation(1));
        var controller = new SessionController(request =>
            TestProcesses.FakeFactory(request.ResumeSessionFile == target ? "missing-saved-model" : "normal", root)(request),
            new LaunchRequest(root, original));
        var vm = new MainViewModel(controller, new AppArgs());
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 720 };
        window.Show();
        vm.OnWindowOpened();
        try
        {
            await Until(() => vm.Phase == SessionPhase.Ready);
            vm.ComposerText = "peer draft";
            vm.RequestCatalogRefresh();
            await Until(() => vm.Sessions.Any(row => row.Model.Path == target));
            await vm.OpenSessionCommand.ExecuteAsync(vm.Sessions.First(row => row.Model.Path == target));
            await Until(() => vm.CanOpenOnCurrentModel);
            var failed = vm.Session;
            Assert.NotSame(controller, failed);
            Assert.Equal(target, failed.CurrentLaunch.ResumeSessionFile);
            Assert.Contains(failed, vm.OpenChats);
            Assert.Equal(SessionPhase.Ready, controller.Snapshot().Phase);
            Assert.NotEqual("open-failed", vm.SessionCard?.Kind);
            vm.ComposerText = "failed chat draft";

            await vm.OpenSessionCommand.ExecuteAsync(vm.Sessions.First(row => row.Model.Path == original));
            Assert.Same(controller, vm.Session);
            Assert.Equal("peer draft", vm.ComposerText);
            await vm.OpenSessionCommand.ExecuteAsync(vm.Sessions.First(row => row.Model.Path == target));
            Assert.Same(failed, vm.Session);
            Assert.Equal("failed chat draft", vm.ComposerText);
            await vm.OpenOnCurrentModelCommand.ExecuteAsync(null);
            await Until(() => vm.Phase == SessionPhase.Ready);
            Assert.Equal(target, failed.SessionFile);
            Assert.Equal(controller.ProcessId, failed.ProcessId);
            Assert.Equal("failed chat draft", vm.ComposerText);
            Assert.DoesNotContain(failed.Snapshot().Items.OfType<UserItem>(), item => item.Text == "failed chat draft");
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }
}
