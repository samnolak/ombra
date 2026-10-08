using System.IO.Compression;
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
/// E2E acceptance: one daily-work session in the real window, typed on the keyboard, against the Original OMP Core
/// (omp 18.2.0 over its RPC). Runs when OMPGUI_TEST_CONFIG names a runtime (CI smoke: the stand-in model, whose
/// scripted keywords TOOLCALL / BASHCALL / SLOWSHORT drive tool calls; a real model would need other assertions).
/// </summary>
public sealed class AcceptanceTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMPGUI_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static async Task Until(Func<bool> condition, string what, int seconds = 120)
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

    private static void Type(Window w, string text, RawInputModifiers enter = RawInputModifiers.None)
    {
        w.KeyTextInput(text);
        w.KeyPress(Key.Enter, enter, PhysicalKey.Enter, null);
    }

    private static bool Alive(int pid)
    {
        try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    [AvaloniaFact]
    public async Task Daily_work_in_the_window_against_the_real_omp()
    {
        if (TestProcesses.RealOmp() is not { } options)
        {
            Assert.Skip("OMPGUI_TEST_CONFIG is not set");
            return;
        }
        // The client's own settings file, holding the runtime's environment (as a user's would): diagnostics must hide it.
        var store = new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("acceptance-settings"), "omp-gui.local.json"));
        store.Update(_ => options);
        var session = new SessionController(r => options.ToLaunchSpec(r), new LaunchRequest(options.WorkingDirectory, ApprovalMode: "write"), TimeSpan.FromSeconds(90));
        var vm = new MainViewModel(session, new AppArgs(), settings: store) { OmpTuiLaunch = dir => options.ToTuiLaunchSpec(dir) };
        var w = new MainWindow { DataContext = vm, Width = 1200, Height = 820 };
        w.Show();
        vm.OnWindowOpened();

        // 1. omp starts; the header shows omp's model and the ask-before-commands approval mode.
        await Until(() => vm.Phase == SessionPhase.Ready, "omp ready");
        Assert.False(string.IsNullOrEmpty(vm.CurrentModel));
        Assert.Equal("Accept edits", vm.ApprovalLabel);
        var omp = session.ProcessId!.Value;

        // 2. A prompt with a read tool call: streamed answer and a completed tool row.
        Type(w, "TOOLCALL please");
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<ToolRowViewModel>().Any(t => t.Name == "read"), "read tool done");
        Assert.Contains(vm.Rows.OfType<AssistantRowViewModel>(), a => a.Text.Length > 0);

        // 3. A shell command needs approval: the card shows it, Allow runs it.
        Type(w, "BASHCALL now");
        await Until(() => vm.HasDialog, "approval card");
        Assert.Contains("bash", vm.CurrentDialog!.Headline);
        Shot(w, "acceptance-approval");
        // The card's own button (it lives in the dialog's template, not the window's name scope).
        w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AllowButton" && b.IsEffectivelyVisible).Command!.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<ToolRowViewModel>().Any(t => t.Name == "bash" && !t.IsFailed && t.StatusText == "done"), "bash ran");

        // 4. While omp works, Enter queues a follow-up; it is delivered after the run.
        Type(w, "SLOWSHORT reply please");
        await Until(() => vm.IsRunning && vm.Rows.OfType<AssistantRowViewModel>().LastOrDefault()?.Text.Length > 10, "streaming");
        Type(w, "follow-up from the queue");
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<UserRowViewModel>().Any(u => u.Text == "follow-up from the queue") && vm.QueuedMessages.Count == 0, "follow-up delivered");

        // 5. omp's own slash command runs locally and prints its output.
        Type(w, "/context");
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<CommandOutputRowViewModel>().Any(), "command output");

        // 6. Rename, then a new session, then back to the renamed one from the sidebar.
        vm.StartRenameCommand.Execute(null);
        vm.RenameText = "Acceptance session";
        await vm.CommitRenameCommand.ExecuteAsync(null);
        await Until(() => vm.SessionTitle == "Acceptance session", "renamed");
        var first = session.Snapshot().SessionFile;
        vm.NewSessionCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready && session.Snapshot().SessionFile != first && !vm.Rows.OfType<UserRowViewModel>().Any(), "new session");
        Type(w, "hello from the second session");
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<AssistantRowViewModel>().Any(), "second session reply");
        await Until(() => vm.Sessions.Any(s => s.Title == "Acceptance session"), "sidebar lists the renamed session", 30);
        vm.OpenSessionCommand.Execute(vm.Sessions.First(s => s.Title == "Acceptance session"));
        await Until(() => session.Snapshot().SessionFile == first && vm.Rows.OfType<UserRowViewModel>().Any(u => u.Text == "TOOLCALL please"), "first session reopened");
        Shot(w, "acceptance-reopened");

        // 7. A shell in the terminal panel, in the project folder.
        vm.ToggleTerminalCommand.Execute(null);
        var tab = Assert.Single(vm.Terminals);
        var term = w.TerminalOf(tab)!;
        await Until(() => term.IsLive || tab.HasExited, "shell started", 30);
        Assert.False(tab.HasExited, tab.Status);
        var shell = term.Pid;
        await term.SendInputAsync("echo acceptance-terminal-ok\r", CancellationToken.None);
        await Until(() => term.FindInBuffer("acceptance-terminal-ok", default) >= 2, "shell echoed", 30);

        // 8. Diagnostics: saved where the user chooses, without the runtime's environment values.
        var saved = new MemoryStream();
        var copy = new MemoryStream();
        vm.SaveFileRequested += _ => Task.FromResult<Stream?>(new NonClosing(saved, copy));
        await vm.SaveDiagnosticsCommand.ExecuteAsync(null);
        using (var zip = new ZipArchive(new MemoryStream(copy.ToArray())))
        {
            var text = string.Join("\n", zip.Entries.Select(e => new StreamReader(e.Open()).ReadToEnd()));
            Assert.Contains("phase Ready", text);
            foreach (var value in options.Environment.Values.Where(v => v is { Length: >= 6 }))
                Assert.DoesNotContain(value!, text);
        }

        // 9. Closing the window asks first (the shell still runs), then ends omp and the shell: nothing is left behind.
        w.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("quit", vm.SessionCard?.Kind);
        vm.SessionCard!.Primary!.Command.Execute(null);
        await Until(() => !w.IsVisible, "window closed", 30);
        await Until(() => !Alive(omp) && !Alive(shell), "no process left", 30);
    }

    /// <summary>Records what is written; the bundle writer disposes its stream.</summary>
    private sealed class NonClosing(MemoryStream inner, MemoryStream copy) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); copy.Write(buffer, offset, count); }
    }
}
