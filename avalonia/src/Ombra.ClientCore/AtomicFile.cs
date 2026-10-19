using System.Text;

namespace Ombra.ClientCore;

internal static class AtomicFile
{
    // Keep the lock file: deleting it can give waiting writers different lock identities.
    internal static FileStream AcquireLock(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 49) { Thread.Sleep(100); }
        }
    }

    internal static void WriteAllText(string path, string content, Encoding encoding)
    {
        var temp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, encoding))
                writer.Write(content);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
