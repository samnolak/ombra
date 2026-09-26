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
/// REAL MODEL E2E: the real window, typed on the keyboard → the Original OMP Core (omp 18.2.0 over its RPC) → omp's own
/// provider path (models.yml, openai-completions) → a real language model. Runs only when OMPGUI_REAL_MODEL_CONFIG names
/// a runtime options file whose omp profile routes to a real model and OMPGUI_REAL_MODEL_ROUTE is that route
/// (e.g. local-llamacpp/smollm2-135m-instruct-q4_1). Never the stand-in: a route containing "flash-next" or served by
/// tools/mock-model is refused. The model's words are not asserted (a real model says what it says); what is asserted is
/// the path: streamed deltas, a completed message, an abort that stops the stream, recovery, and a tool call that only
/// runs after the user allows it. Each step's outcome is appended to OMPGUI_REAL_MODEL_REPORT when set.
/// </summary>
public sealed class RealModelTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMPGUI_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static (OmpRuntimeOptions Options, string Route)? Config()
    {
        var path = Environment.GetEnvironmentVariable("OMPGUI_REAL_MODEL_CONFIG");
        var route = Environment.GetEnvironmentVariable("OMPGUI_REAL_MODEL_ROUTE");
        if (path is null || route is null || !File.Exists(path)) return null;
        Assert.DoesNotContain("flash-next", route, StringComparison.OrdinalIgnoreCase); // the stand-in's route name
        return (OmpRuntimeOptions.Load(path), route);
    }

    private static void Report(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        if (Environment.GetEnvironmentVariable("OMPGUI_REAL_MODEL_REPORT") is { Length: > 0 } file)
            File.AppendAllText(file, $"{DateTimeOffset.UtcNow:O} {line}\n");
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 300)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Shot(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ShotDir);
        using var frame = w.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(ShotDir, name + ".png"));
        frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    /// <summary>A failure message with the transcript and omp's recent events, so an intermittent failure explains itself.</summary>
    private static string Describe(SessionSnapshot s, string what) =>
        what + "\nitems:\n" + string.Join("\n", s.Items.Select(i => i switch
        {
            AssistantItem a => $"  assistant #{a.Key} streaming={a.Streaming} stop={a.StopReason} error={a.Error} text={Trim(a.Text)}",
            UserItem u => $"  user #{u.Key} {Trim(u.Text)}",
            _ => $"  {i.GetType().Name} #{i.Key}",
        })) + "\nevents:\n" + string.Join("\n", s.DebugTail.TakeLast(60).Select(d => $"  {d.At:HH:mm:ss.fff} {d.Type} {d.Summary}"));

    private static void Type(Window w, string text)
    {
        w.KeyTextInput(text);
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
    }

    private static Button Button(Window w, string name) =>
        w.GetVisualDescendants().OfType<Button>().First(b => b.Name == name && b.IsEffectivelyVisible);

    private sealed record Ui(SessionController Session, MainViewModel Vm, Window Window, string Route) : IAsyncDisposable
    {
        public SessionSnapshot Snap => Session.Snapshot();

        /// <summary>Types <paramref name="text"/>, samples the reply while it streams, and waits for the run to end.</summary>
        public async Task<(AssistantItem Reply, int Samples, long Frames)> RunAsync(string text, int seconds = 600)
        {
            var before = Snap.MessagesCompleted;
            var framesBefore = Snap.FramesReceived;
            Type(Window, text);
            var lengths = new HashSet<int>();
            await Until(() =>
            {
                if (Vm.IsRunning && Vm.Rows.OfType<AssistantRowViewModel>().LastOrDefault() is { Text.Length: > 0 } a) lengths.Add(a.Text.Length);
                return Vm.Phase == SessionPhase.Ready && Snap.MessagesCompleted > before;
            }, "reply to: " + text, seconds);
            var reply = Snap.Items.OfType<AssistantItem>().Last();
            return (reply, lengths.Count, Snap.FramesReceived - framesBefore);
        }

        public async ValueTask DisposeAsync()
        {
            var omp = Session.ProcessId;
            await Vm.DisposeAsync();
            Window.Close();
            if (omp is { } pid)
                await Until(() => { try { return System.Diagnostics.Process.GetProcessById(pid).HasExited; } catch (ArgumentException) { return true; } }, "omp exited", 30);
        }
    }

    private static async Task<Ui?> StartAsync(string name)
    {
        if (Config() is not { } c)
        {
            Assert.Skip("OMPGUI_REAL_MODEL_CONFIG / OMPGUI_REAL_MODEL_ROUTE are not set (no real model)");
            return null;
        }
        var options = c.Options;
        var session = new SessionController(r => options.ToLaunchSpec(r), new LaunchRequest(options.WorkingDirectory, ApprovalMode: "write"), TimeSpan.FromSeconds(180));
        var vm = new MainViewModel(session, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1200, Height = 820 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "omp ready", 180);
        Assert.Equal(c.Route, session.Snapshot().Model);
        Assert.Equal("Accept edits", vm.ApprovalLabel);
        Report($"[{name}] omp {session.ProcessId} ready, model {session.Snapshot().Model}, protocol v{session.Snapshot().ProtocolVersion}");
        return new Ui(session, vm, w, c.Route);
    }

    [AvaloniaFact]
    public async Task A_prompt_streams_and_completes_then_abort_stops_a_reply_and_the_session_recovers()
    {
        await using var ui = await StartAsync("stream");

        // 1. Prompt → streamed reply → completed message.
        var (reply, samples, frames) = await ui!.RunAsync("Reply with one short sentence about the sea.");
        Report($"[stream] reply: {reply.Text.Length} chars, stop reason {reply.StopReason}, {frames} RPC frames during the run, {samples} distinct reply lengths shown while streaming, error {reply.Error ?? "none"}");
        Report($"[stream] reply text: {Trim(reply.Text)}");
        Assert.True(reply.Text.Length > 0, "the model returned no text");
        Assert.Null(reply.Error);
        Assert.Contains(reply.StopReason, new[] { "stop", "length" });
        Assert.True(samples >= 2, $"expected the window to show the reply growing while it streamed, saw {samples} length(s) in {frames} frames");
        Assert.Equal(0, ui.Snap.StreamMismatches);
        Shot(ui.Window, "real-model-reply");

        // 2. A long answer, stopped with the Stop button while it streams.
        var repliesBefore = ui.Snap.Items.OfType<AssistantItem>().Count();
        Type(ui.Window, "Write a very long story, at least two thousand words, about a lighthouse keeper and a storm.");
        // The new reply's own row, streaming (not the previous reply's row).
        await Until(() => ui.Vm.IsRunning && ui.Snap.Items.OfType<AssistantItem>().Skip(repliesBefore).LastOrDefault() is { Streaming: true, Text.Length: > 40 },
            "long reply streaming");
        var lengthAtStop = ui.Snap.Items.OfType<AssistantItem>().Last().Text.Length;
        Shot(ui.Window, "real-model-streaming");
        Button(ui.Window, "StopButton").Command!.Execute(null);
        await Until(() => ui.Vm.Phase == SessionPhase.Ready, "abort settles", 60);
        var stopped = ui.Snap.Items.OfType<AssistantItem>().Last();
        Report($"[abort] stopped at {stopped.Text.Length} chars (was {lengthAtStop} when Stop was pressed), stop reason {stopped.StopReason}");
        Assert.True(repliesBefore + 1 == ui.Snap.Items.OfType<AssistantItem>().Count(), Describe(ui.Snap, $"expected {repliesBefore + 1} assistant replies"));
        Assert.False(stopped.Streaming);
        Assert.Equal("aborted", stopped.StopReason);
        Assert.Equal("Interrupted", ui.Vm.Rows.OfType<AssistantRowViewModel>().Last().Footer); // the user's stop, not an error
        Assert.True(stopped.Text.Length >= lengthAtStop);
        await Task.Delay(3000); // nothing more arrives after the stop
        Assert.Equal(stopped.Text.Length, ui.Snap.Items.OfType<AssistantItem>().Last().Text.Length);

        // 3. The session recovers: the next prompt gets a normal reply.
        var (again, _, againFrames) = await ui.RunAsync("Now say hello in five words.");
        Report($"[recover] reply: {again.Text.Length} chars, stop reason {again.StopReason}, {againFrames} RPC frames: {Trim(again.Text)}");
        Assert.True(again.Text.Length > 0);
        Assert.Contains(again.StopReason, new[] { "stop", "length" });
        Shot(ui.Window, "real-model-recovered");
    }

    [AvaloniaFact]
    public async Task The_model_is_discovered_through_omp_and_chosen_in_the_picker()
    {
        await using var ui = await StartAsync("discovery");
        var chip = ui!.Window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "ModelButton");
        chip.Command!.Execute(null);
        await Until(() => ui.Vm.IsModelMenuOpen && ui.Vm.Models.Count > 0, "model list from omp", 120);
        Report($"[discovery] omp lists {ui.Vm.Models.Count} model(s): {string.Join(", ", ui.Vm.Models.Take(12).Select(m => m.Key))}");
        Assert.Contains(ui.Vm.Models, m => m.Key == ui.Route);
        var filter = ui.Window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "ModelFilterBox");
        filter.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = ui.Route.Split('/').Last() });
        await Until(() => ui.Vm.Models.Count >= 1 && ui.Vm.Models[0].Key == ui.Route, "filtered to the route");
        filter.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Until(() => !ui.Vm.IsModelMenuOpen && ui.Vm.CurrentModel == ui.Route, "chosen", 60);
        Assert.Equal(ui.Route, ui.Snap.Model);
        var (reply, _, _) = await ui.RunAsync("Say hello in one short sentence.");
        Report($"[discovery] chosen {ui.Snap.Model}; reply {reply.Text.Length} chars, stop reason {reply.StopReason}");
        Assert.True(reply.Text.Length > 0);
        Assert.Null(reply.Error);
    }

    [AvaloniaFact]
    public async Task A_long_response_streams_to_the_end_without_loss()
    {
        await using var ui = await StartAsync("long");
        // What counts as long depends on the model and server: OMPGUI_REAL_MODEL_LONG_MIN_CHARS (default 400, which a
        // small model capped at 384 tokens reaches; the target gate asks for more).
        var min = int.TryParse(Environment.GetEnvironmentVariable("OMPGUI_REAL_MODEL_LONG_MIN_CHARS"), out var m) ? m : 400;
        var (reply, samples, frames) = await ui!.RunAsync("Write a long, detailed story of at least 800 words about a lighthouse keeper and a storm. Plain prose, no lists.");
        Report($"[long] reply: {reply.Text.Length} chars, stop reason {reply.StopReason}, {frames} RPC frames, {samples} distinct lengths shown, mismatches {ui.Snap.StreamMismatches}");
        Assert.Null(reply.Error);
        Assert.Contains(reply.StopReason, new[] { "stop", "length" });
        Assert.True(reply.Text.Length >= min, $"expected at least {min} chars, got {reply.Text.Length}");
        Assert.True(samples >= 10, $"expected the window to show the reply growing, saw {samples} lengths");
        Assert.Equal(0, ui.Snap.StreamMismatches);
        // The window shows omp's final text, not a truncated stream.
        await Until(() => ui.Vm.Rows.OfType<AssistantRowViewModel>().LastOrDefault()?.Text == reply.Text, "window shows the final text", 30);
        Shot(ui.Window, "real-model-long");
    }

    [AvaloniaFact]
    public async Task A_tool_call_of_the_model_runs_only_after_the_user_allows_it()
    {
        await using var ui = await StartAsync("tool");
        const string Command = "echo real-model-ok";
        var before = ui!.Snap.MessagesCompleted;
        Type(ui.Window, $"Use the bash tool to run exactly this command and nothing else: {Command}");
        // Ends with an approval card (bash asks in this approval mode), a tool that needs none (read, …), or plain text.
        await Until(() => ui.Vm.HasDialog || (ui.Vm.Phase == SessionPhase.Ready && ui.Snap.MessagesCompleted > before), "tool call or reply", 600);
        if (!ui.Vm.HasDialog)
        {
            var tools = ui.Snap.Items.OfType<ToolItem>().ToList();
            var text = ui.Snap.Items.OfType<AssistantItem>().LastOrDefault()?.Text ?? "";
            Report($"[tool] no approval requested; tool calls: [{string.Join(", ", tools.Select(t => $"{t.Name} {t.Status}"))}]; text: {Trim(text)}");
            Assert.All(tools, t => Assert.NotEqual("bash", t.Name)); // bash never runs without the card
            // The target model gate (tools/verify/target-model-gate.sh) requires tool calling: there a skip is a failure.
            if (Environment.GetEnvironmentVariable("OMPGUI_REAL_MODEL_REQUIRE_TOOLS") == "1")
                Assert.Fail("OMPGUI_REAL_MODEL_REQUIRE_TOOLS=1 and the model made no bash call: " + Trim(text));
            Assert.Skip(tools.Count == 0
                ? "the model answered in text and made no tool call: tool calling NOT SUPPORTED BY THIS MODEL in this run"
                : $"the model called {string.Join(", ", tools.Select(t => t.Name))} (no approval needed), not bash");
            return;
        }

        var dialog = ui.Snap.Dialogs[0];
        Report($"[tool] approval card: {dialog.Headline} | {Trim(dialog.Details)}");
        Assert.Equal(DialogKind.Approval, dialog.Kind);
        Assert.StartsWith("Allow tool:", dialog.Headline);
        Assert.DoesNotContain(ui.Snap.Items, i => i is ToolItem { Status: ToolStatus.Succeeded, Name: "bash" }); // nothing ran yet
        Shot(ui.Window, "real-model-approval");
        // Only the exact harmless command is allowed (omp words it "Command: …"); anything else the model made up is denied.
        var exact = dialog.Headline == "Allow tool: bash" && dialog.Details.Split('\n').Any(l => l.Trim() == "Command: " + Command);
        Button(ui.Window, exact ? "AllowButton" : "DenyButton").Command!.Execute(null);
        await Until(() => !ui.Vm.HasDialog && ui.Snap.Items.OfType<ToolItem>().Any(t => t.Status is ToolStatus.Succeeded or ToolStatus.Failed), "tool finished", 120);
        var tool = ui.Snap.Items.OfType<ToolItem>().Last(t => t.Status is ToolStatus.Succeeded or ToolStatus.Failed);
        Report($"[tool] {(exact ? "allowed" : "denied (not the exact command)")}: {tool.Name} {tool.Status}, output {Trim(tool.Output ?? "")}");
        if (exact)
        {
            Assert.Equal(ToolStatus.Succeeded, tool.Status);
            Assert.Contains("real-model-ok", tool.Output);
        }
        else
        {
            Assert.Equal(ToolStatus.Failed, tool.Status);
            Assert.Contains("denied", tool.Output, StringComparison.OrdinalIgnoreCase);
        }
        await Until(() => ui.Vm.Phase == SessionPhase.Ready || ui.Vm.HasDialog, "run ends after the tool", 600);
        if (ui.Vm.HasDialog) ui.Vm.AbortCommand.Execute(null); // a small model may keep asking: stop instead of answering blindly
        await Until(() => ui.Vm.Phase == SessionPhase.Ready, "settled", 120);
        Shot(ui.Window, "real-model-after-tool");
    }

    private static string Trim(string s) => (s.Length > 300 ? s[..300] + "…" : s).ReplaceLineEndings(" ⏎ ");
}
