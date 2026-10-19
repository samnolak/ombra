using System.Security.Cryptography;
using System.Text;
using Avalonia.Headless.XUnit;
using Ombra.App.Services.Documents;

namespace Ombra.Tests;

public sealed class WorkspaceDocumentTests
{
    private static (string Root, string Path, string Recovery) Paths(string name)
    {
        var root = TestProcesses.TempDir("documents-" + name);
        return (root, System.IO.Path.Combine(root, "source.txt"), System.IO.Path.Combine(root, "recovery"));
    }

    private static Encoding EncodingFor(string id, bool bom) => id switch
    {
        "utf-8" => new UTF8Encoding(bom, true),
        "utf-16le" => new UnicodeEncoding(false, bom, true),
        "utf-16be" => new UnicodeEncoding(true, bom, true),
        "utf-32le" => new UTF32Encoding(false, bom, true),
        "utf-32be" => new UTF32Encoding(true, bom, true),
        _ => throw new ArgumentException(id)
    };

    private static byte[] Encoded(Encoding encoding, string text) => [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [AvaloniaTheory]
    [InlineData("utf-8", false)]
    [InlineData("utf-8", true)]
    [InlineData("utf-16le", true)]
    [InlineData("utf-16be", true)]
    [InlineData("utf-32le", true)]
    [InlineData("utf-32be", true)]
    public async Task Open_and_save_preserve_exact_encoding_bom_mixed_eol_and_final_line(string id, bool bom)
    {
        var (_, path, recovery) = Paths(id);
        const string text = "α 😀\r\nsecond\nthird\rlast";
        var encoding = EncodingFor(id, bom);
        var original = Encoded(encoding, text);
        File.WriteAllBytes(path, original);
        await using var service = new WorkspaceDocumentService(recovery, watchFiles: false);
        var document = await service.OpenAsync(path);
        Assert.Equal(text, document.Buffer.Text);
        Assert.Equal(id, document.EncodingName);
        Assert.Equal(bom, document.HasBom);
        Assert.Equal("\r\n", document.NewLine);
        Assert.Equal(Hash(original), document.BaseHash);
        Assert.True(document.Revision >= 1);
        await document.SaveAsync();
        Assert.Equal(original, File.ReadAllBytes(path));
        document.Buffer.Insert(document.Buffer.TextLength, " é");
        await document.SaveAsync();
        Assert.Equal(Encoded(encoding, text + " é"), File.ReadAllBytes(path));
        Assert.False(document.IsDirty);
        Assert.Empty(Directory.GetFiles(recovery, "*.json"));
        Assert.Empty(Directory.GetFiles(System.IO.Path.GetDirectoryName(path)!, ".ombra-save-*"));
    }

    [AvaloniaTheory]
    [InlineData("C080")]
    [InlineData("E282")]
    [InlineData("EFBBBFFF")]
    [InlineData("FFFE00")]
    [InlineData("FFFE00D8")]
    [InlineData("FEFFD800")]
    [InlineData("FFFE000000001100")]
    [InlineData("0000FEFF00110000")]
    [InlineData("610062")]
    public async Task Invalid_encodings_and_binary_bytes_are_rejected_without_replacement(string hex)
    {
        var (_, path, _) = Paths("invalid");
        var bytes = Convert.FromHexString(hex);
        File.WriteAllBytes(path, bytes);
        await using var service = new WorkspaceDocumentService(watchFiles: false);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.OpenAsync(path));
        Assert.Empty(service.Documents);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [AvaloniaTheory]
    [InlineData("utf-8", false)]
    [InlineData("utf-8", true)]
    [InlineData("utf-16le", true)]
    [InlineData("utf-16be", true)]
    [InlineData("utf-32le", true)]
    [InlineData("utf-32be", true)]
    public async Task Empty_and_bom_only_files_round_trip(string id, bool bom)
    {
        var (_, path, _) = Paths("empty");
        var bytes = EncodingFor(id, bom).GetPreamble();
        File.WriteAllBytes(path, bytes);
        await using var service = new WorkspaceDocumentService(watchFiles: false);
        var document = await service.OpenAsync(path);
        Assert.Equal("", document.Buffer.Text);
        await document.SaveAsync();
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [AvaloniaFact]
    public async Task Large_files_and_oversize_edits_fail_instead_of_preview_truncation()
    {
        var (_, path, _) = Paths("limits");
        using (var stream = File.Create(path)) stream.SetLength(WorkspaceDocumentService.MaxFileBytes + 1L);
        await using var service = new WorkspaceDocumentService(watchFiles: false);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.OpenAsync(path));
        File.WriteAllText(path, new string('a', WorkspaceDocumentService.MaxTextLength + 1), new UTF8Encoding(false));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.OpenAsync(path));
        File.WriteAllText(path, "one\r\ntwo\nlast", new UTF8Encoding(false));
        var document = await service.OpenAsync(path);
        Assert.Equal("one\r\ntwo\nlast", document.Buffer.Text);
        Assert.Throws<NotSupportedException>(() => document.Buffer.Insert(0, new string('a', WorkspaceDocumentService.MaxTextLength)));
        Assert.Equal("one\r\ntwo\nlast", document.Buffer.Text);
        Assert.False(document.IsDirty);
    }

