using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Ombra.App;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

// Only the executable/runtime prefix is taken from --config. The profile and project are throwaway.
// Headless cannot certify native display cadence; native runs need a visible, awake display.
var configIndex = Array.IndexOf(args, "--config");
var workIndex = Array.IndexOf(args, "--work");
var secondsIndex = Array.IndexOf(args, "--seconds");
var modelIndex = Array.IndexOf(args, "--model-url");
var renderIndex = Array.IndexOf(args, "--render");
if (configIndex < 0 || configIndex + 1 >= args.Length || workIndex < 0 || workIndex + 1 >= args.Length ||
    modelIndex < 0 || modelIndex + 1 >= args.Length)
    throw new ArgumentException("Required: --config <runtime config> --model-url <stand-in http://127.0.0.1:port/v1> --work <scratch directory>; optional: --headless --seconds 1800 --render opengl|metal (macOS native, default opengl as the app)");
var modelUrl = new Uri(args[modelIndex + 1], UriKind.Absolute);
if (!modelUrl.IsLoopback || modelUrl.Scheme != "http" || modelUrl.Query.Length > 0 || modelUrl.Fragment.Length > 0)
    throw new ArgumentException("The stand-in model URL must be loopback HTTP, without a query or fragment.");
var seconds = secondsIndex < 0 ? 30 : int.Parse(args[secondsIndex + 1], CultureInfo.InvariantCulture);
if (seconds < 10) throw new ArgumentOutOfRangeException(nameof(seconds), "At least 10 seconds of streaming is required.");
// macOS backend: the app's own choice (Program.BuildAvaloniaApp) is OpenGL; --render metal measures the alternative.
var render = renderIndex < 0 ? "opengl" : args[renderIndex + 1];
if (render is not ("opengl" or "metal")) throw new ArgumentException("--render takes opengl or metal.");
var options = OmpRuntimeOptions.Load(args[configIndex + 1]);
var headless = args.Contains("--headless", StringComparer.Ordinal);
var root = Path.Combine(Path.GetFullPath(args[workIndex + 1]), "run-" + Guid.NewGuid().ToString("N"));
var builder = AppBuilder.Configure<App>();
if (headless) builder.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia();
else builder.UsePlatformDetect().With(new AvaloniaNativePlatformOptions
{
    RenderingMode = render == "metal" ? [AvaloniaNativeRenderingMode.Metal] : [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software],
    OverlayPopups = true,
});
builder.SetupWithoutStarting();
using var headlessClock = headless ? DispatcherTimer.Run(() =>
{
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    return true;
}, TimeSpan.FromMilliseconds(16)) : null;
using var stop = new CancellationTokenSource();
Dispatcher.UIThread.Post(async () =>
{
    try
    {
        Directory.CreateDirectory(root);
        using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) })
        {
            using var response = await http.GetAsync(modelUrl.AbsoluteUri.TrimEnd('/') + "/models");
            if (!response.IsSuccessStatusCode || !response.Headers.TryGetValues("x-ombra-stand-in", out var values) || !values.Contains("1"))
                throw new InvalidOperationException("The endpoint is not tools/mock-model/server.mjs; refusing to start an agent.");
        }
        var home = Path.Combine(root, "home");
        var project = Path.Combine(root, "project");
        var profile = Path.Combine(home, ".omp", "profiles", "performance", "agent");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(profile, "models.yml"), $"""
            providers:
              qwen-local:
                baseUrl: {JsonSerializer.Serialize(modelUrl.AbsoluteUri.TrimEnd('/'))}
                api: openai-completions
                apiKey: QWEN_LOCAL_API_KEY
                models:
                  - id: flash-next-w4a16
                    name: Performance stand-in (not a model)
                    reasoning: false
                    input: [text]
                    maxTokens: 8192
                    contextWindow: 131072
            """);
        for (var i = 0; i < 10; i++) File.WriteAllText(Path.Combine(project, $"notes-{i}.md"), $"Performance note {i}.\n");
        options = new OmpRuntimeOptions
        {
            Command = options.Command, PrefixArgs = options.PrefixArgs, Profile = "performance",
            Model = "qwen-local/flash-next-w4a16", WorkingDirectory = project, DefaultThinkingLevel = "off",
            Environment = new Dictionary<string, string?>
            {
                ["HOME"] = home, ["USERPROFILE"] = home, ["QWEN_LOCAL_API_KEY"] = "stand-in-only",
                ["AWS_ACCESS_KEY_ID"] = null, ["AWS_SECRET_ACCESS_KEY"] = null, ["AWS_SESSION_TOKEN"] = null
            }
        };
        var failures = new List<string>();
        var history = Path.Combine(root, "history.jsonl");
        using (var writer = new StreamWriter(history))
        {
            writer.WriteLine(JsonSerializer.Serialize(new { type = "session", version = 3, id = "performance", cwd = root, timestamp = "2026-01-01T00:00:00Z" }));
            for (var i = 0; i < 10_000; i++)
            {
                object message = i % 2 == 0
                    ? new { role = "user", content = $"Message {i}: explain the following change.", timestamp = i }
                    : new { role = "assistant", content = new[] { new { type = "text", text = $"Message {i}: **completed**.\n\n```csharp\nreturn {i};\n```\n\nThe change preserves existing behavior." } }, timestamp = i };
                writer.WriteLine(JsonSerializer.Serialize(new { type = "message", id = $"m{i}", parentId = i == 0 ? null : $"m{i - 1}",
                    timestamp = "2026-01-01T00:00:00Z", message }));
            }
        }
        // The first open also pays the engine process start and first-window JIT (reported, not gated); the gate is
        // the warm open a user gets when switching to a long chat while the app runs.
        foreach (var pass in new[] { "cold", "warm" })
        {
            var openTimer = Stopwatch.StartNew();
            var reader = new SessionController(r => options.ToLaunchSpec(r), new LaunchRequest(root, history) { ReadOnly = true });
            var historyVm = new MainViewModel(reader, new AppArgs(), settings: new ClientSettingsStore(Path.Combine(root, "history-settings.json")));
            var historyWindow = new MainWindow { DataContext = historyVm, Width = 1180, Height = 760 };
            try
            {
                historyWindow.Show();
                historyVm.OnWindowOpened();
                await Until(() =>
                {
                    var state = reader.Snapshot();
                    if (state.LastError is not null || state.History.Error is not null)
                        throw new InvalidOperationException(state.LastError ?? state.History.Error);
                    if (!state.History.IsLoading && state.History.TotalMessages > 0 && state.History.TotalMessages != 10_000)
                        throw new InvalidOperationException("Unexpected history: " + JsonSerializer.Serialize(state.History));
                    return historyVm.History.TotalMessages == 10_000 && !historyVm.History.IsLoading;
                }, "10k history", 60);
                historyWindow.UpdateLayout();
                await Frame(historyWindow);
                var openMs = openTimer.Elapsed.TotalMilliseconds;
                var snapshot = reader.Snapshot();
                if (snapshot.History.LoadedMessages != 100 || !snapshot.Items.OfType<AssistantItem>().Any(m => m.Text.StartsWith("Message 9999:", StringComparison.Ordinal)))
                    throw new InvalidOperationException("History did not hydrate the correct bounded tail.");
                var gated = pass == "warm";
                Console.WriteLine(JsonSerializer.Serialize(new { metric = "history-open", pass, messages = 10_000, loaded = snapshot.History.LoadedMessages, milliseconds = openMs, budgetMs = gated ? 1500 : (int?)null, headless }));
                if (gated) Capture(historyWindow, Path.Combine(root, "history.png"));
                if (gated && openMs >= 1500) failures.Add($"10k history open exceeded 1500ms: {openMs:F1}ms");
            }
            finally { await historyVm.DisposeAsync(); historyWindow.Close(); }
        }

        var controller = new SessionController(r => options.ToLaunchSpec(r), new LaunchRequest(options.WorkingDirectory, ApprovalMode: "yolo"));
        var vm = new MainViewModel(controller, new AppArgs(), settings: new ClientSettingsStore(Path.Combine(root, "stream-settings.json")));
        var window = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        var probe = new RenderProbe(seconds * 150) { Width = 4, Height = 4, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        var surface = (Control)window.Content!;
        window.Content = null;
        window.Content = new Grid { Children = { surface, probe } };
        try
        {
            window.Show();
            vm.OnWindowOpened();
            await Until(() => vm.Phase == SessionPhase.Ready, "runtime ready", 120);
            // Covers the probed interval, the unprobed one and the warm-up, with a margin.
            vm.ComposerText = $"LONGTASK {((2 * seconds + 120) / 60.0).ToString(CultureInfo.InvariantCulture)} id={Guid.NewGuid():N}";
            await vm.SendCommand.ExecuteAsync(null);
            await Until(() => controller.Snapshot().Items.OfType<AssistantItem>().Any(a => a.Text.Length > 0), "first streamed text", 60);
            // Warm caches before retained-memory drift; do not force collections in the measured interval.
            await Task.Delay(TimeSpan.FromSeconds(30));
            var start = controller.Snapshot();
            probe.Start();
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var initialRss = process.WorkingSet64;
            // macOS compresses idle pages, so its RSS swings by half within a minute while retained memory is flat:
            // there the gate reads phys_footprint (the OS's own accounting); RSS elsewhere.
            var initialFootprint = Footprint();
            var initialHeap = GC.GetTotalMemory(false);
            var callbackCount = 0;
            var sampling = true;
            var elapsed = Stopwatch.StartNew();
            var cpuStart = process.TotalProcessorTime;
            var lastFrames = start.FramesReceived;
            var noProgress = Stopwatch.StartNew();
            void OnFrame(TimeSpan _)
            {
                if (!sampling) return;
                callbackCount++;
                probe.InvalidateVisual();
                window.RequestAnimationFrame(OnFrame);
            }
            window.RequestAnimationFrame(OnFrame);
            for (var i = 0; i < seconds; i++)
            {
                await Task.Delay(1000);
                if (vm.Phase is SessionPhase.Faulted or SessionPhase.Stopped || !vm.IsRunning)
                    throw new InvalidOperationException($"Streaming stopped before the measurement ended: {vm.Phase}");
                var current = controller.Snapshot();
                if (current.FramesReceived != lastFrames) { lastFrames = current.FramesReceived; noProgress.Restart(); }
                else if (noProgress.Elapsed.TotalSeconds >= 5) throw new InvalidOperationException("Streaming stalled for five seconds.");
                // A trend, not two points: macOS compresses idle pages, so a single RSS pair cannot tell a leak from noise.
                if ((i + 1) % 60 == 0)
                {
                    process.Refresh();
                    Console.WriteLine(JsonSerializer.Serialize(new { metric = "memory-sample", minute = (i + 1) / 60, rssBytes = process.WorkingSet64,
                        footprintBytes = Footprint(), heapBytes = GC.GetTotalMemory(false), gen2 = GC.CollectionCount(2),
                        items = current.Items.Count, textChars = current.Items.OfType<AssistantItem>().Sum(a => (long)a.Text.Length) }));
                }
            }
            sampling = false;
            process.Refresh();
            var streamCpu = (process.TotalProcessorTime - cpuStart).TotalMilliseconds / 10 / elapsed.Elapsed.TotalSeconds;
            var final = controller.Snapshot();
            process.Refresh();
            var rss = process.WorkingSet64;
            var rssDrift = (rss - initialRss) / (double)initialRss;
            var finalFootprint = Footprint();
            var memoryMetric = initialFootprint is long f0 && finalFootprint is long f1 ? "footprint" : "rss";
            var drift = memoryMetric == "footprint" ? (finalFootprint!.Value - initialFootprint!.Value) / (double)initialFootprint.Value : rssDrift;
            var frames = probe.Stop();
            var fps = frames.Length / elapsed.Elapsed.TotalSeconds;
            Array.Sort(frames);
            var p95 = frames.Length == 0 ? 0 : frames[(int)((frames.Length - 1) * .95)];
            if (frames.Length == 0) failures.Add("The compositor produced no measured frames.");
            if (final.FramesReceived <= start.FramesReceived || final.StreamMismatches != 0)
                throw new InvalidOperationException("No streaming progress or a stream mismatch occurred.");
            Console.WriteLine(JsonSerializer.Serialize(new { metric = "stream", seconds = elapsed.Elapsed.TotalSeconds, headless, render,
                renderedFps = fps, callbackFps = callbackCount / elapsed.Elapsed.TotalSeconds, frameP95Ms = p95, rpcFrames = final.FramesReceived - start.FramesReceived,
                cpuPercentOfCore = streamCpu,
                initialRssBytes = initialRss, finalRssBytes = rss, rssDriftPercent = rssDrift * 100,
                initialFootprintBytes = initialFootprint, finalFootprintBytes = finalFootprint, memoryMetric, memoryDriftPercent = drift * 100,
                initialHeapBytes = initialHeap, finalHeapBytes = GC.GetTotalMemory(false),
                memoryGateExercised = seconds >= 1800, nativeCadenceGateExercised = !headless }));
            // The same stream without the probe forcing a frame per vsync: what the app itself costs while text streams.
            var plainStart = controller.Snapshot().FramesReceived;
            var plainCpu = await CpuOver(process, seconds);
            if (controller.Snapshot().FramesReceived <= plainStart || vm.Phase is SessionPhase.Faulted or SessionPhase.Stopped)
                throw new InvalidOperationException("Streaming ended before the unprobed interval did.");
            Console.WriteLine(JsonSerializer.Serialize(new { metric = "stream-unprobed", seconds, headless, render, cpuPercentOfCore = plainCpu }));
            Capture(window, Path.Combine(root, "stream.png"));
            await controller.AbortAsync();
            // Idle: the window shown, nothing streaming or animating (after the stop settles).
            await Until(() => !vm.IsRunning, "run stopped", 30);
            await Task.Delay(TimeSpan.FromSeconds(5));
            Console.WriteLine(JsonSerializer.Serialize(new { metric = "idle", seconds, headless, render, cpuPercentOfCore = await CpuOver(process, seconds) }));
            // 60Hz clocks have fractional sampling boundaries; tolerate one frame per second, not sustained stalls.
            if (!headless && fps < 59) failures.Add($"Native cadence missed 60Hz target: {fps:F2}fps");
            if (!headless && p95 > 20) failures.Add($"Native frame pacing exceeded 20ms p95: {p95:F2}ms");
            if (seconds >= 1800 && drift >= .05) failures.Add($"30-minute memory drift ({memoryMetric}) exceeded 5%: {drift:P2}");
        }
        finally { await vm.DisposeAsync(); window.Close(); }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine("PASS performance gates exercised above; short runs do not certify 30-minute memory stability.");
    }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    finally
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Scratch cleanup failed for {root}: {cleanup.Message}");
            Environment.ExitCode = 1;
        }
        finally { stop.Cancel(); }
    }
});
Dispatcher.UIThread.MainLoop(stop.Token);

