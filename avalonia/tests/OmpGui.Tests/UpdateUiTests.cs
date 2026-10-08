using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OmpGui.App;
using OmpGui.App.Services;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// The update in the real window: the automatic check puts a notice in the sidebar, Settings → Updates downloads,
/// verifies and unpacks the new version next to the app, "Restart now" hands it to the apply script and quits, and
/// the script swaps it in. Every state is measured by <see cref="LayoutAudit"/> in both themes, wide and narrow; with
/// OMPGUI_REVIEW_DIR set the screenshots go to &lt;dir&gt;/updates.
/// </summary>
public sealed class UpdateUiTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("OMPGUI_REVIEW_DIR");
    private const string Feed = "https://updates.test/update.json";

    private sealed class Server(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static async Task Settle(int ms = 250)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    private static async Task Until(Func<bool> c, string what, int seconds = 30)
    {
        var d = DateTime.UtcNow.AddSeconds(seconds);
        while (!c())
        {
            if (DateTime.UtcNow > d) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static readonly StringBuilder Report = new();
    private static int _findings;

    private static void Take(Window w, string screen)
    {
        Dispatcher.UIThread.RunJobs();
        if (Dir is not null)
        {
            using var frame = w.CaptureRenderedFrame();
            Directory.CreateDirectory(Path.Combine(Dir, "updates"));
            using var f = File.Create(Path.Combine(Dir, "updates", screen + ".png"));
            frame?.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        var found = LayoutAudit.Run(w);
        _findings += found.Count;
        Report.Append(LayoutAudit.Report(screen, found));
    }

    private static void WriteApp(string dir, string version, string marker)
    {
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "OmpGui");
        File.WriteAllText(exe, $"#!/bin/sh\necho \"OMP GUI {version} linux-x64\"\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, (UnixFileMode)0b111_101_101);
        File.WriteAllText(Path.Combine(dir, "marker.txt"), marker);
    }

    private static byte[] Package(string version)
    {
        var src = TestProcesses.TempDir("upd-ui-src");
        WriteApp(Path.Combine(src, $"omp-gui-{version}-linux-x64"), version, "new " + version);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            TarFile.CreateFromDirectory(src, gz, includeBaseDirectory: false);
        return ms.ToArray();
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Check_install_and_restart_from_the_window(string theme)
    {
        if (OperatingSystem.IsWindows()) return; // the apply script run here is the POSIX one
        MainViewModel.ApplyTheme(theme);
        Report.Clear();
        _findings = 0;
        var package = Package("0.2.0");
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 2,
            version = "0.2.0",
            notes = "Faster start. Sessions load in the background.",
            runtime = new { pack = "omp-19.0.0-bun-1.5.0", omp = "19.0.0", bun = "1.5.0" },
            assets = new Dictionary<string, object>
            {
                ["linux-x64"] = new { file = "omp-gui-0.2.0-linux-x64.tar.gz", url = "https://updates.test/omp-gui-0.2.0-linux-x64.tar.gz", sha256 = Convert.ToHexStringLower(SHA256.HashData(package)), size = package.Length },
            },
        });
        var server = new Server(new()
        {
            [Feed] = manifest,
            [Feed + ".sig"] = TestSigning.Sign(manifest),
            ["https://updates.test/omp-gui-0.2.0-linux-x64.tar.gz"] = package,
        });
        var parent = TestProcesses.TempDir("upd-ui");
        var root = Path.Combine(parent, "omp-gui-0.1.0-linux-x64");
        WriteApp(root, "0.1.0", "old");
        ProcessStartInfo? launched = null;
        var installer = new UpdateInstaller(root, "OmpGui", Path.Combine(parent, "state")) { Launch = psi => launched = psi };
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs())
        {
            Updates = new UpdateChecker(new HttpClient(server), Feed, "0.1.0", "linux-x64", TestSigning.PublicKey),
            UpdateInstaller = installer,
            UpdateCheckDelay = TimeSpan.FromMilliseconds(50),
        };
        var quit = false;
        vm.QuitRequested += () => quit = true;
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "omp ready");

        // The automatic check: a notice in the sidebar, nothing downloaded.
        await Until(() => vm.UpdateAvailable, "update offered");
        await Settle();
        Assert.True(w.FindControl<Button>("UpdatePill")!.IsEffectivelyVisible);
        Take(w, $"{theme}-sidebar-available");
        Assert.DoesNotContain(Directory.GetFileSystemEntries(parent), d => Path.GetFileName(d).StartsWith('.'));

        vm.OpenUpdatesCommand.Execute(null);
        await Until(() => vm.IsSettingsOpen && vm.SettingsCategory == "updates", "updates page");
        await Settle();
        Assert.Equal("Download and install", w.FindControl<Button>("DownloadUpdateButton")!.Content);
        Assert.True(w.FindControl<TextBlock>("UpdateOmp")!.IsEffectivelyVisible);
        Take(w, $"{theme}-updates-available");

        vm.DownloadUpdateCommand.Execute(null);
        await Until(() => vm.UpdateReady, "update unpacked");
        await Settle();
        Assert.True(w.FindControl<Button>("RestartToUpdateButton")!.IsEffectivelyVisible);
        Assert.False(w.FindControl<Button>("DownloadUpdateButton")!.IsEffectivelyVisible);
        Assert.Equal("Restart to update to 0.2.0", vm.UpdatePill);
        Assert.Equal("old", File.ReadAllText(Path.Combine(root, "marker.txt"))); // nothing replaced while running
        Take(w, $"{theme}-updates-ready");
        w.Width = 640;
        await Settle();
        Take(w, $"{theme}-updates-ready-narrow");
        w.Width = 1180;

        // Restart now: the script is handed over with this process to wait for, and the window is asked to close.
        vm.RestartToUpdateCommand.Execute(null);
        Assert.True(quit);
        Assert.NotNull(launched);
        var script = File.ReadAllText(launched!.ArgumentList[0]);
        Assert.Contains($"kill -0 {Environment.ProcessId}", script);
        // The relaunch: macOS opens the app bundle (Launch Services), elsewhere the executable is started detached
        var relaunch = OperatingSystem.IsMacOS() ? "open '" + root + "'" : "nohup '" + Path.Combine(root, "OmpGui") + "'";
        Assert.Contains(relaunch, script);
        // Run it as if this process had quit (and without starting the stand-in app again).
        using (var exited = Process.Start(new ProcessStartInfo("/bin/sh", "-c true"))!) { exited.WaitForExit(); script = script.Replace($"kill -0 {Environment.ProcessId}", $"kill -0 {exited.Id}"); }
        script = string.Join('\n', script.Split('\n').Where(l => !l.Contains(relaunch, StringComparison.Ordinal)));
        var copy = Path.Combine(parent, "run.sh");
        File.WriteAllText(copy, script);
        using (var p = Process.Start(new ProcessStartInfo("/bin/sh", [copy]))!) p.WaitForExit();
        Assert.Equal("new 0.2.0", File.ReadAllText(Path.Combine(root, "marker.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(installer.PreviousDirectory, "marker.txt")));

        await vm.DisposeAsync();
        w.Close();
        MainViewModel.ApplyTheme("light");
        Assert.True(_findings == 0, Report.ToString());
    }
}
