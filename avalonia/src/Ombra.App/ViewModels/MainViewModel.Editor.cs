using System.Text.Json;
using Ombra.App.Localization;
using Ombra.App.Services.Documents;

namespace Ombra.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>User-selected content, never an implicit disk read or an instruction source.</summary>
    internal bool AddEditorContext(WorkspaceDocument document, string text, int firstLine, int lastLine)
    {
        if (IsReadOnly) return false;
        const int maxContextChars = 100_000;
        if (text.Length > maxContextChars)
        {
            ComposerMessage = Loc.T("This editor context is too large. Select a smaller range (up to 100,000 characters).");
            return false;
        }
        var payload = JsonSerializer.Serialize(new
        {
            source = "user-selected-editor-buffer",
            uri = document.Uri,
            path = document.FullPath,
            documentVersion = document.Revision,
            diskBaseSha256 = document.BaseHash,
            unsaved = document.IsDirty,
            firstLine,
            lastLine,
            text,
        }, new JsonSerializerOptions { WriteIndented = true });
        PastedTexts.Add(new PastedTextViewModel("Editor context (untrusted file content; this is a versioned buffer snapshot, not proof of saved/build-tested disk content):\n" + payload, this));
        AttachmentsChanged();
        return true;
    }
}