static async Task Until(Func<bool> predicate, string label, int seconds)
{
    var timer = Stopwatch.StartNew();
    while (!predicate())
    {
        if (timer.Elapsed.TotalSeconds > seconds) throw new TimeoutException(label);
        await Task.Delay(10);
    }
}

// Process CPU over the next <seconds>, as percent of one core.
static async Task<double> CpuOver(Process process, int seconds)
{
    process.Refresh();
    var cpu = process.TotalProcessorTime;
    var timer = Stopwatch.StartNew();
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    process.Refresh();
    return (process.TotalProcessorTime - cpu).TotalMilliseconds / 10 / timer.Elapsed.TotalSeconds;
}

static Task Frame(Window window)
{
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    window.RequestAnimationFrame(_ => ready.TrySetResult());
    return ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
}

static void Capture(Window window, string path)
{
    using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
    bitmap.Render(window);
    bitmap.Save(path, new PngBitmapEncoderOptions());
}

// phys_footprint (what Activity Monitor shows as Memory), from proc_pid_rusage(RUSAGE_INFO_V2); null off macOS.
static long? Footprint()
{
    if (!OperatingSystem.IsMacOS()) return null;
    var buffer = new byte[512];
    return proc_pid_rusage(Environment.ProcessId, 2, buffer) == 0 ? BitConverter.ToInt64(buffer, 72) : null;
}

