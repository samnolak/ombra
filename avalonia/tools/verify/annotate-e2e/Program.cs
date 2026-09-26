using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.Controls;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;
using OmpGui.Rpc;

// Real-window check of page comments (annotate mode): the main window with the scripted fake omp, a page served on
// localhost:8766 in the preview (WebKitGTK), and real X11 mouse and keyboard input through xdotool. run.sh sets it up.
var dir = Environment.GetEnvironmentVariable("SHOTS") ?? Directory.GetCurrentDirectory();
var fake = Environment.GetEnvironmentVariable("FAKE_OMP_DLL")
    ?? throw new InvalidOperationException("FAKE_OMP_DLL: the path of tests/OmpGui.FakeOmp's built OmpGui.FakeOmp.dll");
AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
var vm = new MainViewModel(new SessionController(new OmpLaunchSpec { FileName = "dotnet", Arguments = [fake, "normal"] }), new AppArgs());
var w = new MainWindow { DataContext = vm, Width = 1280, Height = 800, Position = new PixelPoint(0, 0) };
w.Show();
var cts = new CancellationTokenSource();
var fails = 0;
void Check(string name, bool ok, string? detail = "") { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name} {detail}"); if (!ok) fails++; }
void X(params string[] a) { using var p = Process.Start(new ProcessStartInfo("xdotool", a))!; p.WaitForExit(); }
void Shot(string n) { using var p = Process.Start(new ProcessStartInfo("import", ["-window", "root", Path.Combine(dir, n + ".png")]))!; p.WaitForExit(); Console.WriteLine("SHOT " + n); }
async Task Until(Func<bool> c, int seconds = 15) { var d = DateTime.UtcNow.AddSeconds(seconds); while (!c() && DateTime.UtcNow < d) await Task.Delay(50); }
PixelPoint Screen(Control c, Point p) => c.PointToScreen(p);
void Click(PixelPoint p) { X("mousemove", p.X.ToString(), p.Y.ToString()); Thread.Sleep(150); X("click", "1"); }
async Task<string?> Js(string script)
{
    var panel = w.GetVisualDescendants().OfType<PreviewPanel>().First();
    var web = (NativeWebView?)typeof(PreviewPanel).GetProperty("WebView", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(panel);
    return web is null ? null : await web.InvokeScript(script);
}

async Task Script()
{
    vm.OnWindowOpened();
    await Until(() => vm.Phase == SessionPhase.Ready);
    vm.IsPreviewOpen = true;
    await Task.Delay(300);
    vm.Preview.NavigateCommand.Execute("http://localhost:8766/index.html");
    await Until(() => vm.Preview.Title == "Shop" && !vm.Preview.IsLoading);
    await Task.Delay(800);
    Check("page-loaded", vm.Preview.Title == "Shop", vm.Preview.CurrentUrl?.ToString());
    Shot("01-page");

    var annotate = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AnnotateButton");
    Click(Screen(annotate, new Point(annotate.Bounds.Width / 2, annotate.Bounds.Height / 2)));
    await Until(() => vm.Preview.IsAnnotating, 5);
    Check("annotate-on", vm.Preview.IsAnnotating);
    await Task.Delay(500);

    var host = w.GetVisualDescendants().OfType<Border>().First(b => b.Name == "WebHost");
    var buy = Screen(host, new Point(40 + 110, 140 + 28));
    X("mousemove", buy.X.ToString(), buy.Y.ToString());
    await Task.Delay(400);
    X("mousemove", (buy.X + 3).ToString(), buy.Y.ToString());
    await Task.Delay(500);
    Shot("02-hover");
    X("click", "1");
    await Task.Delay(700);
    Shot("03-comment-box");
    X("type", "--delay", "25", "Make this button bigger and blue");
    await Task.Delay(300);
    Shot("04-typed");
    X("key", "Return");
    await Until(() => vm.Preview.Annotations.Count == 1, 5);
    Check("comment-1", vm.Preview.Annotations.Count == 1, vm.Preview.Annotations.FirstOrDefault()?.Element.Selector);
    await Task.Delay(600);

    var heading = Screen(host, new Point(40 + 60, 24 + 16));
    Click(heading);
    await Task.Delay(700);
    X("type", "--delay", "25", "Shorter heading: Shop");
    X("key", "Return");
    await Until(() => vm.Preview.Annotations.Count == 2, 5);
    Check("comment-2", vm.Preview.Annotations.Count == 2, vm.Preview.Annotations.LastOrDefault()?.Element.Selector);
    await Task.Delay(600);
    Shot("05-two-pins");

    var count = await Js("document.getElementById('count').textContent");
    Check("page-click-blocked", Dec(count) == "0", "count=" + count);

    X("key", "Escape");
    await Until(() => !vm.Preview.IsAnnotating, 5);
    Check("esc-leaves-mode", !vm.Preview.IsAnnotating);
    Click(buy);
    await Task.Delay(500);
    count = await Js("document.getElementById('count').textContent");
    Check("page-works-again", Dec(count) == "1", "count=" + count);

    vm.Preview.ReloadCommand.Execute(null);
    await Task.Delay(2500);
    var pins = await Js("(function(){var h=document.querySelector('omp-annotate');return h?'host':'none'})()");
    Check("pins-after-reload", Dec(pins) == "host", pins);
    Shot("06-after-reload");

    Check("composer-chips", w.GetVisualDescendants().OfType<ItemsControl>().First(c => c.Name == "PageCommentsChip").IsEffectivelyVisible);
    var send = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SendButton");
    Click(Screen(send, new Point(send.Bounds.Width / 2, send.Bounds.Height / 2)));
    await Until(() => vm.Rows.OfType<UserRowViewModel>().Any(), 10);
    var sent = vm.Rows.OfType<UserRowViewModel>().FirstOrDefault()?.Text ?? "";
    Check("sent", sent.Contains("1. Make this button bigger and blue") && sent.Contains("Selector: #buy") && sent.Contains("2. Shorter heading"), "");
    Console.WriteLine("---- sent ----\n" + sent + "\n--------------");
    Check("cleared", vm.Preview.Annotations.Count == 0);
    await Task.Delay(1500);
    Shot("07-sent");
    Console.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
    Environment.ExitCode = fails == 0 ? 0 : 1;
    await vm.DisposeAsync();
    cts.Cancel();
}
Dispatcher.UIThread.Post(() => _ = Script());
Dispatcher.UIThread.MainLoop(cts.Token);
static string Dec(string? raw) => string.IsNullOrEmpty(raw) ? "" : raw.Length >= 2 && raw[0] == '"' ? System.Text.Json.JsonDocument.Parse(raw).RootElement.GetString() ?? "" : raw;
