#if OMBRA_FEATURE_SMOKE
using System.Diagnostics;
using Ombra.App.ViewModels;
using Ombra.ClientCore;
using Ombra.ClientCore.Browser;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] AgentBrowserSections() => [("agent-browser", AgentBrowser)];

        /// <summary>
        /// omp's browser tool from the agent's <c>eval</c> (BROWSERCALL in tools/mock-model: <c>browser.open</c>), served by
        /// the app's browser over the backend <c>OMBRA_AGENT_BROWSER</c> picks (tern: TernHost, the built-in preview's
        /// tabs). Every approval it asks (the eval, then each browser call in it) is answered with its card's Allow button.
        /// The open is timed from the answer just before the page showed up loaded in the Browser panel, the eval from the
        /// last answer to its end: both well inside omp's 10 s wait for a tab's load (TernHost: the open's page loads
        /// through the block, so omp hears <c>loaded</c>).
        /// </summary>
        private async Task AgentBrowser()
        {
            var port = Environment.GetEnvironmentVariable("OMBRA_FEATURE_SMOKE_MOCKPORT");
            if (!Check("agent-browser-mock-port", port is { Length: > 0 })) return;
            var url = $"http://127.0.0.1:{port}/v1/models?agent-browser";
            vm.SetAgentBrowserEngineCommand.Execute("preview");
            Log($"agent browser backend={AgentBrowserDefaults.ChooseBackend()} engine={vm.AgentBrowserEngine}");
            var before = vm.Preview.Tabs.Where(t => t.IsAgentTab).ToHashSet();
            IEnumerable<ToolItem> Evals() => vm.Session.Snapshot().Items.OfType<ToolItem>().Where(t => t.Name == "eval");
            var evalsBefore = Evals().Count();
            ToolItem? Eval() => Evals().Skip(evalsBefore).LastOrDefault();
            PreviewTab? Opened() => vm.Preview.Tabs.FirstOrDefault(t => t.IsAgentTab && !before.Contains(t)
                && t.Url?.AbsoluteUri.Contains("agent-browser", StringComparison.Ordinal) == true && !t.IsLoading);
            var clock = Stopwatch.StartNew();
            TimeSpan? opened = null, evalDone = null;
            // Watched apart from the answering below, which waits on each card for a while
            using var stop = new CancellationTokenSource();
            var watch = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (opened is null && Opened() is not null) opened = clock.Elapsed;
                        if (evalDone is null && Eval() is { Status: not ToolStatus.Running }) evalDone = clock.Elapsed;
                    });
                    await Task.Delay(20);
                }
            });
            await Send("BROWSERCALL " + url);

            var answered = new List<(string Headline, TimeSpan At)>();
            var end = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < end && (evalDone is null || vm.IsRunning))
            {
                if (vm.CurrentDialog is { IsApproval: true } d)
                {
                    var n = answered.Count + 1;
                    await Task.Delay(300);
                    Shot($"60-agent-browser-approval-{n}");
                    var allow = ButtonFor(d.AllowCommand);
                    if (!Check($"agent-browser-approval-{n}-allow", allow is not null, d.Headline)) break;
                    await ClickSettled(allow!);
                    var at = clock.Elapsed;
                    Check($"agent-browser-approval-{n}-answered", await Until(() => !ReferenceEquals(vm.CurrentDialog, d), 10), d.Headline);
                    answered.Add((d.Headline, at));
                    continue;
                }
                await Task.Delay(50);
            }
            await stop.CancelAsync();
            await watch;
            var tool = Eval();
            var final = vm.Session.Snapshot().Items.OfType<AssistantItem>().LastOrDefault(a => a.Text.Contains("BROWSERCALL done", StringComparison.Ordinal))?.Text ?? "";
            // From the answer that last let omp go on before the moment (the message when nothing asked)
            TimeSpan? Since(TimeSpan? at) => at is { } t ? t - answered.Where(a => a.At <= t).Select(a => a.At).DefaultIfEmpty(TimeSpan.Zero).Max() : null;
            var openLatency = Since(opened);
            var evalLatency = Since(evalDone);
            var approvals = answered.Select(a => $"{a.Headline} @{a.At.TotalMilliseconds:0}").ToList();
            Log($"agent-browser approvals={answered.Count} [{string.Join(" | ", approvals)}] opened @{opened?.TotalMilliseconds:0} ({openLatency?.TotalMilliseconds:0} ms after its answer) "
                + $"eval done @{evalDone?.TotalMilliseconds:0} ({evalLatency?.TotalMilliseconds:0} ms after the last answer) took={tool?.Took?.TotalMilliseconds:0}ms");
            Check("agent-browser-approvals", answered.Count >= 2 && answered[0].Headline.Contains("eval", StringComparison.Ordinal)
                && answered.Skip(1).Any(a => a.Headline.Contains("browser", StringComparison.Ordinal)), $"{answered.Count}: [{string.Join(" | ", approvals)}]");
            Check("agent-browser-tab-opened", openLatency is { } open && open < TimeSpan.FromSeconds(10),
                $"{openLatency?.TotalMilliseconds:0} ms after its answer; tabs=[{string.Join(", ", vm.Preview.Tabs.Select(t => $"{t.Id}:{t.Url}:{(t.IsLoading ? "loading" : "loaded")}"))}]");
            Check("agent-browser-eval-succeeded", tool is { Status: ToolStatus.Succeeded } && evalLatency is { } done && done < TimeSpan.FromSeconds(10)
                && tool.Output?.Contains("timed out", StringComparison.OrdinalIgnoreCase) != true,
                $"status={tool?.Status} {evalLatency?.TotalMilliseconds:0} ms after the last answer output='{tool?.Output?.ReplaceLineEndings(" ")}'");
            Check("agent-browser-reply", final.Contains("agent-browser", StringComparison.Ordinal), $"'{final.ReplaceLineEndings(" ")}'");
            // omp named the chat that opened the page (open_split's session_id), and as it is the one shown the page came
            // forward
            var page = Opened();
            Check("agent-browser-owner", page is not null && page.OwnerSessionId is { Length: > 0 } owner && owner == vm.Session.SessionId,
                $"owner={page?.OwnerSessionId} chat={vm.Session.SessionId}");
            Check("agent-browser-shown", page is not null && vm.Preview.ActiveTab == page && vm.IsPreviewOpen, $"active={vm.Preview.ActiveTab.Id}");

            await Task.Delay(500);
            Shot("61-agent-browser-opened");

            // The user turns to Files: the agent's next page (the same named tab, or a new one) loads behind, Files stays
            // on screen and nothing switches
            vm.ShowPaneCommand.Execute(SidePane.Files);
            var active = vm.Preview.ActiveTab;
            var switched = false;
            void Watch(object? s, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName is nameof(MainViewModel.ActivePane) && vm.ActivePane != SidePane.Files) switched = true;
            }
            vm.PropertyChanged += Watch;
            evalsBefore = Evals().Count();
            await Send("BROWSERCALL " + url + "&behind");
            var behindEnd = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < behindEnd && (Eval() is null or { Status: ToolStatus.Running } || vm.IsRunning))
            {
                // its cards were clicked above; here only where the page goes matters
                if (vm.CurrentDialog is { IsApproval: true } d && d.AllowCommand.CanExecute(null)) d.AllowCommand.Execute(null);
                await Task.Delay(50);
            }
            vm.PropertyChanged -= Watch;
            Shot("62-agent-browser-behind");
            Check("agent-browser-behind", Eval() is { Status: ToolStatus.Succeeded }
                && vm.Preview.Tabs.Any(t => t.IsAgentTab && t.Url?.AbsoluteUri.Contains("behind", StringComparison.Ordinal) == true)
                && vm.ActivePane == SidePane.Files && !switched && vm.Preview.ActiveTab == active,
                $"eval={Eval()?.Status} pane={vm.ActivePane} switched={switched} active={vm.Preview.ActiveTab.Id} was={active.Id} "
                + $"tabs=[{string.Join(", ", vm.Preview.Tabs.Select(t => $"{t.Id}:{t.Url}"))}]");
        }
    }
}
#endif
