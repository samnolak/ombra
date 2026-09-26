using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OmpGui.App.Services.Runtime;
using OmpGui.ClientCore;

namespace OmpGui.App.Services;

/// <summary>
/// <c>OmpGui --self-test report.json [--install-runtime]</c>: checks a packaged build without opening a window —
/// native libraries load (Skia, HarfBuzz, PTY, speech engine, audio), the pinned runtime is there (or is installed),
/// and the original omp starts from it over RPC and stops again with no process left behind. A clean machine without
/// a model provider passes when omp reports that (the first-run path the window guides through). Exit code 0 = pass.
/// </summary>
public static class SelfTest
{
    private sealed record Check(string Name, bool Ok, bool Required, string Detail);

    public static async Task<int> RunAsync(AppArgs args)
    {
        var checks = new List<Check>();
        var started = Stopwatch.GetTimestamp();

        Try(checks, "skia", true, () =>
        {
            using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(16, 16));
            surface.Canvas.Clear(SkiaSharp.SKColors.White);
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black };
            surface.Canvas.DrawRect(2, 2, 8, 8, paint);
            return "SkiaSharp " + typeof(SkiaSharp.SKSurface).Assembly.GetName().Version;
        });
        Try(checks, "harfbuzz", true, () =>
        {
            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf8("omp");
            return $"{buffer.Length} glyph slots";
        });
        await TryAsync(checks, "pty", true, PtyAsync).ConfigureAwait(false);
        Try(checks, "speech-engine", false, () =>
        {
            // The same resolution the engine's DllImport uses (runtimes/<rid>/native via the app's deps.json).
            if (!System.Runtime.InteropServices.NativeLibrary.TryLoad("sherpa-onnx-c-api", typeof(SherpaOnnx.OfflineRecognizer).Assembly, null, out var handle))
                throw new DllNotFoundException("sherpa-onnx-c-api could not be loaded");
            System.Runtime.InteropServices.NativeLibrary.Free(handle);
            return "sherpa-onnx native library loaded";
        });
        Try(checks, "audio", false, () =>
        {
            try
            {
                PortAudioSharp.PortAudio.Initialize();
                try { return $"PortAudio: {PortAudioSharp.PortAudio.DeviceCount} device(s), default input {(PortAudioSharp.PortAudio.DefaultInputDevice == PortAudioSharp.PortAudio.NoDevice ? "none" : "present")}"; }
                finally { PortAudioSharp.PortAudio.Terminate(); }
            }
            catch (Exception e) when (e is DllNotFoundException or TypeInitializationException)
            {
                // Dictation then records through a system recorder when there is one.
                if (Speech.CommandMicrophone.SystemRecorders() is { Count: > 0 } recorders)
                    return $"PortAudio not loadable ({ViewModels.MainViewModel.MicrophoneError(e)}); dictation records with {string.Join(" / ", recorders.Select(r => r.Name))}";
                throw new DllNotFoundException(ViewModels.MainViewModel.MicrophoneError(e) + " No system recorder (parecord, pw-record, arecord) either.", e);
            }
        });

        var platform = Platform.RuntimePlatform.Key(out var description);
        var installer = new RuntimeInstaller(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, RuntimePack.DefaultRoot, platform);
        InstalledRuntime? runtime = installer.FindInstalled();
        if (runtime is null && args.InstallRuntime)
            await TryAsync(checks, "runtime-install", true, async () =>
            {
                var t = Stopwatch.GetTimestamp();
                runtime = await installer.InstallAsync(null, CancellationToken.None).ConfigureAwait(false);
                return $"installed in {Stopwatch.GetElapsedTime(t).TotalSeconds:0} s to {runtime.Directory}";
            }).ConfigureAwait(false);
        checks.Add(new Check("runtime", runtime is not null || !args.InstallRuntime, args.InstallRuntime,
            runtime is not null ? $"{RuntimePack.Name} ({runtime.Platform}) at {runtime.Directory}" : $"not installed (platform {platform ?? "unsupported"}; {description})"));

        await TryAsync(checks, "omp", true, async () =>
        {
            var store = new ClientSettingsStore(args.ConfigPath ?? Environment.GetEnvironmentVariable(OmpRuntimeOptions.ConfigEnvVar) ?? OmpRuntimeOptions.DefaultConfigPath);
            var options = store.Load();
            if (runtime is not null) options = runtime.ApplyTo(options);
            var session = new SessionController(r => options.ToLaunchSpec(r), new LaunchRequest(options.WorkingDirectory, ApprovalMode: OmpRuntimeOptions.EffectiveApprovalMode(options.ApprovalMode)));
            int? pid = null;
            string outcome;
            try
            {
                await session.StartAsync().ConfigureAwait(false);
                pid = session.ProcessId;
                var snap = session.Snapshot();
                outcome = $"ready (protocol v{snap.ProtocolVersion}, model {snap.Model ?? "none"})";
            }
            catch (OmpGui.Rpc.OmpStartException) when (session.Snapshot().StartProblem == StartProblem.NoModel)
            {
                outcome = "started and reported no model provider (first-run setup path)";
            }
            await session.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            if (pid is { } p && Alive(p)) throw new InvalidOperationException($"omp (pid {p}) still running after stop");
            return outcome + (pid is null ? "" : "; stopped, no process left");
        }).ConfigureAwait(false);

        var pass = checks.Where(c => c.Required).All(c => c.Ok);
        var report = new
        {
            pass,
            version = typeof(SelfTest).Assembly.GetName().Version?.ToString(),
            os = description,
            platform,
            seconds = Math.Round(Stopwatch.GetElapsedTime(started).TotalSeconds, 1),
            paths = new
            {
                settings = args.ConfigPath ?? Environment.GetEnvironmentVariable(OmpRuntimeOptions.ConfigEnvVar) ?? OmpRuntimeOptions.DefaultConfigPath,
                defaultSettings = OmpRuntimeOptions.DefaultConfigPath,
                runtimes = RuntimePack.DefaultRoot,
                speechModel = Speech.SpeechModel.DefaultDirectory,
            },
            checks = checks.Select(c => new { c.Name, c.Ok, c.Required, c.Detail }),
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (args.SelfTestReport is { Length: > 0 } path) await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        Console.WriteLine(json);
        return pass ? 0 : 1;
    }

    private static void Try(List<Check> checks, string name, bool required, Func<string> check)
    {
        try { checks.Add(new Check(name, true, required, check())); }
        catch (Exception e) { checks.Add(new Check(name, false, required, e.GetType().Name + ": " + e.Message)); }
    }

    private static async Task TryAsync(List<Check> checks, string name, bool required, Func<Task<string>> check)
    {
        try { checks.Add(new Check(name, true, required, await check().ConfigureAwait(false))); }
        catch (Exception e) { checks.Add(new Check(name, false, required, e.GetType().Name + ": " + e.Message)); }
    }

    /// <summary>The terminal panel's PTY: the user's shell echoes a marker and exits.</summary>
    private static async Task<string> PtyAsync()
    {
        var (file, shellArgs) = ViewModels.MainViewModel.DefaultShell();
        var options = new Porta.Pty.PtyOptions
        {
            Name = "ompgui-self-test",
            Cols = 80,
            Rows = 24,
            Cwd = Path.GetTempPath(),
            App = file,
            CommandLine = shellArgs,
            Environment = new Dictionary<string, string>(),
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var pty = await Porta.Pty.PtyProvider.SpawnAsync(options, timeout.Token).ConfigureAwait(false);
        var input = Encoding.UTF8.GetBytes("echo ompgui-self-test-$((6*7))%OMPGUI_X%\r");
        await pty.WriterStream.WriteAsync(input, timeout.Token).ConfigureAwait(false);
        await pty.WriterStream.FlushAsync(timeout.Token).ConfigureAwait(false);
        var seen = new StringBuilder();
        var buffer = new byte[4096];
        while (!Echoed(seen.ToString()))
        {
            var n = await pty.ReaderStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (n == 0) break;
            seen.Append(Encoding.UTF8.GetString(buffer, 0, n));
        }
        pty.Kill();
        if (!Echoed(seen.ToString())) throw new InvalidOperationException("the shell ended without echoing: " + seen.ToString()[^Math.Min(200, seen.Length)..]);
        return $"{Path.GetFileName(file)} on a PTY (pid {pty.Pid}) echoed through the terminal";
    }

    /// <summary>A POSIX shell expands $((6*7)); cmd.exe prints the line with its output after the command echo.</summary>
    private static bool Echoed(string output) =>
        output.Contains("ompgui-self-test-42", StringComparison.Ordinal)
        || output.Split('\n').Count(l => l.Contains("ompgui-self-test-$((6*7))", StringComparison.Ordinal)) >= 2;

    private static bool Alive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }
}
