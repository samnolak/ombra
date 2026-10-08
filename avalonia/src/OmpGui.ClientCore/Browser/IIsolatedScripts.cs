namespace OmpGui.ClientCore.Browser;

/// <summary>
/// Scripts of the app in a world of their own in a web view's page (WebKit's <c>WKContentWorld</c>): they share the
/// DOM with the page but not its globals, so the page can neither see nor replace them, and their built-ins are the
/// engine's own, whatever the page patched.
/// </summary>
public interface IIsolatedScripts
{
    /// <summary>Why this view has no isolated world (engine, missing API); null when it has one.</summary>
    string? Unsupported { get; }

    /// <summary>
    /// Calls <paramref name="functionSource"/> (a JavaScript function expression) in the main frame's isolated world
    /// with the arguments of the JSON array <paramref name="argsJson"/>, awaits a returned Promise, and answers its value
    /// as JSON text; null when the value is <c>undefined</c>, a function or a symbol. A script exception or rejection is
    /// an <see cref="IsolatedScriptException"/>; <see cref="NotSupportedException"/> when <see cref="Unsupported"/> is set.
    /// </summary>
    Task<string?> CallAsync(string functionSource, string argsJson, CancellationToken ct);
}

/// <summary>An engine without an isolated world: every call reports why.</summary>
public sealed class UnsupportedIsolatedScripts(string reason) : IIsolatedScripts
{
    public string? Unsupported => reason;

    public Task<string?> CallAsync(string functionSource, string argsJson, CancellationToken ct) =>
        Task.FromException<string?>(new NotSupportedException(reason));
}

/// <summary>A script in an isolated (or the page) world threw, its Promise rejected, or the engine refused it.</summary>
/// <param name="message">The exception as the page sees it (<c>String(error)</c>, e.g. "Error: message"), with its stack after a line break when there is one.</param>
public sealed class IsolatedScriptException(string message, string? domain = null, long code = 0) : Exception(message)
{
    /// <summary>The engine's error domain (WKErrorDomain), null for a script exception.</summary>
    public string? Domain { get; } = domain;

    public long Code { get; } = code;
}
