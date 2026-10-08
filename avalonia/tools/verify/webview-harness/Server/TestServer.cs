using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace OmpGui.WebViewHarness.Server;

/// <summary>
/// Test pages on <c>http://127.0.0.1:&lt;free port&gt;/</c> (loopback only: no firewall or local-network prompt).
/// Pages live in <c>Server/Pages/*.html</c> (copied next to the binary); a few routes are generated here.
/// </summary>
internal sealed class TestServer : IDisposable
{
    public const string AuthUser = "user";
    public const string AuthPassword = "pass";
    public const string AuthRealm = "Studio";
    public const string DownloadBody = "hello download\n";

    private readonly HttpListener _listener = new();
    private readonly string _pages = Path.Combine(AppContext.BaseDirectory, "Pages");
    private readonly CancellationTokenSource _stop = new();

    public TestServer()
    {
        Port = FreePort();
        BaseUrl = $"http://127.0.0.1:{Port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }
    public string BaseUrl { get; }

    /// <summary>Every request, in order: method, path, status, whether it carried credentials.</summary>
    public ConcurrentQueue<JsonObject> Requests { get; } = new();

    public string Url(string path) => BaseUrl + path.TrimStart('/');

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var path = req.Url?.AbsolutePath ?? "/";
        var status = 200;
        try
        {
            status = Route(path, req, res);
        }
        catch (Exception e)
        {
            status = 500;
            Write(res, 500, "text/plain", e.ToString());
        }
        finally
        {
            Requests.Enqueue(new JsonObject
            {
                ["method"] = req.HttpMethod, ["path"] = path, ["status"] = status,
                ["authorization"] = req.Headers["Authorization"] is { } a ? a.Split(' ')[0] : null,
                ["userAgent"] = req.UserAgent,
            });
            try
            {
                res.Close();
            }
            catch
            {
                // client went away
            }
        }
    }

    private int Route(string path, HttpListenerRequest req, HttpListenerResponse res)
    {
        switch (path)
        {
            case "/auth/basic":
                {
                    if (BasicCredentials(req) is (AuthUser, AuthPassword))
                        return Write(res, 200, "text/html", "<!doctype html><title>signed in</title><p id=r>signed-in as user</p>");
                    res.AddHeader("WWW-Authenticate", $"Basic realm=\"{AuthRealm}\"");
                    // nginx's body, as studio.example.com sends it
                    return Write(res, 401, "text/html",
                        "<html><head><title>401 Authorization Required</title></head><body><center><h1>401 Authorization Required</h1></center></body></html>");
                }
            case "/download/attachment":
                res.AddHeader("Content-Disposition", "attachment; filename=\"report.txt\"");
                return Write(res, 200, "text/plain", DownloadBody);
            case "/download/plain.txt":
                return Write(res, 200, "text/plain", DownloadBody);
            case "/download/binary":
                return Write(res, 200, "application/x-omp-unknown", DownloadBody);
            case "/slow":
                Thread.Sleep(3000); // a navigation the next one cancels
                return Write(res, 200, "text/html", "<!doctype html><title>slow</title><p id=r>slow</p>");
            case "/":
                return Write(res, 200, "text/html", "<!doctype html><title>harness</title><p id=r>ok</p>");
        }
        var name = path.Trim('/').Replace('/', '-');
        var file = Path.Combine(_pages, name + ".html");
        if (name.Length > 0 && !name.Contains("..", StringComparison.Ordinal) && File.Exists(file))
            return Write(res, 200, "text/html", File.ReadAllText(file));
        return Write(res, 404, "text/plain", "not found: " + path);
    }

    private static (string, string)? BasicCredentials(HttpListenerRequest req)
    {
        var header = req.Headers["Authorization"];
        if (header is null || !header.StartsWith("Basic ", StringComparison.Ordinal)) return null;
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
            var colon = text.IndexOf(':');
            return colon < 0 ? null : (text[..colon], text[(colon + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static int Write(HttpListenerResponse res, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        res.StatusCode = status;
        res.ContentType = contentType + (contentType.StartsWith("text/", StringComparison.Ordinal) ? "; charset=utf-8" : "");
        res.Headers["Cache-Control"] = "no-store";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes);
        return status;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // already stopped
        }
    }
}