    [AvaloniaFact]
    public async Task All_opens_share_one_buffer_undo_stack_and_positive_monotonic_revision()
    {
        var (_, path, _) = Paths("canonical");
        File.WriteAllText(path, "original");
        await using var service = new WorkspaceDocumentService(watchFiles: false);
        var opened = 0;
        service.DocumentOpened += _ => opened++;
        var pair = await Task.WhenAll(service.OpenAsync(path), service.OpenAsync(path));
        Assert.Same(pair[0], pair[1]);
        Assert.Equal(1, opened);
        Assert.Single(service.Documents);
        var firstRevision = pair[0].Revision;
        pair[0].Buffer.Insert(0, "edit ");
        Assert.Equal("edit original", pair[1].Buffer.Text);
        Assert.True(pair[1].IsDirty);
        var editedRevision = pair[0].Revision;
        Assert.True(editedRevision > firstRevision);
        pair[1].Buffer.UndoStack.Undo();
        Assert.Equal("original", pair[0].Buffer.Text);
        Assert.False(pair[0].IsDirty);
        Assert.True(pair[0].Revision > editedRevision);
        pair[0].Buffer.UndoStack.Redo();
        await pair[0].SaveAsync();
        Assert.False(pair[1].IsDirty);
        pair[1].Buffer.UndoStack.Undo();
        Assert.True(pair[0].IsDirty);
        Assert.Equal("original", pair[0].Buffer.Text);
        Assert.Equal("edit original", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task Agent_edits_require_clean_matching_revision_and_disk_base_and_are_unsaved_undoable()
    {
        var (_, path, _) = Paths("agent-base");
        File.WriteAllText(path, "original");
        await using var service = new WorkspaceDocumentService(watchFiles: false);
        var document = await service.OpenAsync(path);
        var revision = document.Revision;
        var hash = document.BaseHash;
        Assert.False(document.ApplyEdit(revision, new string('0', 64), "wrong hash"));
        Assert.False(document.ApplyEdit(revision + 1, hash, "wrong revision"));
        Assert.False(document.ApplyEdit(revision, hash, "invalid\ud800"));
        Assert.Equal(revision, document.Revision);
        Assert.Equal("original", document.Buffer.Text);
        Assert.True(document.ApplyEdit(revision, hash, "agent draft"));
        Assert.True(document.IsDirty);
        Assert.Equal(hash, document.BaseHash);
        Assert.Equal("original", File.ReadAllText(path));
        Assert.False(document.ApplyEdit(document.Revision, hash, "would overwrite draft"));
        document.Buffer.UndoStack.Undo();
        Assert.False(document.IsDirty);
        Assert.Equal("original", document.Buffer.Text);
        Assert.False(document.ApplyEdit(revision, hash, "stale even after undo"));
        Assert.True(document.ApplyEdit(document.Revision, hash, "fresh edit"));
    }

    [AvaloniaFact]
    public async Task Unicode_edits_cannot_split_surrogates_or_introduce_binary_content()
    {
        var (_, path, _) = Paths("unicode");
        File.WriteAllText(path, "a😀b");
        await using var service = new WorkspaceDocumentService(watchFiles: false);
        var document = await service.OpenAsync(path);
        Assert.Throws<NotSupportedException>(() => document.Buffer.Remove(1, 1));
        Assert.Throws<NotSupportedException>(() => document.Buffer.Insert(2, "x"));
        Assert.Throws<NotSupportedException>(() => document.Buffer.Insert(0, "\0"));
        Assert.Equal("a😀b", document.Buffer.Text);
        document.Buffer.Replace(1, 2, "é");
        Assert.Equal("aéb", document.Buffer.Text);
    }

    [AvaloniaFact]
    public async Task Watcher_observation_never_replaces_dirty_text_and_save_checks_unobserved_disk_changes()
    {
        var (_, path, recovery) = Paths("conflict");
        File.WriteAllText(path, "base");
        await using var service = new WorkspaceDocumentService(recovery, watchFiles: false);
        var document = await service.OpenAsync(path);
        var hash = document.BaseHash;
        document.Buffer.Insert(0, "mine ");
        await service.FlushRecoveryAsync();
        File.WriteAllText(path, "external");
        await Assert.ThrowsAsync<DocumentConflictException>(() => document.SaveAsync());
        Assert.True(document.IsDirty);
        Assert.True(document.HasConflict);
        Assert.Equal("mine base", document.Buffer.Text);
        Assert.Equal(hash, document.BaseHash);
        Assert.Equal("external", File.ReadAllText(path));
        await service.ObserveExternalChangeAsync(path);
        Assert.Equal("mine base", document.Buffer.Text);
        Assert.True(document.HasConflict);
        Assert.False(document.ApplyEdit(document.Revision, hash, "agent"));
        await document.ReloadAsync();
        Assert.Equal("external", document.Buffer.Text);
        Assert.False(document.IsDirty);
        Assert.False(document.HasConflict);
        Assert.Empty(Directory.GetFiles(recovery, "*.json"));
    }

    [AvaloniaFact]
    public async Task Clean_external_reload_reuses_buffer_and_updates_encoding_and_base()
    {
        var (_, path, _) = Paths("clean-watch");
        File.WriteAllText(path, "old");
        await using var service = new WorkspaceDocumentService(watchFiles: false);
        var document = await service.OpenAsync(path);
        var buffer = document.Buffer;
        var revision = document.Revision;
        var bytes = Encoded(new UTF32Encoding(true, true, true), "new\r\ntext");
        File.WriteAllBytes(path, bytes);
        await service.ObserveExternalChangeAsync(path);
        Assert.Same(buffer, document.Buffer);
        Assert.Equal("new\r\ntext", buffer.Text);
        Assert.Equal("utf-32be", document.EncodingName);
        Assert.Equal(Hash(bytes), document.BaseHash);
        Assert.True(document.Revision > revision);
        Assert.False(document.IsDirty);
        Assert.False(document.HasConflict);
    }

    [AvaloniaFact]
    public async Task Invalid_external_content_and_failed_reload_preserve_buffer_and_journal()
    {
        var (_, path, recovery) = Paths("invalid-external");
        File.WriteAllText(path, "base");
        await using var service = new WorkspaceDocumentService(recovery, watchFiles: false);
        var document = await service.OpenAsync(path);
        document.Buffer.Insert(0, "draft ");
        await service.FlushRecoveryAsync();
        var journal = Assert.Single(Directory.GetFiles(recovery, "*.json"));
        var journalBytes = File.ReadAllBytes(journal);
        File.WriteAllBytes(path, [0xff, 0x00]);
        await service.ObserveExternalChangeAsync(path);
        Assert.True(document.HasConflict);
        Assert.Equal("draft base", document.Buffer.Text);
        await Assert.ThrowsAsync<NotSupportedException>(() => document.ReloadAsync());
        Assert.Equal("draft base", document.Buffer.Text);
        await service.FlushRecoveryAsync();
        Assert.Equal(journalBytes, File.ReadAllBytes(journal));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_restores_drafts_without_writing_disk_and_conflicts_with_changed_disk(bool externalChange)
    {
        var (_, path, recovery) = Paths("recover");
        var encoding = new UnicodeEncoding(true, true, true);
        File.WriteAllBytes(path, Encoded(encoding, "base\r\n"));
        string hash;
        long revision;
        await using (var original = new WorkspaceDocumentService(recovery, watchFiles: false))
        {
            var document = await original.OpenAsync(path);
            hash = document.BaseHash;
            document.Buffer.Insert(0, "recovered 😀\n");
            revision = document.Revision;
            await original.FlushRecoveryAsync();
        }
        if (externalChange) File.WriteAllBytes(path, Encoded(encoding, "external\r\n"));
        var diskBefore = File.ReadAllBytes(path);
        await using var reopened = new WorkspaceDocumentService(recovery, watchFiles: false);
        await reopened.DiscoverRecoveryAsync(CancellationToken.None);
        Assert.Single(reopened.RecoverablePaths);
        var recovered = await reopened.OpenAsync(path);
        Assert.True(recovered.IsRecovered);
        Assert.True(recovered.IsDirty);
        Assert.Equal(externalChange, recovered.HasConflict);
        Assert.Equal("recovered 😀\nbase\r\n", recovered.Buffer.Text);
        Assert.Equal("utf-16be", recovered.EncodingName);
        Assert.Equal(hash, recovered.BaseHash);
        Assert.True(recovered.Revision > revision);
        Assert.Equal(diskBefore, File.ReadAllBytes(path));
        if (externalChange)
        {
            await Assert.ThrowsAsync<DocumentConflictException>(() => recovered.SaveAsync());
            Assert.Equal(diskBefore, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(recovery, "*.json"));
        }
        else
        {
            await recovered.SaveAsync();
            Assert.Equal(Encoded(encoding, "recovered 😀\nbase\r\n"), File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(recovery, "*.json"));
        }
    }

    [AvaloniaFact]
    public async Task Deleted_original_can_be_recovered_and_exported_but_is_never_recreated_by_save()
    {
        var (root, path, recovery) = Paths("missing-recovery");
        File.WriteAllText(path, "base");
        await using (var service = new WorkspaceDocumentService(recovery, watchFiles: false))
        {
            var document = await service.OpenAsync(path);
            document.Buffer.Insert(0, "draft ");
            await service.FlushRecoveryAsync();
        }
        File.Delete(path);
        await using var reopened = new WorkspaceDocumentService(recovery, watchFiles: false);
        var recovered = await reopened.OpenAsync(path);
        Assert.True(recovered.HasConflict);
        Assert.True(recovered.IsDirty);
        Assert.Equal("draft base", recovered.Buffer.Text);
        await Assert.ThrowsAsync<DocumentConflictException>(() => recovered.SaveAsync());
        Assert.False(File.Exists(path));
        var copy = System.IO.Path.Combine(root, "recovered.txt");
        await recovered.SaveCopyAsync(copy);
        Assert.Equal("draft base", File.ReadAllText(copy));
        Assert.True(recovered.IsDirty);
        Assert.Single(Directory.GetFiles(recovery, "*.json"));
        await Assert.ThrowsAnyAsync<IOException>(() => recovered.SaveCopyAsync(copy));
        await Assert.ThrowsAnyAsync<IOException>(() => recovered.SaveCopyAsync(path));
    }

    [AvaloniaFact]
    public async Task Deleting_a_dirty_open_file_marks_conflict_without_recreating_it()
    {
        var (_, path, recovery) = Paths("deleted-open");
        File.WriteAllText(path, "base");
        await using var service = new WorkspaceDocumentService(recovery, watchFiles: false);
        var document = await service.OpenAsync(path);
        document.Buffer.Insert(0, "draft ");
        await service.FlushRecoveryAsync();
        File.Delete(path);
        await Assert.ThrowsAsync<DocumentConflictException>(() => document.SaveAsync());
        Assert.True(document.HasConflict);
        Assert.True(document.IsDirty);
        Assert.Equal("draft base", document.Buffer.Text);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(recovery, "*.json"));
    }

    [AvaloniaFact]
    public async Task Recovery_left_after_a_completed_save_is_not_reapplied_over_identical_disk_bytes()
    {
        var (_, path, recovery) = Paths("saved-recovery");
        File.WriteAllText(path, "base");
        await using (var service = new WorkspaceDocumentService(recovery, watchFiles: false))
        {
            var document = await service.OpenAsync(path);
            document.Buffer.Insert(0, "draft ");
            await service.FlushRecoveryAsync();
        }
        // A crash after replacing the file but before deleting recovery leaves this state.
        File.WriteAllText(path, "draft ");
        File.AppendAllText(path, "base");
        var bytes = File.ReadAllBytes(path);
        await using var reopened = new WorkspaceDocumentService(recovery, watchFiles: false);
        var recovered = await reopened.OpenAsync(path);
        Assert.Equal("draft base", recovered.Buffer.Text);
        Assert.False(recovered.IsDirty);
        Assert.False(recovered.HasConflict);
        Assert.False(recovered.IsRecovered);
        Assert.Equal(Hash(bytes), recovered.BaseHash);
        await reopened.FlushRecoveryAsync();
        Assert.Empty(Directory.GetFiles(recovery, "*.json"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [AvaloniaFact]
    public async Task Corrupt_recovery_is_reported_and_retained_on_edit_flush_save_and_dispose_errors()
    {
        var (_, path, recovery) = Paths("corrupt-recovery");
        File.WriteAllText(path, "disk");
        Directory.CreateDirectory(recovery);
        var journal = DocumentRecoveryStore.PathFor(recovery, DocumentFile.CanonicalPath(path));
        const string corrupt = "{broken recovery";
        File.WriteAllText(journal, corrupt);
        var service = new WorkspaceDocumentService(recovery, watchFiles: false);
        var document = await service.OpenAsync(path);
        Assert.NotNull(document.RecoveryError);
        Assert.Contains("retained", document.Status);
        document.Buffer.Insert(0, "new ");
        await Assert.ThrowsAnyAsync<IOException>(() => service.FlushRecoveryAsync());
        await Assert.ThrowsAnyAsync<IOException>(() => document.SaveAsync());
        Assert.Equal("disk", File.ReadAllText(path));
        Assert.Equal("new disk", document.Buffer.Text);
        Assert.Equal(corrupt, File.ReadAllText(journal));
        await Assert.ThrowsAnyAsync<IOException>(() => service.DisposeAsync().AsTask());
        Assert.Equal(corrupt, File.ReadAllText(journal));
    }

    [AvaloniaFact]
    public async Task Failed_journal_write_keeps_dirty_text_and_can_be_flushed_after_storage_is_repaired()
    {
        var (_, path, recovery) = Paths("recovery-write");
        File.WriteAllText(path, "base");
        await using var service = new WorkspaceDocumentService(recovery, watchFiles: false);
        var document = await service.OpenAsync(path);
        var journal = DocumentRecoveryStore.PathFor(recovery, document.FullPath);
        Directory.CreateDirectory(journal);
        document.Buffer.Insert(0, "draft ");
        await Assert.ThrowsAnyAsync<IOException>(() => service.FlushRecoveryAsync());
        Assert.True(document.IsDirty);
        Assert.Equal("draft base", document.Buffer.Text);
        Assert.True(Directory.Exists(journal));
        Assert.Equal("base", File.ReadAllText(path));
        Directory.Delete(journal);
        await service.FlushRecoveryAsync();
        Assert.True(File.Exists(journal));
        Assert.Empty(Directory.GetFiles(recovery, "*.tmp-*"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(journal));
    }

    [AvaloniaFact]
    public async Task Recovery_journal_has_one_writer_lease_and_releases_it_on_dispose()
    {
        var (_, path, recovery) = Paths("lease");
        File.WriteAllText(path, "base");
        var first = new WorkspaceDocumentService(recovery, watchFiles: false);
        var document = await first.OpenAsync(path);
        document.Buffer.Insert(0, "first ");
        await first.FlushRecoveryAsync();
        await using var second = new WorkspaceDocumentService(recovery, watchFiles: false);
        await Assert.ThrowsAnyAsync<IOException>(() => second.OpenAsync(path));
        await first.DisposeAsync();
        var recovered = await second.OpenAsync(path);
        Assert.Equal("first base", recovered.Buffer.Text);
        Assert.True(recovered.IsRecovered);
    }

    [AvaloniaFact]
    public async Task Null_recovery_is_memory_only_and_separate_owners_do_not_share_buffers()
    {
        var (root, path, _) = Paths("memory");
        File.WriteAllText(path, "base");
        await using var first = new WorkspaceDocumentService(null, watchFiles: false);
        await using var second = new WorkspaceDocumentService(null, watchFiles: false);
        var one = await first.OpenAsync(path);
        var two = await second.OpenAsync(path);
        Assert.NotSame(one.Buffer, two.Buffer);
        one.Buffer.Insert(0, "one ");
        await first.FlushRecoveryAsync();
        Assert.Equal("base", two.Buffer.Text);
        Assert.Single(Directory.GetFiles(root));
        Assert.Empty(Directory.GetDirectories(root));
    }

    [AvaloniaFact]
    public async Task Physical_file_and_directory_symlink_aliases_share_buffer_and_save_preserves_links_and_mode()
    {
        if (OperatingSystem.IsWindows()) return; // Windows link creation requires developer mode or elevation.
        var (root, path, _) = Paths("aliases");
        File.WriteAllText(path, "base");
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead;
        File.SetUnixFileMode(path, mode);
        var link = System.IO.Path.Combine(root, "alias.txt");
        File.CreateSymbolicLink(link, path);
        var directory = System.IO.Path.Combine(root, "directory-alias");
        Directory.CreateSymbolicLink(directory, root);
        await using var service = new WorkspaceDocumentService(watchFiles: false);
        var document = await service.OpenAsync(path);
        Assert.Same(document, await service.OpenAsync(link));
        Assert.Same(document, await service.OpenAsync(System.IO.Path.Combine(directory, "source.txt")));
        Assert.True(service.TryGet(link, out var found));
        Assert.Same(document, found);
        Assert.True(document.IsSymlink);
        Assert.Equal(new Uri(DocumentFile.CanonicalPath(path)).AbsoluteUri, document.Uri);
        document.Buffer.Insert(0, "edited ");
        await document.SaveAsync();
        Assert.Equal("edited base", File.ReadAllText(path));
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal(mode, File.GetUnixFileMode(path));
        Assert.Single(service.Documents);
    }

    [AvaloniaFact]
    public async Task Replacing_the_canonical_file_with_a_symlink_cannot_redirect_a_save()
    {
        if (OperatingSystem.IsWindows()) return;
        var (root, path, _) = Paths("retarget");
        var other = System.IO.Path.Combine(root, "other.txt");
        File.WriteAllText(path, "base");
        File.WriteAllText(other, "other");
        await using var service = new WorkspaceDocumentService(watchFiles: false);
        var document = await service.OpenAsync(path);
        document.Buffer.Insert(0, "mine ");
        File.Delete(path);
        File.CreateSymbolicLink(path, other);
        await Assert.ThrowsAsync<DocumentConflictException>(() => document.SaveAsync());
        Assert.Equal("other", File.ReadAllText(other));
        Assert.Equal("mine base", document.Buffer.Text);
        Assert.True(document.HasConflict);
        Assert.NotNull(new FileInfo(path).LinkTarget);
    }

    [AvaloniaFact]
    public async Task Readonly_file_is_honest_and_never_replaced()
    {
        var (_, path, _) = Paths("readonly");
        File.WriteAllText(path, "readonly");
        var attributes = File.GetAttributes(path);
        File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
        try
        {
            await using var service = new WorkspaceDocumentService(watchFiles: false);
            var document = await service.OpenAsync(path);
            Assert.True(document.IsReadOnly);
            Assert.False(document.IsEditable);
            Assert.Contains("Read-only", document.Status);
            Assert.Throws<InvalidOperationException>(() => document.Buffer.Insert(0, "bad"));
            Assert.False(document.ApplyEdit(document.Revision, document.BaseHash, "bad"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => document.SaveAsync());
            Assert.Equal("readonly", File.ReadAllText(path));
        }
        finally { File.SetAttributes(path, attributes); }
    }

    [AvaloniaFact]
    public async Task Service_watchers_observe_hidden_dirty_documents_without_a_view()
    {
        var (_, path, _) = Paths("watcher");
        File.WriteAllText(path, "base");
        await using var service = new WorkspaceDocumentService();
        var document = await service.OpenAsync(path);
        document.Buffer.Insert(0, "hidden ");
        File.WriteAllText(path, "external");
        await TestProcesses.Eventually(() => document.HasConflict, conflict => conflict, TimeSpan.FromSeconds(10), "hidden dirty document observed");
        Assert.Equal("hidden base", document.Buffer.Text);
        Assert.True(document.IsDirty);
        Assert.Equal("external", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task Agent_disk_write_under_unsaved_text_reload_keeps_the_users_text_as_a_copy()
    {
        var (_, path, _) = Paths("agent-reload-copy");
        File.WriteAllText(path, "base\r\n");
        await using var service = new WorkspaceDocumentService(watchFiles: false);
        var document = await service.OpenAsync(path);
        document.Buffer.Insert(0, "mine ");
        // The engine writes disk when the buffer is dirty (the UI refuses a buffer edit).
        Assert.False(document.ApplyEdit(document.Revision, document.BaseHash, "agent\r\n"));
        File.WriteAllText(path, "agent\r\n");
        await service.ObserveExternalChangeAsync(path);
        Assert.True(document.HasConflict);
        Assert.True(document.CanKeepMine);
        Assert.Equal("mine base\r\n", document.Buffer.Text);

        var copy = await document.ReloadKeepingCopyAsync();
        Assert.Equal(System.IO.Path.GetDirectoryName(document.FullPath), System.IO.Path.GetDirectoryName(copy));
        Assert.Equal("mine base\r\n", File.ReadAllText(copy));
        Assert.Equal("agent\r\n", document.Buffer.Text);
        Assert.False(document.IsDirty);
        Assert.False(document.HasConflict);
        Assert.Equal("agent\r\n", File.ReadAllText(path));
        // A second copy never overwrites the first.
        document.Buffer.Insert(0, "again ");
        File.WriteAllText(path, "agent 2\r\n");
        await service.ObserveExternalChangeAsync(path);
        var second = await document.ReloadKeepingCopyAsync();
        Assert.NotEqual(copy, second);
        Assert.Equal("mine base\r\n", File.ReadAllText(copy));
        Assert.Equal("again agent\r\n", File.ReadAllText(second));
    }

    [AvaloniaFact]
    public async Task Keep_mine_accepts_the_changed_disk_as_base_and_save_writes_the_users_text()
    {
        var (_, path, recovery) = Paths("agent-keep-mine");
        File.WriteAllText(path, "base");
        await using var service = new WorkspaceDocumentService(recovery, watchFiles: false);
        var document = await service.OpenAsync(path);
        document.Buffer.Insert(0, "mine ");
        File.WriteAllText(path, "agent");
        await service.ObserveExternalChangeAsync(path);
        await Assert.ThrowsAsync<DocumentConflictException>(() => document.SaveAsync());
        Assert.Equal("agent", File.ReadAllText(path));

        document.KeepMine();
        Assert.False(document.HasConflict);
        Assert.True(document.IsDirty);
        Assert.Equal(Hash(File.ReadAllBytes(path)), document.BaseHash);
        Assert.Equal("mine base", document.Buffer.Text);
        await document.SaveAsync();
        Assert.Equal("mine base", File.ReadAllText(path));
        Assert.False(document.IsDirty);
        // Undo still reaches the user's earlier states; nothing was discarded.
        document.Buffer.UndoStack.Undo();
        Assert.Equal("base", document.Buffer.Text);
    }
}
