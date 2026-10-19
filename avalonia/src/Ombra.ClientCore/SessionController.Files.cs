using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>
/// Whether omp's write tool creates its file: omp says nothing about it, so the client looks at the disk when the call
/// starts (omp sends the start before it writes) and tells the conversation before applying that frame. A late look
/// (omp wrote first) finds the file and only says "changed": never a new file that was not.
/// </summary>
public sealed partial class SessionController
{
    /// <summary>Called with every event frame, under the lock, before the conversation applies it.</summary>
    private void NoteNewFile(RpcFrame frame)
    {
        if (frame.Type != "tool_execution_start" || ConversationState.Str(frame.Json, "toolCallId") is not { } id
            || ConversationState.WriteTarget(frame.Json) is not { } path) return;
        if (IsNewFile(_state.Cwd, path)) _state.ExpectNewFile(id);
    }

    /// <summary>
    /// Nothing is at <paramref name="path"/> (resolved against omp's working directory) yet. Without a working
    /// directory a relative path can't be checked, and a path the OS rejects is not a file: both are "not new".
    /// </summary>
    internal static bool IsNewFile(string? cwd, string path)
    {
        try
        {
            if (LocalFilePaths.Resolve(cwd, path) is not { } local) return false;
            return !File.Exists(local) && !Directory.Exists(local);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