[System.Runtime.InteropServices.DllImport("libproc.dylib")]
static extern int proc_pid_rusage(int pid, int flavor, byte[] buffer);

// Counts actual compositor drawing, not just UI animation callbacks. This does not measure OS presentation latency.
sealed class RenderProbe(int capacity) : Control, ICustomDrawOperation
{
    private readonly object _gate = new();
    private readonly List<double> _intervals = new(capacity);
    private long _previous;
    private bool _sampling;
    public void Start() { lock (_gate) { _intervals.Clear(); _previous = 0; _sampling = true; } }
    public double[] Stop() { lock (_gate) { _sampling = false; return _intervals.ToArray(); } }
    public override void Render(DrawingContext context) => context.Custom(this);
    Rect ICustomDrawOperation.Bounds => new(0, 0, 4, 4);
    bool ICustomDrawOperation.HitTest(Point point) => false;
    bool IEquatable<ICustomDrawOperation>.Equals(ICustomDrawOperation? other) => false;
    void IDisposable.Dispose() { } // No native resources: the window owns this probe.
    void ICustomDrawOperation.Render(ImmediateDrawingContext context)
    {
        context.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, 4, 4));
        lock (_gate)
        {
            if (!_sampling) return;
            var now = Stopwatch.GetTimestamp();
            if (_previous != 0) _intervals.Add(Stopwatch.GetElapsedTime(_previous, now).TotalMilliseconds);
            _previous = now;
        }
    }
}
