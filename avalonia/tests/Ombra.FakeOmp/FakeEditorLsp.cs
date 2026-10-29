using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ombra.FakeOmp;

/// <summary>
/// The engine's <c>editor_document</c> requests played without a real server, in every scenario: <c>capabilities</c>
/// (formatting, range formatting and symbols for every file but <c>*.txt</c>), <c>formatting</c> / <c>rangeFormatting</c>
/// (trailing spaces removed, as TextEdits), <c>documentSymbol</c> (<c>class</c>/<c>def</c>/<c>function</c> lines,
/// nested by indentation). Other methods fail as before: "The fake engine does not run language servers".
/// </summary>
internal sealed partial class FakeEditorLsp
{
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);

    /// <summary>Answers one <c>editor_document</c> command: (data, error).</summary>
    public (object? Data, string? Error) Handle(JsonNode? cmd)
    {
        var action = cmd?["action"]?.GetValue<string>();
        var uri = cmd?["uri"]?.GetValue<string>() ?? "";
        var version = cmd?["version"]?.GetValue<long>() ?? 0;
        if (action is "open" or "change" && cmd?["text"]?.GetValue<string>() is { } text) _texts[uri] = text;
        if (action == "close") _texts.Remove(uri);
        if (action != "request") return (new { }, null);
        var method = cmd?["method"]?.GetValue<string>();
        var plain = uri.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        var current = _texts.GetValueOrDefault(uri, "");
        JsonNode? result;
        switch (method)
        {
            case "capabilities":
                result = new JsonObject { ["formatting"] = !plain, ["rangeFormatting"] = !plain, ["documentSymbol"] = !plain, ["server"] = "fake" };
                break;
            case "formatting" when !plain:
                result = TrimEdits(current, 0, int.MaxValue);
                break;
            case "rangeFormatting" when !plain:
                result = TrimEdits(current, cmd?["position"]?["line"]?.GetValue<int>() ?? 0, cmd?["end"]?["line"]?.GetValue<int>() ?? 0);
                break;
            case "documentSymbol" when !plain:
                result = Symbols(current);
                break;
            default:
                return (null, "The fake engine does not run language servers");
        }
        return (new JsonObject { ["version"] = version, ["result"] = result, ["positionEncoding"] = "utf-16" }, null);
    }

    private static JsonArray TrimEdits(string text, int first, int last)
    {
        var edits = new JsonArray();
        var lines = text.Split('\n');
        for (var i = Math.Max(0, first); i < lines.Length && i <= last; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var kept = line.TrimEnd(' ', '\t').Length;
            if (kept == line.Length) continue;
            edits.Add(new JsonObject { ["range"] = Range(i, kept, i, line.Length), ["newText"] = "" });
        }
        return edits;
    }

    private static JsonArray Symbols(string text)
    {
        var lines = text.Split('\n');
        var found = new List<(int Line, int Indent, string Name, int Kind)>();
        for (var i = 0; i < lines.Length; i++)
            if (Definition().Match(lines[i]) is { Success: true } m)
                found.Add((i, m.Groups[1].Length, m.Groups[3].Value, m.Groups[2].Value == "class" ? 5 : 12));
        int index = 0;
        return Level(-1);

        JsonArray Level(int parentIndent)
        {
            var list = new JsonArray();
            while (index < found.Count && found[index].Indent > parentIndent)
            {
                var (line, indent, name, kind) = found[index++];
                var children = Level(indent);
                // Ends before the next definition at this indentation or less
                var end = index < found.Count ? found[index].Line - 1 : lines.Length - 1;
                list.Add(new JsonObject
                {
                    ["name"] = name, ["kind"] = kind,
                    ["range"] = Range(line, 0, end, lines[end].TrimEnd('\r').Length),
                    ["selectionRange"] = Range(line, indent, line, lines[line].TrimEnd('\r').Length),
                    ["children"] = children,
                });
            }
            return list;
        }
    }

    private static JsonObject Range(int l1, int c1, int l2, int c2) => new()
    {
        ["start"] = new JsonObject { ["line"] = l1, ["character"] = c1 },
        ["end"] = new JsonObject { ["line"] = l2, ["character"] = c2 },
    };

    [GeneratedRegex(@"^(\s*)(class|def|function)\s+(\w+)")]
    private static partial Regex Definition();
}
