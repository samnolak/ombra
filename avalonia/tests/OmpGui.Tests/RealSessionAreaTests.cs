using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// The Session area against the real omp (OMPGUI_TEST_CONFIG): what its builtins print is what the parsers read, and
/// the window's session menu, context ring and model options work on it. Read-only: nothing here changes omp's
/// settings (the setters are checked on the fake; the real omp's answers to them are in docs/PARITY.md).
/// </summary>
public sealed class RealSessionAreaTests(ITestOutputHelper log)
{
    [Fact]
    public async Task Real_omp_prints_what_the_session_area_reads()
    {
        if (TestProcesses.RealOmp() is not { } options)
        {
            Assert.Skip("OMPGUI_TEST_CONFIG is not set");
            return;
        }
        await using var s = new SessionController(options.ToLaunchSpec, new LaunchRequest(options.WorkingDirectory), TimeSpan.FromSeconds(90));
        await s.StartAsync();
        async Task<string> Run(string c)
        {
            var r = await s.RunSlashCommandAsync(c);
            log.WriteLine($"=== {c}  ok={r.Ok} agent={r.AgentInvoked} error={r.Error}\n{r.Output}\n");
            Assert.True(r.Ok, $"{c}: {r.Error}");
            return r.Output;
        }

        var context = SessionOutputs.ParseContext(await Run("/context"));
        Assert.NotNull(context);
        Assert.True(context.Unavailable is not null || context is { Window: > 0, Categories.Count: > 0 });
        Assert.NotNull(SessionOutputs.ParseUsage(await Run("/usage")));
        Assert.NotNull(SessionOutputs.ParseOnOff(await Run("/fast status"), "Fast mode"));
        Assert.NotNull(SessionOutputs.ParseOnOff(await Run("/extended-context status"), "Extended context"));
        Assert.NotEqual(AdvisorState.Unknown, SessionOutputs.ParseAdvisor(await Run("/advisor status")));
        Assert.NotNull(SessionOutputs.ParseDirectories(await Run("/dirs")).WorkingDirectory);
        Assert.StartsWith("Fresh provider session started", await Run("/fresh"));
        Assert.Equal("Nothing to retry.", await Run("/retry"));
        var memory = await Run("/memory stats");
        Assert.False(string.IsNullOrWhiteSpace(memory));

        var state = await s.GetOptionsStateAsync();
        Assert.NotNull(state);
        log.WriteLine($"get_state options: {state}");
        Assert.True(state.ContextWindow > 0);

        // /compact answers at once and prints its result later (a new session has nothing to compact)
        var before = s.Snapshot();
        var compact = await s.RunSlashCommandAsync("/compact");
        Assert.True(compact.Ok, compact.Error);
        var end = SessionOutputs.ParseCompactEnd(compact.Output)
                  ?? SessionOutputs.ParseCompactEnd(await s.WaitForCommandOutputAsync(before, t => SessionOutputs.ParseCompactEnd(t) is not null, TimeSpan.FromSeconds(60)) ?? "");
        log.WriteLine($"=== /compact → {end}");
        Assert.NotNull(end);

        // !command: RPC bash, the output as a row
        Assert.True(await s.RunShellCommandAsync("echo parity-check"));
        Assert.Contains("parity-check", s.Snapshot().Items.OfType<ToolItem>().Single(t => t.Name == "shell").Output);

        // /export writes into omp's working directory (removed again)
        var path = SessionOutputs.ParseExportPath(await Run("/export"));
        Assert.NotNull(path);
        var file = Path.IsPathRooted(path) ? path : Path.Combine(options.WorkingDirectory!, path);
        Assert.True(File.Exists(file), file);
        File.Delete(file);
    }

    [AvaloniaFact]
    public async Task The_window_reads_real_omps_context_usage_and_model_options()
    {
        if (TestProcesses.RealOmp() is not { } options)
        {
            Assert.Skip("OMPGUI_TEST_CONFIG is not set");
            return;
        }
        var s = new SessionController(options.ToLaunchSpec, new LaunchRequest(options.WorkingDirectory), TimeSpan.FromSeconds(90));
        var vm = new MainViewModel(s, new AppArgs()) { OmpCliLaunch = (a, d) => options.ToCliLaunchSpec(a, d) };
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, 120);
        await Until(() => vm.Usage.HasPercent, 30);
        log.WriteLine($"ring: {vm.Usage.Percent:0.0}% ({vm.Usage.RingLevel})");

        vm.Usage.IsOpen = true;
        await Until(() => !vm.Usage.IsLoading && (vm.Usage.HasContext || vm.Usage.ContextNote.Length > 0), 60);
        log.WriteLine($"{vm.Usage.ContextHeadline}\n" + string.Join("\n", vm.Usage.ContextRows.Select(r => $"  {r.Label}: {r.Tokens} ({r.Percent})")) +
                      "\n" + string.Join("\n", vm.Usage.Totals.Select(t => $"  {t.Label}: {t.Value}")));
        Assert.True(vm.Usage.HasContext);
        Assert.True(vm.Usage.HasTotals || vm.Usage.HasLimits);
        vm.Usage.IsOpen = false;

        await vm.OpenModelMenuCommand.ExecuteAsync(null);
        await Until(() => vm.ModelOptions.IsLoaded && !vm.ModelOptions.IsLoading, 60);
        log.WriteLine($"options: fast={vm.ModelOptions.FastMode} extended={vm.ModelOptions.ExtendedContext} advisor={vm.ModelOptions.Advisor} " +
                      $"({vm.ModelOptions.AdvisorNote}) autoCompact={vm.ModelOptions.AutoCompaction} autoRetry={vm.ModelOptions.AutoRetry} known={vm.ModelOptions.AutoRetryKnown}");
        Assert.True(vm.ModelOptions.AutoRetryKnown);
        vm.IsModelMenuOpen = false;

        await vm.FreshProviderSessionCommand.ExecuteAsync(null);
        Assert.Equal("Fresh provider session", vm.SessionCard!.Title);
        await vm.WorkspaceFoldersCommand.ExecuteAsync(null);
        Assert.Equal(options.WorkingDirectory, vm.SessionCard!.Folders.Single().Path);
        await vm.ExportHtmlCommand.ExecuteAsync(null);
        Assert.Equal("Exported as HTML", vm.SessionCard!.Title);
        Assert.True(File.Exists(vm.SessionCard.Detail), vm.SessionCard.Detail);
        File.Delete(vm.SessionCard.Detail);

        // A terminal-only command stays in the window
        vm.ComposerText = "/plan make a plan";
        vm.SendCommand.Execute(null);
        Assert.Equal("terminal", vm.SessionCard!.Kind);
        Assert.Empty(vm.Rows.OfType<UserRowViewModel>());
        await vm.DisposeAsync();
        w.Close();
    }

    private static async Task Until(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("never reached");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
