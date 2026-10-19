using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ombra.App.Services.Documents;

internal sealed record DocumentEncoding(string Id, Encoding Codec, bool HasBom)
{
    internal static DocumentEncoding FromId(string id, bool bom) => id switch
    {
        "utf-8" => new(id, new UTF8Encoding(bom, true), bom),
        "utf-16le" => new(id, new UnicodeEncoding(false, bom, true), bom),
        "utf-16be" => new(id, new UnicodeEncoding(true, bom, true), bom),
        "utf-32le" => new(id, new UTF32Encoding(false, bom, true), bom),
        "utf-32be" => new(id, new UTF32Encoding(true, bom, true), bom),
        _ => throw new NotSupportedException("Unknown document encoding.")
    };

    internal static (DocumentEncoding Encoding, string Text) Decode(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes;
        var (id, skip) = data.StartsWith<byte>([0xff, 0xfe, 0, 0]) ? ("utf-32le", 4)
            : data.StartsWith<byte>([0, 0, 0xfe, 0xff]) ? ("utf-32be", 4)
            : data.StartsWith<byte>([0xef, 0xbb, 0xbf]) ? ("utf-8", 3)
            : data.StartsWith<byte>([0xff, 0xfe]) ? ("utf-16le", 2)
            : data.StartsWith<byte>([0xfe, 0xff]) ? ("utf-16be", 2) : ("utf-8", 0);
        var encoding = FromId(id, skip != 0);
        try
        {
            var text = encoding.Codec.GetString(data[skip..]);
            ValidateText(text);
            return (encoding, text);
        }
        catch (DecoderFallbackException e)
        {
            throw new NotSupportedException("The file is not valid UTF-8 or BOM-marked UTF-16/UTF-32; no replacement characters were inserted.", e);
        }
    }

    internal static void ValidateText(string text)
    {
        if (text.Length > WorkspaceDocumentService.MaxTextLength)
            throw new NotSupportedException("The file exceeds the editor's 4 Mi UTF-16 character limit; it was not truncated.");
        var high = false;
        foreach (var c in text)
        {
            if (IsBinaryCharacter(c)) throw new NotSupportedException("Binary content cannot be edited as text.");
            if (high && !char.IsLowSurrogate(c) || !high && char.IsLowSurrogate(c))
                throw new NotSupportedException("The text contains an unpaired UTF-16 surrogate.");
            high = char.IsHighSurrogate(c);
        }
        if (high) throw new NotSupportedException("The text contains an unpaired UTF-16 surrogate.");
    }

    internal static bool IsBinaryCharacter(char c) => c < ' ' && c is not ('\t' or '\n' or '\r' or '\f');

    internal int ValidateForSave(string text)
    {
        ValidateText(text);
        var length = Codec.GetByteCount(text);
        if (length > WorkspaceDocumentService.MaxFileBytes - Codec.Preamble.Length)
            throw new NotSupportedException("The encoded document exceeds the editor's 8 MiB limit; no bytes were written.");
        return length;
    }

    internal byte[] Encode(string text)
    {
        var length = ValidateForSave(text);
        var preamble = Codec.GetPreamble();
        var bytes = new byte[preamble.Length + length];
        preamble.CopyTo(bytes, 0);
        Codec.GetBytes(text, bytes.AsSpan(preamble.Length));
        return bytes;
    }

    internal static string FirstNewLine(string text)
    {
        var index = text.AsSpan().IndexOfAny('\r', '\n');
        return index < 0 ? "\n" : text[index] == '\n' ? "\n"
            : index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r";
    }
}

internal sealed record DocumentDiskSnapshot(string Text, DocumentEncoding Encoding, string Hash, bool IsReadOnly);

/// <summary>Strict, bounded reads and same-directory, hash-checked atomic replacement. Links are resolved before admission.</summary>
internal static class DocumentFile
{
    internal static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string CanonicalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // realpath follows directory links before interpreting '..', unlike lexical GetFullPath.
        if (!OperatingSystem.IsWindows())
        {
            var pointer = RealPath(path, IntPtr.Zero);
            if (pointer != IntPtr.Zero)
            {
                try { return Marshal.PtrToStringUTF8(pointer) ?? throw new IOException("Could not decode the physical file path."); }
                finally { Free(pointer); }
            }
            var error = Marshal.GetLastPInvokeError();
            if (error is 13 or 1) throw new UnauthorizedAccessException($"Cannot resolve the physical file path: {path}", new Win32Exception(error));
            if (error != 2) throw new IOException($"Cannot resolve the physical file path: {path}", new Win32Exception(error));
        }
        else if (File.Exists(path) || Directory.Exists(path))
        {
            using var handle = OpenPath(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException("Cannot open the physical file path.", new Win32Exception(Marshal.GetLastPInvokeError()));
            var chars = new char[32768];
            var length = GetFinalPathNameByHandle(handle, chars, (uint)chars.Length, 0);
            if (length == 0 || length >= chars.Length) throw new IOException("Cannot resolve the physical file path.", new Win32Exception(Marshal.GetLastPInvokeError()));
            var final = new string(chars, 0, (int)length);
            return final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + final[8..]
                : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
        }
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        if (parent is null || parent == full) return full;
        // A missing file may have a recovery journal. Still resolve every existing parent.
        parent = CanonicalPath(parent);
        var missing = Path.Combine(parent, Path.GetFileName(full));
        if (new FileInfo(missing).LinkTarget is not null)
            throw new IOException("The file is a dangling symbolic link; it cannot be safely edited.");
        return missing;
    }

