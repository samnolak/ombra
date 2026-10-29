namespace Ombra.App.Services.Documents;

/// <summary>
/// Settings › General › Autosave for every editor buffer (the Editor page and the Files pane alike):
/// <c>off</c> (default), <c>delay</c> (saved <see cref="Delay"/> after the last edit) or <c>focus</c> (saved when an
/// editor showing it loses the keyboard, or the window goes to the background). A buffer in conflict with the disk,
/// read-only, or already saved is left alone; a failed save stays unsaved and says why in the editor's status.
/// </summary>
public sealed class EditorAutosave
{
    public const string Off = "off", AfterDelay = "delay", OnFocusChange = "focus";
    public static TimeSpan Delay { get; internal set; } = TimeSpan.FromSeconds(1);

    private readonly WorkspaceDocumentService _documents;
    private readonly HashSet<WorkspaceDocument> _observed = [];
    private readonly Dictionary<WorkspaceDocument, CancellationTokenSource> _pending = [];

    public EditorAutosave(WorkspaceDocumentService documents)
    {
        _documents = documents;
        documents.DocumentOpened += Observe;
        foreach (var document in documents.Documents) Observe(document);
    }

    public string Mode
    {
        get;
        set
        {
            field = value is AfterDelay or OnFocusChange ? value : Off;
            if (field != AfterDelay) CancelPending();
        }
    } = Off;

    /// <summary>The window went to the background: in focus mode every unsaved buffer is saved.</summary>
    public void WindowDeactivated()
    {
        if (Mode != OnFocusChange) return;
        foreach (var document in _documents.Documents) _ = SaveAsync(document);
    }

    private void Observe(WorkspaceDocument document)
    {
        if (!_observed.Add(document)) return;
        document.Changed += Changed;
        document.EditorFocusLeft += FocusLeft;
    }

    private void FocusLeft(WorkspaceDocument document)
    {
        if (Mode == OnFocusChange) _ = SaveAsync(document);
    }

    private void Changed(WorkspaceDocument document)
    {
        if (Mode != AfterDelay || !document.IsDirty) return;
        if (_pending.Remove(document, out var previous)) previous.Cancel();
        var cts = _pending[document] = new CancellationTokenSource();
        _ = SaveLaterAsync(document, cts);
    }

    private async Task SaveLaterAsync(WorkspaceDocument document, CancellationTokenSource cts)
    {
        try { await Task.Delay(Delay, cts.Token); }
        catch (OperationCanceledException) { return; }
        if (_pending.TryGetValue(document, out var current) && ReferenceEquals(current, cts)) _pending.Remove(document);
        await SaveAsync(document);
    }

    private static async Task SaveAsync(WorkspaceDocument document)
    {
        if (!document.IsDirty || document.HasConflict || document.IsReadOnly) return;
        try { await document.SaveAsync(); }
        catch (Exception e) when (e is not OutOfMemoryException) { } // the buffer stays unsaved; its status says why
    }

    private void CancelPending()
    {
        foreach (var cts in _pending.Values) cts.Cancel();
        _pending.Clear();
    }
}
