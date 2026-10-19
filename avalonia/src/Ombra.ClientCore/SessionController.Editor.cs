using Ombra.Rpc;

namespace Ombra.ClientCore;

public sealed partial class SessionController
{
    /// <summary>Awaited before session startup completes, so open buffers are registered before prompts run.</summary>
    public event Func<Task>? EditorConnectionChanged;
    /// <summary>Handled outside the conversation state lock; never blocks the response reader.</summary>
    public event Func<RpcConnection, RpcFrame, Task>? EditorFrameReceived;

    private async Task NotifyEditorConnectionAsync()
    {
        if (EditorConnectionChanged is not { } handlers) return;
        foreach (Func<Task> handler in handlers.GetInvocationList()) await handler().ConfigureAwait(false);
    }

    private async Task HandleEditorFrameAsync(RpcConnection connection, RpcFrame frame)
    {
        try
        {
            if (EditorFrameReceived is { } handlers)
            {
                foreach (Func<RpcConnection, RpcFrame, Task> handler in handlers.GetInvocationList())
                    await handler(connection, frame).ConfigureAwait(false);
            }
            else if (frame.Json.TryGetProperty("requestId", out var id))
                await connection.SendEditorEditResultAsync(id.GetString()!, null, "No editor is attached").ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (connection.CloseReason is null && frame.Json.TryGetProperty("requestId", out var id))
            {
                try { await connection.SendEditorEditResultAsync(id.GetString()!, null, error.Message).ConfigureAwait(false); }
                catch (Exception sendError) when (sendError is IOException or ObjectDisposedException or RpcConnectionClosedException) { }
            }
        }
    }
}
