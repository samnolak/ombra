using System.Text.Json;

namespace Ombra.Rpc;

public sealed record EditorDocumentSnapshot(string Uri, long Version, string Text, string LanguageId, string BaseHash, bool Dirty, bool Conflict);

public static class EditorCommands
{
    public static async Task<JsonElement> EditorDocumentAsync(this RpcConnection connection, string action,
        EditorDocumentSnapshot document, string? method = null, int line = 0, int character = 0,
        string? newName = null, CancellationToken ct = default)
    {
        var response = await connection.RequestAsync("editor_document", writer =>
        {
            writer.WriteString("action", action);
            WriteDocument(writer, document, action is "open" or "change");
            if (method is not null)
            {
                writer.WriteString("method", method);
                writer.WriteStartObject("position");
                writer.WriteNumber("line", line);
                writer.WriteNumber("character", character);
                writer.WriteEndObject();
                if (newName is not null) writer.WriteString("newName", newName);
            }
        }, ct: ct).ConfigureAwait(false);
        if (!response.Success) throw new RpcCommandException("editor_document", response.Error ?? "Editor request failed", response.Code);
        return response.Data ?? throw new InvalidDataException("Editor response omitted data");
    }

    public static async Task SetEditorTrustAsync(this RpcConnection connection, bool trusted, CancellationToken ct = default)
    {
        var response = await connection.RequestAsync("editor_trust", writer => writer.WriteBoolean("trusted", trusted), ct: ct).ConfigureAwait(false);
        if (!response.Success) throw new RpcCommandException("editor_trust", response.Error ?? "Trust request failed", response.Code);
    }

    public static Task SendEditorEditResultAsync(this RpcConnection connection, string requestId, EditorDocumentSnapshot? document,
        string? error = null, CancellationToken ct = default) => connection.SendAsync("editor_edit_result", null, writer =>
        {
            writer.WriteString("requestId", requestId);
            writer.WriteBoolean("success", document is not null);
            if (error is not null) writer.WriteString("error", error);
            if (document is not null)
            {
                writer.WriteStartObject("document");
                WriteDocument(writer, document, true);
                writer.WriteEndObject();
            }
        }, ct);

    private static void WriteDocument(Utf8JsonWriter writer, EditorDocumentSnapshot document, bool includeText)
    {
        writer.WriteString("uri", document.Uri);
        writer.WriteNumber("version", document.Version);
        writer.WriteString("languageId", document.LanguageId);
        writer.WriteString("baseHash", document.BaseHash);
        writer.WriteBoolean("dirty", document.Dirty);
        writer.WriteBoolean("conflict", document.Conflict);
        if (includeText) writer.WriteString("text", document.Text);
    }
}
