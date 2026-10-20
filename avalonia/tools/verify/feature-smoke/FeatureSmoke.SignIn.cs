#if OMBRA_FEATURE_SMOKE
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ombra.App.ViewModels;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] SignInSections() => [("sign-in", SignIn)];

        /// <summary>
        /// A sign-in page opened in Ombra (<see cref="LinkTarget.SignIn"/>) completes its OAuth redirect to a callback on
        /// this computer, as omp's MCP and provider sign-ins do: a local server's /authorize answers 302 to its /callback,
        /// and the callback must be requested by the page's engine. In the chat's Browser tool and in the sign-in window
        /// over Settings, with the built-in preview and then with Nightly (when this Mac can run it).
        /// </summary>
        private async Task SignIn()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            using var http = new HttpListener();
            http.Prefixes.Add($"http://127.0.0.1:{port}/");
            http.Start();
            var callbacks = new System.Collections.Concurrent.ConcurrentQueue<string>();
            _ = Task.Run(async () =>
            {
                while (http.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await http.GetContextAsync(); }
                    catch (Exception) { return; }
                    var path = context.Request.Url!.AbsolutePath;
                    var query = context.Request.Url.Query;
                    if (path == "/authorize")
                    {
                        context.Response.StatusCode = 302;
                        context.Response.RedirectLocation = $"http://127.0.0.1:{port}/callback{query.Replace("?state=", "?code=c&state=")}";
                    }
                    else
                    {
                        if (path == "/callback") callbacks.Enqueue(query);
                        var body = Encoding.UTF8.GetBytes("<html><body style='background:#0a0'>Signed in. You can close this tab.</body></html>");
                        context.Response.ContentType = "text/html";
                        await context.Response.OutputStream.WriteAsync(body);
                    }
                    context.Response.Close();
                }
            });
            try
            {
                foreach (var engine in new[] { "preview", "nightly" })
                {
                    if (engine == "nightly" && !MainViewModel.NightlyBrowserAvailable)
                    {
                        Log($"SKIP sign-in-nightly: Nightly can't run here");
                        continue;
                    }
                    vm.SetAgentBrowserEngineCommand.Execute(engine);
                    await Task.Delay(500);

                    // The chat's Browser tool
                    vm.ShowChatsCommand.Execute(null);
                    var state = $"{engine}-chat";
                    vm.OpenLink(new Uri($"http://127.0.0.1:{port}/authorize?state={state}"), LinkTarget.SignIn);
                    var done = await Until(() => callbacks.Contains($"?code=c&state={state}"), 45);
                    Check($"sign-in-callback-{state}", done && vm.Preview.UseNightly == (engine == "nightly")
                        && await Until(() => vm.Preview.CurrentUrl?.AbsolutePath == "/callback", 15),
                        $"useNightly={vm.Preview.UseNightly} fallback={vm.Preview.NightlyFallbackReason} url={vm.Preview.CurrentUrl} callbacks={string.Join(" ", callbacks)}");
                    Shot($"60-sign-in-{state}");

                    // Over Settings: the sign-in window
                    vm.IsSettingsOpen = true;
                    state = $"{engine}-window";
                    vm.OpenLink(new Uri($"http://127.0.0.1:{port}/authorize?state={state}"), LinkTarget.SignIn);
                    var browser = vm.SignInBrowser;
                    done = await Until(() => callbacks.Contains($"?code=c&state={state}"), 45);
                    Check($"sign-in-callback-{state}", done && browser is { } b && b.UseNightly == (engine == "nightly") && vm.IsSettingsOpen
                        && await Until(() => b.CurrentUrl?.AbsolutePath == "/callback", 15),
                        $"useNightly={browser?.UseNightly} url={browser?.CurrentUrl} callbacks={string.Join(" ", callbacks)}");
                    Shot($"61-sign-in-{state}");
                    vm.SignInBrowser = null; // done: the window closes
                    await Task.Delay(500);
                    vm.CloseSettingsCommand.Execute(null);
                }
            }
            finally
            {
                http.Stop();
                vm.SetAgentBrowserEngineCommand.Execute("preview");
            }
        }
    }
}
#endif
