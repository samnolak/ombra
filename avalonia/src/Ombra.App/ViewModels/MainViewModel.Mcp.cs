using CommunityToolkit.Mvvm.ComponentModel;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.App.ViewModels;

/// <summary>
/// Connector (MCP server) sign-in: omp's <c>mcp_login</c> sends the authorization page as <c>open_url</c> with
/// <c>mcpServer</c>. The browser opens by itself only for a sign-in the user started here and only for an https page
/// (or http on this computer); any other link waits on the link card for the user.
/// </summary>
public partial class MainViewModel
{
    private readonly HashSet<(SessionController Session, string Server)> _mcpLogins = [];

    /// <summary>Connectors of the shown chat that need the user to sign in (again), newest last.</summary>
    [ObservableProperty] private IReadOnlyList<McpAuthRequest> _mcpAuthRequests = [];

    /// <summary>An https page, or http on this computer (a local callback), without a user name or password in it.</summary>
    internal static bool IsSafeBrowserUrl(string url, out Uri uri)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri!) || uri.UserInfo.Length > 0) return false;
        return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }

    /// <summary>Signs in to a connector; its sign-in page opens in the browser. Throws what omp answered.</summary>
    /// <exception cref="OmpNotRunningException" />
    /// <exception cref="RpcCommandException" />
    public async Task<OmpMcpServer?> SignInToConnectorAsync(string server)
    {
        var session = Session;
        var key = (session, server);
        _mcpLogins.Add(key);
        try { return await session.McpLoginAsync(server, _cts.Token); }
        finally { _mcpLogins.Remove(key); }
    }

    public Task<bool> CancelConnectorSignInAsync(string server) => Session.McpLoginCancelAsync(server, _cts.Token);

    public Task<IReadOnlyList<OmpMcpServer>> GetConnectorStatesAsync() => Session.GetMcpServersAsync(_cts.Token);

    public Task<IReadOnlyList<OmpMcpServer>> ReconnectConnectorsAsync(string? server = null) => Session.McpReconnectAsync(server, _cts.Token);

    public void DismissConnectorSignInPrompt(string server) => Session.DismissMcpAuthRequest(server);

    /// <summary>omp's link for a sign-in the user started here: opened now. False: the link card shows it.</summary>
    private bool OpenOwnSignInPage(OpenUrlRequest request)
    {
        if (request.McpServer is not { } server || !_mcpLogins.Contains((Session, server)) || !IsSafeBrowserUrl(request.Target, out var uri)) return false;
        OpenUrlRequested?.Invoke(uri.AbsoluteUri);
        return true;
    }
}
