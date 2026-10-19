using System.Formats.Cbor;

namespace Ombra.Server.Contracts;

/// <summary>Strict reader for a canonical CBOR map with text keys (envelope and envelope bodies).</summary>
public sealed class CborMap
{
    private readonly Dictionary<string, ReadOnlyMemory<byte>> _values;

    private CborMap(Dictionary<string, ReadOnlyMemory<byte>> values) => _values = values;

    public static CborMap Read(ReadOnlyMemory<byte> encoded)
    {
        try
        {
            var r = new CborReader(encoded, CborConformanceMode.Ctap2Canonical);
            var count = r.ReadStartMap() ?? throw new WireFormatException("indefinite map");
            var values = new Dictionary<string, ReadOnlyMemory<byte>>(count, StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var key = r.ReadTextString();
                values[key] = r.ReadEncodedValue();
            }
            r.ReadEndMap();
            if (r.BytesRemaining != 0)
                throw new WireFormatException("trailing bytes after CBOR map");
            return new CborMap(values);
        }
        catch (CborContentException e)
        {
            throw new WireFormatException("not canonical CBOR: " + e.Message);
        }
        catch (InvalidOperationException e)
        {
            throw new WireFormatException("unexpected CBOR structure: " + e.Message);
        }
    }

    public IReadOnlyCollection<string> Keys => _values.Keys;

    public void RequireExactly(params string[] keys)
    {
        if (_values.Count != keys.Length || keys.Any(k => !_values.ContainsKey(k)))
            throw new WireFormatException("expected keys {" + string.Join(",", keys) + "}, got {" + string.Join(",", _values.Keys) + "}");
    }

    public bool Has(string key) => _values.ContainsKey(key);

    public ReadOnlyMemory<byte> Raw(string key) =>
        _values.TryGetValue(key, out var v) ? v : throw new WireFormatException("missing key " + key);

    private CborReader Reader(string key) => new(Raw(key), CborConformanceMode.Ctap2Canonical);

    public ulong UInt(string key) => Wrap(key, r => r.ReadUInt64());
    public string Text(string key) => Wrap(key, r => r.ReadTextString());
    public byte[] Bytes(string key) => Wrap(key, r => r.ReadByteString());
    public bool Bool(string key) => Wrap(key, r => r.ReadBoolean());
    public Guid Uuid(string key)
    {
        var b = Bytes(key);
        if (b.Length != 16) throw new WireFormatException(key + " must be 16 bytes");
        return Ids.FromBytes(b);
    }
    public byte[] Bytes(string key, int length)
    {
        var b = Bytes(key);
        if (b.Length != length) throw new WireFormatException($"{key} must be {length} bytes");
        return b;
    }
    public CborMap Map(string key) => Read(Raw(key));
    public IReadOnlyList<CborMap> MapArray(string key) => Wrap(key, r =>
    {
        var n = r.ReadStartArray() ?? throw new WireFormatException("indefinite array");
        var list = new List<CborMap>(n);
        for (var i = 0; i < n; i++) list.Add(Read(r.ReadEncodedValue()));
        r.ReadEndArray();
        return list;
    });

    private T Wrap<T>(string key, Func<CborReader, T> read)
    {
        try
        {
            var r = Reader(key);
            var v = read(r);
            if (r.BytesRemaining != 0) throw new WireFormatException("trailing bytes in " + key);
            return v;
        }
        catch (CborContentException e) { throw new WireFormatException($"{key}: {e.Message}"); }
        catch (InvalidOperationException e) { throw new WireFormatException($"{key}: {e.Message}"); }
        catch (OverflowException e) { throw new WireFormatException($"{key}: {e.Message}"); }
    }
}

/// <summary>Canonical CBOR map writer; keys are sorted by the canonical writer.</summary>
public sealed class CborMapWriter
{
    private readonly List<(string Key, Action<CborWriter> Write)> _entries = [];

    public CborMapWriter UInt(string key, ulong value) { _entries.Add((key, w => w.WriteUInt64(value))); return this; }
    public CborMapWriter Text(string key, string value) { _entries.Add((key, w => w.WriteTextString(value))); return this; }
    public CborMapWriter Bytes(string key, ReadOnlyMemory<byte> value) { _entries.Add((key, w => w.WriteByteString(value.Span))); return this; }
    public CborMapWriter Bool(string key, bool value) { _entries.Add((key, w => w.WriteBoolean(value))); return this; }
    public CborMapWriter Uuid(string key, Guid value) => Bytes(key, Ids.ToBytes(value));
    public CborMapWriter Map(string key, CborMapWriter value) { var enc = value.Encode(); _entries.Add((key, w => w.WriteEncodedValue(enc))); return this; }
    public CborMapWriter MapArray(string key, IEnumerable<CborMapWriter> values)
    {
        var enc = values.Select(v => v.Encode()).ToList();
        _entries.Add((key, w =>
        {
            w.WriteStartArray(enc.Count);
            foreach (var e in enc) w.WriteEncodedValue(e);
            w.WriteEndArray();
        }));
        return this;
    }

    public byte[] Encode()
    {
        var w = new CborWriter(CborConformanceMode.Ctap2Canonical);
        w.WriteStartMap(_entries.Count);
        foreach (var (key, write) in _entries)
        {
            w.WriteTextString(key);
            write(w);
        }
        w.WriteEndMap();
        return w.Encode();
    }
}

public static class Ids
{
    /// <summary>UUID as 16 bytes big-endian (RFC 9562 byte order), as in all signed formats.</summary>
    public static byte[] ToBytes(Guid id) => id.ToByteArray(bigEndian: true);
    public static Guid FromBytes(ReadOnlySpan<byte> b) => new(b, bigEndian: true);

    /// <summary>32-byte team/group identifiers travel as lowercase hex.</summary>
    public static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(b);

    public static bool TryParseHash(string? s, out byte[] value)
    {
        value = [];
        if (s is null || s.Length != 64) return false;
        try { value = Convert.FromHexString(s); return true; }
        catch (FormatException) { return false; }
    }
}
