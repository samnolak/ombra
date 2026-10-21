using Avalonia.Threading;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly DraftStore _draftStore;
    private bool _changingDraft;

    // Ownership follows the live chat, so closing it or changing its identity cannot leave a stale claim behind.
    private bool IsDraftClaimed(string id)
    {
        foreach (var open in _opens.Values)
            if (open.DraftId == id) return true;
        return false;
    }

    private void InitializeDraft(OpenSession open, bool recoverUnfiled = false)
    {
        var launch = open.Controller.CurrentLaunch;
        if (launch.ReadOnly) return;
        var project = Path.GetFullPath(launch.WorkingDirectory ?? Environment.CurrentDirectory);
        var saved = _draftStore.All.FirstOrDefault(d => !IsDraftClaimed(d.Id) &&
            (launch.ResumeSessionFile is { } file ? SessionCatalog.SamePath(d.SessionFile, file) && SessionCatalog.SamePath(d.Project, project)
                : recoverUnfiled && d.SessionFile is null && SessionCatalog.SamePath(d.Project, project)));
        open.DraftId = saved?.Id ?? Guid.NewGuid().ToString("N");
        open.DraftSessionFile = saved?.SessionFile ?? launch.ResumeSessionFile;
        open.DraftProject = project;
        if (saved is not null)
        {
            open.ComposerText = saved.Composer.Text;
            open.Attachments = saved.Composer.Images.Select(i => new AttachmentViewModel(i, this)).ToList();
            open.PastedTexts = saved.Composer.Pastes.Select(p => new PastedTextViewModel(p, this)).ToList();
            open.Marks = saved.Composer.Marks.Select(m => new MarkViewModel(m, Marks)).ToList();
            foreach (var q in saved.Queue) AppendRecovered(open, q.Text, q.Images);
            foreach (var p in saved.Pending) AppendRecovered(open, p.Text, p.Images);
            if (saved.QuestionReply.Length > 0) AppendRecovered(open, saved.QuestionReply, []);
            open.ComposerMessage = Loc.T("Recovered unsent input. Review it before sending; nothing was sent automatically.");
            // The journal is consolidated only after the recovered composer is in memory.
            _draftStore.Update(open.DraftId, d => d with { Composer = CaptureDraft(open), Queue = [], Pending = [], QuestionReply = "", RecoveredSequences = [] });
        }
        open.Controller.QueueCheckpoint = queue => _draftStore.Update(open.DraftId, d => d with
        {
            Queue = queue.Where(q => !q.Recovered || !d.RecoveredSequences.Contains(q.Seq)).ToArray(),
            Pending = d.Pending.Where(p => !queue.Any(q => q.SubmissionId == p.Id)).ToArray(),
        }, open.DraftProject, open.DraftSessionFile, flush: true);
        open.Controller.SubmissionDelivered = id => _draftStore.Update(open.DraftId,
            d => d with { Pending = d.Pending.Where(p => p.Id != id).ToArray() }, flush: true);
    }

    private DraftContent CaptureDraft(OpenSession open)
    {
        var shown = open == _open;
        return new DraftContent(shown ? ComposerText : open.ComposerText,
            (shown ? Attachments.AsEnumerable() : open.Attachments).Select(a => a.Model).ToArray(),
            (shown ? PastedTexts.AsEnumerable() : open.PastedTexts).Select(p => p.Text).ToArray(),
            (shown ? Marks.Items.AsEnumerable() : open.Marks).Select(m => m.Model).ToArray());
    }

    private void SaveDraft(OpenSession? open = null)
    {
        if (_changingDraft) return;
        open ??= _open;
        if (open is null || open.DraftId.Length == 0) return;
        var content = CaptureDraft(open);
        var dialog = open == _open ? CurrentDialog : open.Dialog;
        // Authentication replies are credentials, not chat drafts.
        var reply = open.InFlightReply ?? (open.Controller.Snapshot().SigningIn is null ? dialog?.ReplyForRecovery ?? "" : "");
        _draftStore.Update(open.DraftId, d => d with { Composer = content, QuestionReply = reply }, open.DraftProject, open.DraftSessionFile);
        // Input recovered into a chat in the background changes its row's pencil (the shown chat's row has none)
        if (open != _open) SetDraftMarks();
    }

    private bool FlushDrafts()
    {
        try
        {
            foreach (var open in _opens.Values) SaveDraft(open);
            _draftStore.Flush();
            foreach (var open in _opens.Values)
                foreach (var q in open.Controller.Snapshot().Queued.Where(q => q.Recovered && open.RecoveredSequences.Contains(q.Seq)))
                    open.Controller.AcknowledgeRecovered(q.Seq);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ComposerMessage = Loc.F("Drafts could not be saved: {0}", e.Message);
            return false;
        }
    }
    private void PersistKnownDraftIdentity(OpenSession open)
    {
        if (open.Controller.CurrentLaunch.ReadOnly || open.Controller.SessionFile is not { } file || !File.Exists(file)) return;
        _draftStore.Update(open.DraftId, d => d with { SessionFile = file });
    }


    private void SyncDraftIdentity(OpenSession open, SessionSnapshot snapshot)
    {
        if (snapshot.SessionFile is not { } file) return;
        var project = snapshot.Cwd ?? open.DraftProject;
        if (SessionCatalog.SamePath(open.DraftSessionFile, file) && SessionCatalog.SamePath(open.DraftProject, project)) return;
        if (!File.Exists(file) && open.DraftSessionFile is null)
        {
            // omp can allocate a filename before writing it. Such a draft has no sidebar file to reopen yet.
            if (!SessionCatalog.SamePath(open.DraftProject, project))
            {
                open.DraftProject = project;
                _draftStore.Update(open.DraftId, d => d with { Project = project });
            }
            return;
        }
        var saved = _draftStore.All.FirstOrDefault(d => d.Id != open.DraftId && !IsDraftClaimed(d.Id) &&
            SessionCatalog.SamePath(d.SessionFile, file) && SessionCatalog.SamePath(d.Project, project));
        if (saved is not null || open.DraftSessionFile is { } previous && !SessionCatalog.SamePath(previous, file))
        {
            // A switch in an untouched process is a different chat, not a rename of its previous draft.
            SaveDraft(open);
            open.DraftId = saved?.Id ?? Guid.NewGuid().ToString("N");
            if (saved is not null) RestoreSavedInto(open, saved);
        }
        open.DraftSessionFile = file;
        open.DraftProject = project;
        _draftStore.Update(open.DraftId, d => d with { SessionFile = file, Project = project });
    }

    private void RestoreSavedInto(OpenSession open, SavedDraft saved, bool consumeSavedJournal = true)
    {
        var changing = _changingDraft;
        _changingDraft = true;
        try
        {
            AppendRecovered(open, saved.Composer.Text, saved.Composer.Images);
            foreach (var paste in saved.Composer.Pastes) AppendPaste(open, paste);
            foreach (var m in saved.Composer.Marks)
            {
                // A restored mark keeps its id unless the chat already has one with it
                var known = open == _open ? Marks.Items.AsEnumerable() : open.Marks;
                var mark = new MarkViewModel(known.Any(k => k.Id == m.Id) ? m with { Id = LocalMarks.NewId() } : m, Marks);
                if (open == _open) Marks.Items.Add(mark); else open.Marks.Add(mark);
            }
            foreach (var q in saved.Queue) AppendRecovered(open, q.Text, q.Images);
            foreach (var p in saved.Pending) AppendRecovered(open, p.Text, p.Images);
            if (saved.QuestionReply.Length > 0) AppendRecovered(open, saved.QuestionReply, []);
        }
        finally { _changingDraft = changing; }
        _draftStore.Update(open.DraftId, d => consumeSavedJournal
            ? d with { Composer = CaptureDraft(open), Queue = [], Pending = [], QuestionReply = "", RecoveredSequences = [] }
            : d with { Composer = CaptureDraft(open) });
        open.ComposerMessage = Loc.T("Recovered unsent input. Review it before sending; nothing was sent automatically.");
        if (open == _open)
        {
            ComposerMessage = open.ComposerMessage;
            AttachmentsChanged();
        }
    }

    private void AppendPaste(OpenSession open, string text)
    {
        var paste = new PastedTextViewModel(text, this);
        if (open == _open) PastedTexts.Add(paste); else open.PastedTexts.Add(paste);
    }

    private void AppendRecovered(OpenSession open, string text, IReadOnlyList<ImageAttachment> images)
    {
        var changing = _changingDraft;
        _changingDraft = true;
        try
        {
            if (open == _open)
            {
                if (text.Length > 0) ComposerText = ComposerText.Length == 0 ? text : ComposerText + "\n\n" + text;
                // Recovery must not silently discard a fourth image. Sending checks the full RPC byte budget instead.
                foreach (var image in images) Attachments.Add(new AttachmentViewModel(image, this));
                if (!changing) AttachmentsChanged();
            }
            else
            {
                if (text.Length > 0) open.ComposerText = open.ComposerText.Length == 0 ? text : open.ComposerText + "\n\n" + text;
                foreach (var image in images) open.Attachments.Add(new AttachmentViewModel(image, this));
            }
        }
        finally { _changingDraft = changing; }
    }

    private void RecoverRuntimeInput(OpenSession open, SessionSnapshot snapshot)
    {
        if (open.Controller.CurrentLaunch.ReadOnly) return;
        SyncDraftIdentity(open, snapshot);
        var recovered = snapshot.Queued.Where(q => q.Recovered && !open.RecoveredSequences.Contains(q.Seq)).ToArray();
        foreach (var q in recovered)
        {
            AppendRecovered(open, q.Text, q.Images);
            open.RecoveredSequences.Add(q.Seq);
        }
        var dialog = open == _open ? CurrentDialog : open.Dialog;
        if (dialog is not null && !dialog.ReplyInFlight && !snapshot.Dialogs.Any(d => d.Id == dialog.Id))
        {
            RecoverReply(open, dialog);
            if (open != _open) open.Dialog = null;
        }
        var pending = snapshot.Phase is SessionPhase.Faulted or SessionPhase.Stopped
            ? _draftStore.Get(open.DraftId)?.Pending ?? [] : [];
        foreach (var p in pending)
            if (!recovered.Any(q => q.SubmissionId == p.Id)) AppendRecovered(open, p.Text, p.Images);
        if (recovered.Length == 0 && pending.Length == 0) return;
        try
        {
            _draftStore.Update(open.DraftId, d => d with
            {
                Composer = CaptureDraft(open),
                Queue = d.Queue.Where(q => !open.RecoveredSequences.Contains(q.Seq)).ToArray(),
                Pending = d.Pending.Where(p => !pending.Any(recovered => recovered.Id == p.Id)
                    && !recovered.Any(q => q.SubmissionId == p.Id)).ToArray(),
                RecoveredSequences = open.RecoveredSequences.ToArray(),
            }, flush: true);
            foreach (var q in recovered) open.Controller.AcknowledgeRecovered(q.Seq);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            open.ComposerMessage = Loc.F("Recovered input could not be saved: {0}", e.Message);
            if (open == _open) ComposerMessage = open.ComposerMessage;
            return;
        }
        open.ComposerMessage = Loc.T("Recovered unsent input. Review it before sending; nothing was sent automatically.");
        if (open == _open) ComposerMessage = open.ComposerMessage;
        else SetDraftMarks();
    }

    private void RecoverReply(OpenSession open, DialogViewModel dialog)
    {
        if (open.Controller.Snapshot().SigningIn is not null || dialog.ReplyForRecovery.Length == 0) return;
        AppendRecovered(open, dialog.ReplyForRecovery, []);
        dialog.MarkReplyPreserved();
        SaveDraft(open);
        try { _draftStore.Flush(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            open.ComposerMessage = Loc.F("Recovered input could not be saved: {0}", e.Message);
            if (open == _open) ComposerMessage = open.ComposerMessage;
        }
    }

    private string StageSubmission(OpenSession open, (string Text, ImageAttachment[] Images) content, bool fromComposer)
    {
        try { SessionController.ValidateMessage(content.Text, content.Images); }
        catch (ArgumentException)
        {
            if (fromComposer && open == _open) OfferInputFile(content.Text);
            throw;
        }
        SaveDraft(open);
        var id = Guid.NewGuid().ToString("N");
        var before = _draftStore.Get(open.DraftId)!.Composer;
        try
        {
            _draftStore.Update(open.DraftId, d => d with
            {
                Composer = fromComposer ? DraftContent.Empty : d.Composer,
                Pending = [.. d.Pending, new DraftSubmission(id, content.Text, content.Images)],
            }, flush: true);
        }
        catch
        {
            _draftStore.Update(open.DraftId, d => d with { Composer = before, Pending = d.Pending.Where(p => p.Id != id).ToArray() });
            throw;
        }
        if (fromComposer) ClearComposer();
        return id;
    }

    private void FinishSubmission(OpenSession open, string id, bool accepted, bool waitForDelivery = false, DraftContent? recovery = null)
    {
        var pending = _draftStore.Get(open.DraftId)?.Pending.FirstOrDefault(p => p.Id == id);
        // Queue admission may fail before its checkpoint replaces Pending. The runtime row then owns recovery:
        // restoring the original file draft here would append both it and its queued reference (and images).
        var runtimeOwnsRecovery = !accepted && recovery is not null
            && open.Controller.Snapshot().Queued.Any(q => q.SubmissionId == id);
        if (pending is not null && !accepted && !runtimeOwnsRecovery)
        {
            if (recovery is null) AppendRecovered(open, pending.Text, pending.Images);
            else RestoreSavedInto(open, new SavedDraft(open.DraftId, open.DraftProject, open.DraftSessionFile,
                recovery, [], []), consumeSavedJournal: false);
            if (open == _open) ComposerMessage = Loc.T("Delivery was not confirmed. Review the conversation before sending the recovered input again.");
        }
        _draftStore.Update(open.DraftId, d => d with
        {
            Composer = CaptureDraft(open),
            // Keep the last durable copy until RecoverRuntimeInput atomically folds the runtime row into Composer.
            Pending = waitForDelivery || runtimeOwnsRecovery ? d.Pending : d.Pending.Where(p => p.Id != id).ToArray(),
        });
        // The previous checkpoint remains safe if a disk failure prevents recording acknowledgement.
        try { _draftStore.Flush(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ComposerMessage = Loc.F("Drafts could not be saved: {0}", e.Message); }
    }

    private void RemoveDeletedDrafts(string file, string project, string? draftId, OpenSession? preserveComposer = null, DraftContent? closedComposer = null)
    {
        if (preserveComposer is not null)
        {
            // The current runtime remains alive for new_session. Its callbacks follow this fresh identity.
            preserveComposer.DraftId = Guid.NewGuid().ToString("N");
            preserveComposer.DraftSessionFile = null;
            preserveComposer.RecoveredSequences.Clear();
            SaveDraft(preserveComposer);
        }
        if (closedComposer is not null)
        {
            // Capture before closing: disposing the old chat releases its image chips.
            RestoreSavedInto(_open, new SavedDraft(_open.DraftId, _open.DraftProject, _open.DraftSessionFile,
                closedComposer, [], []), consumeSavedJournal: false);
        }
        try
        {
            _draftStore.RemoveSession(file, draftId);
            // Stop deliberately kept a draft's folder. Retry cleanup now, but never remove a still-open chat's cwd.
            foreach (var open in _opens.Values)
                if (SessionCatalog.SamePath(open.Controller.CurrentLaunch.WorkingDirectory, project)) return;
            DeleteUnusedChatFolder(project, file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ComposerMessage = Loc.F("Drafts could not be saved: {0}", e.Message);
        }
    }
}