    internal static async Task<DocumentDiskSnapshot> ReadAsync(string path, CancellationToken ct)
    {
        EnsureIdentity(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = await ReadBytesAsync(stream, ct).ConfigureAwait(false);
        var (encoding, text) = DocumentEncoding.Decode(bytes);
        EnsureIdentity(path);
        return new(text, encoding, Hash(bytes), IsReadOnly(path));
    }

    private static async Task<byte[]> ReadBytesAsync(FileStream stream, CancellationToken ct)
    {
        var length = stream.Length;
        if (length > WorkspaceDocumentService.MaxFileBytes)
            throw new NotSupportedException("The file exceeds the editor's 8 MiB limit; it was not truncated.");
        var bytes = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        var extra = new byte[1];
        if (await stream.ReadAsync(extra, ct).ConfigureAwait(false) != 0)
            throw new IOException("The file changed while it was being read; reopen it.");
        return bytes;
    }

    internal static bool IsReadOnly(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) return true;
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode write = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
            if ((File.GetUnixFileMode(path) & write) == 0 || (File.GetUnixFileMode(Path.GetDirectoryName(path)!) & write) == 0) return true;
            if (Access(path, 2) != 0 || Access(Path.GetDirectoryName(path)!, 2) != 0) return true;
        }
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return true; } // A write-denying lease is also not currently editable.
    }

    internal static void EnsureIdentity(string path)
    {
        if (!PathComparer.Equals(path, CanonicalPath(path)))
            throw new DocumentConflictException("The document's physical path now points elsewhere. Reopen the new target; the buffer was preserved.");
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static async Task SaveAsync(string path, byte[] bytes, string expectedHash, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".ombra-save-" + Guid.NewGuid().ToString("N"));
        string? displaced = null;
        try
        {
            EnsureIdentity(path);
            if (IsReadOnly(path)) throw new UnauthorizedAccessException("The file or its directory is read-only.");
            await StageAsync(temp, bytes, OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path), ct).ConfigureAwait(false);
            EnsureIdentity(path);
            // Keep the old inode open through replacement. On Windows deny concurrent writes, but permit rename.
            using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (original.Length > WorkspaceDocumentService.MaxFileBytes)
                throw new DocumentConflictException("The file grew beyond the editor limit; your buffer was preserved.");
            var actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(original, ct).ConfigureAwait(false));
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new DocumentConflictException("The file changed on disk. Your unsaved buffer was preserved; reload explicitly or save a copy.");
            ct.ThrowIfCancellationRequested();
            EnsureIdentity(path);
            displaced = SwapKeepingDisplaced(temp, path);
            try
            {
                if (new FileInfo(displaced).LinkTarget is not null)
                    throw new IOException("The displaced file is now a symbolic link.");
                using var previous = new FileStream(displaced, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var displacedHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(previous, CancellationToken.None).ConfigureAwait(false));
                if (!string.Equals(displacedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Another writer changed the file during the atomic save.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new DocumentConflictException($"The save raced another writer. The displaced disk version is retained at '{displaced}'; your buffer and recovery are retained.", e);
            }
            DeleteTemporary(displaced);
            displaced = null;
        }
        catch (FileNotFoundException e) { throw new DocumentConflictException("The file was removed from disk. Your unsaved buffer was preserved.", e); }
        catch (DirectoryNotFoundException e) { throw new DocumentConflictException("The file's directory was removed. Your unsaved buffer was preserved.", e); }
        finally { if (displaced != temp) DeleteTemporary(temp); }
    }

    private static string SwapKeepingDisplaced(string staged, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            var backup = staged + ".displaced";
            File.Replace(staged, target, backup, ignoreMetadataErrors: false);
            return backup;
        }
        int result;
        try
        {
            result = OperatingSystem.IsMacOS() ? RenameSwap(staged, target, 2)
                : OperatingSystem.IsLinux() ? RenameExchange(-100, staged, -100, target, 2)
                : throw new NotSupportedException("Atomic save with displaced-version protection is unavailable on this platform.");
        }
        catch (EntryPointNotFoundException e)
        {
            throw new NotSupportedException("This platform cannot atomically preserve the displaced file; save a copy instead.", e);
        }
        if (result != 0)
            throw new IOException("Atomic save with displaced-version protection failed; the original file was not replaced.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        return staged;
    }

    internal static async Task SaveCopyAsync(string destination, byte[] bytes, CancellationToken ct)
    {
        var path = CanonicalPath(destination);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".ombra-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            await StageAsync(temp, bytes, null, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            EnsureIdentity(path);
            File.Move(temp, path, overwrite: false);
        }
        finally { DeleteTemporary(temp); }
    }

    internal static async Task StageAsync(string path, byte[] bytes, UnixFileMode? mode, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows() && mode is { } permissions) File.SetUnixFileMode(path, permissions);
        stream.Flush(flushToDisk: true);
    }

    internal static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);
    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);
    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int Access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
    [DllImport("libc", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int RenameSwap([MarshalAs(UnmanagedType.LPUTF8Str)] string first, [MarshalAs(UnmanagedType.LPUTF8Str)] string second, uint flags);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameExchange(int firstDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string first,
        int secondDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string second, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenPath(string path, uint access, FileShare share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);
}

public sealed class DocumentConflictException(string message, Exception? inner = null) : IOException(message, inner);
