namespace OmpGui.ClientCore.Browser;

/// <summary>
/// Who answers a web page's HTTP authentication challenge (Basic, Digest, NTLM, Negotiate, proxy): the browser pane's
/// <see cref="DialogBroker"/>, which shows the sign-in card to the user. The engine hooks (WKWebView, WebView2,
/// WebKitGTK) call it and give the engine the answer; null means Cancel (the page's own 401 response is shown).
/// Cancelling <paramref name="ct"/> also answers null. Credentials are never filled in by the agent.
/// </summary>
public interface IBrowserAuthHandler
{
    Task<CredentialAnswer?> RequestCredentialsAsync(CredentialRequest request, CancellationToken ct);
}
