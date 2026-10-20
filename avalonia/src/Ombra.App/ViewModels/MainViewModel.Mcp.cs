using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.App.ViewModels;

/// <summary>
/// Connector (MCP server) sign-in: omp's <c>mcp_login</c> sends the authorization page as <c>open_url</c> with
/// <c>mcpServer</c>. The page opens by itself only for a sign-in the user started here (the connectors lists, or
/// <c>/mcp reauth &lt;name&gt;</c> typed in the message box) and only for an https page (or http on this computer); any
/// other link waits on the link card for the user. It opens in Ombra (<see cref="LinkTarget.SignIn"/>: the chat's
/// Browser tool, or the sign-in window over Settings), where omp's local callback completes it; the sign-in button's
/// menu sends it to the default browser instead (passkeys, a company's SSO). Done, its tab closes.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Sign-ins started from the connectors lists, with where their page opens.</summary>
    private readonly Dictionary<(SessionController Session, string Server), LinkTarget> _mcpLogins = [];

    /// <summary>Where the page of a sign-in in progress opened in Ombra: its tab closes when the sign-in ends.</summary>
    private readonly Dictionary<(SessionController Session, string Server), (PreviewViewModel Browser, PreviewTab Tab)> _signInPages = [];

    /// <summary><c>/mcp reauth|login &lt;name&gt;</c> sent from the message box, with when: its sign-in page opens like one started from the list.</summary>
    private readonly Dictionary<(SessionController Session, string Server), long> _typedMcpLogins = [];

    private static readonly TimeSpan TypedSignInWindow = TimeSpan.FromMinutes(2);

    /// <summary>Connectors of the shown chat that need the user to sign in (again), newest last.</summary>
    [ObservableProperty] private IReadOnlyList<McpAuthRequest> _mcpAuthRequests = [];

    /// <summary>An https page, or http on this computer (a local callback), without a user name or password in it.</summary>
    internal static bool IsSafeBrowserUrl(string url, out Uri uri)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri!) || uri.UserInfo.Length > 0) return false;
        return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }

    /// <summary>
    /// Signs in to a connector; its sign-in page opens in Ombra (or the default browser with
    /// <paramref name="defaultBrowser"/>), and its tab closes once omp answered or the user cancelled. Throws what omp answered.
    /// </summary>
    /// <exception cref="OmpNotRunningException" />
    /// <exception cref="RpcCommandException" />
    public async Task<OmpMcpServer?> SignInToConnectorAsync(string server, bool defaultBrowser = false)
    {
        var session = Session;
        var key = (session, server);
        _mcpLogins[key] = defaultBrowser ? LinkTarget.SystemBrowser : LinkTarget.SignIn;
        var done = false;
        try
        {
            var signedIn = await session.McpLoginAsync(server, _cts.Token);
            done = true;
            return signedIn;
        }
        catch (RpcCommandException e) when (e.Code == "mcp_login_cancelled")
        {
            done = true;
            throw;
        }
        finally
        {
            _mcpLogins.Remove(key);
            // A failed sign-in keeps its page: what the server said there
            if (_signInPages.Remove(key, out var page) && done) CloseSignInPage(page);
        }
    }

    public Task<bool> CancelConnectorSignInAsync(string server) => Session.McpLoginCancelAsync(server, _cts.Token);

    public Task<IReadOnlyList<OmpMcpServer>> GetConnectorStatesAsync() => Session.GetMcpServersAsync(_cts.Token);

    public Task<IReadOnlyList<OmpMcpServer>> ReconnectConnectorsAsync(string? server = null) => Session.McpReconnectAsync(server, _cts.Token);

    public Task<OmpMcpServer?> SignOutOfConnectorAsync(string server) => Session.McpLogoutAsync(server, _cts.Token);

    public void DismissConnectorSignInPrompt(string server) => Session.DismissMcpAuthRequest(server);

    /// <summary>
    /// omp's <c>/mcp</c> sent from the message box: <c>reauth</c> / <c>login</c> &lt;name&gt; runs omp's sign-in, whose
    /// page then opens in Ombra like a sign-in started from the connectors list.
    /// </summary>
    private void NoteTypedMcpCommand(string args)
    {
        var words = args.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words[0] is not ("reauth" or "login")) return;
        var server = words[1].Trim('"', '\'');
        if (server.Length > 0) _typedMcpLogins[(Session, server)] = Stopwatch.GetTimestamp();
    }

    /// <summary>omp's link for a sign-in the user started here: opened now. False: the link card shows it.</summary>
    private bool OpenOwnSignInPage(OpenUrlRequest request)
    {
        if (request.McpServer is not { } server || !IsSafeBrowserUrl(request.Target, out var uri)) return false;
        var key = (Session, server);
        var typed = _typedMcpLogins.Remove(key, out var at) && Stopwatch.GetElapsedTime(at) < TypedSignInWindow;
        if (!_mcpLogins.TryGetValue(key, out var target))
        {
            if (!typed) return false;
            target = LinkTarget.SignIn;
        }
        if (target == LinkTarget.SystemBrowser) OpenLink(uri, LinkTarget.SystemBrowser);
        else if (OpenSignInPage(uri) is { } page && _mcpLogins.ContainsKey(key)) _signInPages[key] = page;
        return true;
    }
}
