using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ombra.Server.Logs;

public sealed record ChainEntry(long Seq, string PrevHash, DateTimeOffset At, JsonObject Entry, string Hash);

public sealed class ChainBrokenException(string message) : Exception(message);

/// <summary>
/// Append-only hash-chained journal (revocations.log, blob-deletions.log — §1, N11).
/// Line format: <c>&lt;hash hex&gt; &lt;body json&gt;\n</c>, where body = <c>{"seq":n,"prev":"&lt;hex&gt;","at":"&lt;iso&gt;","entry":{...}}</c>
/// and hash = SHA-256(prevHash bytes ‖ UTF-8 body). The first entry has prev = 64 zeros.
/// Appends take an exclusive lock on the file (server and ombra-admin may both write) and fsync before returning.
/// An optional sink gets the identical line (off-host copy); a sink failure never blocks the local append.
/// </summary>
public sealed class HashChainLog(string path, string? sinkPath = null, Action<Exception>? onSinkError = null)
{
    public static readonly string ZeroHash = new('0', 64);

    public string Path { get; } = path;

    public ChainEntry Append(JsonObject entry, DateTimeOffset at)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        string line;
        ChainEntry result;
        using (var fs = OpenLocked(Path))
        {
            var (lastSeq, lastHash) = ReadTail(fs);
            var body = new JsonObject
            {
                ["seq"] = lastSeq + 1,
                ["prev"] = lastHash,
                ["at"] = at.UtcDateTime.ToString("O"),
                ["entry"] = entry.DeepClone(),
            }.ToJsonString();
            var hash = ComputeHash(lastHash, body);
            line = hash + " " + body + "\n";
            fs.Seek(0, SeekOrigin.End);
            var bytes = Encoding.UTF8.GetBytes(line);
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
            result = new ChainEntry(lastSeq + 1, lastHash, at, entry, hash);
        }
        if (sinkPath is not null)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(sinkPath))!);
                using var sink = OpenLocked(sinkPath);
                sink.Seek(0, SeekOrigin.End);
                sink.Write(Encoding.UTF8.GetBytes(line));
                sink.Flush(flushToDisk: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                onSinkError?.Invoke(e);
            }
        }
        return result;
    }

    /// <summary>Reads and verifies the whole chain. Throws <see cref="ChainBrokenException"/> on any mismatch.</summary>
    public static IReadOnlyList<ChainEntry> ReadVerified(string path)
    {
        if (!File.Exists(path)) return [];
        var list = new List<ChainEntry>();
        var prev = ZeroHash;
        var lineNo = 0;
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            lineNo++;
            if (raw.Length == 0) continue;
            var sp = raw.IndexOf(' ');
            if (sp != 64) throw new ChainBrokenException($"{path}:{lineNo}: malformed line");
            var hash = raw[..64];
            var body = raw[65..];
            if (!string.Equals(ComputeHash(prev, body), hash, StringComparison.Ordinal))
                throw new ChainBrokenException($"{path}:{lineNo}: hash mismatch");
            JsonObject obj;
            try { obj = JsonNode.Parse(body)!.AsObject(); }
            catch (JsonException) { throw new ChainBrokenException($"{path}:{lineNo}: body is not JSON"); }
            var seq = obj["seq"]!.GetValue<long>();
            if (seq != list.Count + 1 || obj["prev"]!.GetValue<string>() != prev)
                throw new ChainBrokenException($"{path}:{lineNo}: sequence/prev mismatch");
            list.Add(new ChainEntry(seq, prev, DateTimeOffset.Parse(obj["at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
                obj["entry"]!.AsObject(), hash));
            prev = hash;
        }
        return list;
    }

    /// <summary>
    /// Merges a local and an off-host copy: one must be a prefix of the other (same hashes); the longer wins.
    /// Divergent copies mean tampering and throw.
    /// </summary>
    public static IReadOnlyList<ChainEntry> Reconcile(IReadOnlyList<ChainEntry> a, IReadOnlyList<ChainEntry> b)
    {
        var n = Math.Min(a.Count, b.Count);
        for (var i = 0; i < n; i++)
            if (a[i].Hash != b[i].Hash)
                throw new ChainBrokenException($"copies diverge at seq {i + 1}");
        return a.Count >= b.Count ? a : b;
    }

    public static string ComputeHash(string prevHex, string body)
    {
        var prev = Convert.FromHexString(prevHex);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var buf = new byte[prev.Length + bodyBytes.Length];
        prev.CopyTo(buf, 0);
        bodyBytes.CopyTo(buf, prev.Length);
        return Convert.ToHexStringLower(SHA256.HashData(buf));
    }

    private static FileStream OpenLocked(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>Last (seq, hash) without reading the whole file; the tail line is re-verified by ReadVerified elsewhere.</summary>
    private static (long Seq, string Hash) ReadTail(FileStream fs)
    {
        if (fs.Length == 0) return (0, ZeroHash);
        var window = (int)Math.Min(fs.Length, 64 * 1024);
        var buf = new byte[window];
        fs.Seek(-window, SeekOrigin.End);
        fs.ReadExactly(buf);
        var text = Encoding.UTF8.GetString(buf).TrimEnd('\n');
        var start = text.LastIndexOf('\n') + 1;
        var last = text[start..];
        if (last.Length < 66 || last[64] != ' ')
            throw new ChainBrokenException($"{fs.Name}: malformed tail line");
        var body = JsonNode.Parse(last[65..])!.AsObject();
        return (body["seq"]!.GetValue<long>(), last[..64]);
    }
}
