using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace OmpGui.App.Controls;

/// <summary>
/// Light syntax colouring for code in diffs, code blocks and the file viewer: comments, strings, numbers, keywords,
/// types and called functions, one line at a time (a block comment or string spanning lines is carried over). It is
/// a reading aid, not a parser: the text is never changed, only coloured (GuiCode* tokens).
/// </summary>
public static partial class SyntaxHighlighter
{
    public enum Token { Plain, Keyword, String, Comment, Number, Type, Function }

    /// <summary>A language's line comment, block comment, string quotes and keywords.</summary>
    public sealed record Language(string Name, string? LineComment, (string Open, string Close)? BlockComment, string Quotes, HashSet<string> Keywords, bool TypesByCase = true);

    private static HashSet<string> Words(string s) => [.. s.Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    private static readonly Language CFamily = new("c", "//", ("/*", "*/"), "\"'`", Words(
        "abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly record ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while yield get set init required partial where with when let function export import from extends implements type undefined of instanceof declare module keyof readonly package func go defer chan map range select fallthrough fn impl mut pub use crate mod trait self super match loop move dyn unsafe match val fun object companion data sealed suspend lateinit"));

    private static readonly Language Python = new("python", "#", null, "\"'", Words(
        "and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return True try while with yield self match case"));

    private static readonly Language Shell = new("shell", "#", null, "\"'", Words(
        "if then else elif fi for while until do done case esac in function return export local readonly set unset echo exit source alias shift"), TypesByCase: false);

    private static readonly Language Json = new("json", null, null, "\"", Words("true false null"), TypesByCase: false);
    private static readonly Language Yaml = new("yaml", "#", null, "\"'", Words("true false null yes no on off"), TypesByCase: false);
    private static readonly Language Css = new("css", null, ("/*", "*/"), "\"'", Words("important media import from to"), TypesByCase: false);
    private static readonly Language Markup = new("markup", null, ("<!--", "-->"), "\"'", [], TypesByCase: false);
    private static readonly Language Sql = new("sql", "--", ("/*", "*/"), "'", Words(
        "select from where insert into values update set delete create table alter drop index join left right inner outer on group by order having limit as and or not null primary key foreign references distinct union all case when then else end SELECT FROM WHERE INSERT INTO VALUES UPDATE SET DELETE CREATE TABLE ALTER DROP INDEX JOIN LEFT RIGHT INNER OUTER ON GROUP BY ORDER HAVING LIMIT AS AND OR NOT NULL PRIMARY KEY DISTINCT UNION ALL CASE WHEN THEN ELSE END"), TypesByCase: false);

    /// <summary>The language for a file name or a code block's info string ("cs", "py", "tsx"…); null: plain.</summary>
    public static Language? ForName(string? nameOrLang)
    {
        if (string.IsNullOrWhiteSpace(nameOrLang)) return null;
        var n = nameOrLang.Trim().ToLowerInvariant();
        var ext = n.Contains('.') ? n[(n.LastIndexOf('.') + 1)..] : n;
        if (n.EndsWith("dockerfile", StringComparison.Ordinal) || n.EndsWith("makefile", StringComparison.Ordinal)) return Shell;
        return ext switch
        {
            "cs" or "csharp" or "c" or "h" or "cpp" or "cc" or "hpp" or "cxx" or "java" or "kt" or "kts" or "kotlin" or "js" or "jsx" or "mjs" or "cjs"
                or "ts" or "tsx" or "javascript" or "typescript" or "go" or "golang" or "rs" or "rust" or "swift" or "scala" or "dart" or "php" or "fs" => CFamily,
            "py" or "python" or "pyi" => Python,
            "sh" or "bash" or "zsh" or "shell" or "fish" or "ps1" or "powershell" or "console" => Shell,
            "json" or "jsonc" or "jsonl" => Json,
            "yml" or "yaml" or "toml" or "ini" => Yaml,
            "css" or "scss" or "less" => Css,
            "html" or "htm" or "xml" or "xaml" or "axaml" or "svg" or "vue" or "svelte" or "csproj" or "props" => Markup,
            "sql" => Sql,
            _ => null,
        };
    }

    [GeneratedRegex(@"^(?:0[xX][0-9a-fA-F_]+|\d[\d_]*(?:\.\d+)?(?:[eE][+-]?\d+)?[a-zA-Z]*)")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^[A-Za-z_$@][\w$]*")]
    private static partial Regex WordRegex();

    /// <summary>
    /// The tokens of one line. <paramref name="inBlock"/> carries an open block comment into and out of the line.
    /// </summary>
    public static List<(Token Kind, string Text)> Tokenize(string line, Language? lang, ref bool inBlock)
    {
        var result = new List<(Token, string)>();
        if (lang is null) { result.Add((Token.Plain, line)); return result; }
        var i = 0;
        var plainStart = 0;
        void Flush(int upTo) { if (upTo > plainStart) result.Add((Token.Plain, line[plainStart..upTo])); }
        void Emit(Token k, int from, int to) { Flush(from); result.Add((k, line[from..to])); plainStart = to; }

        if (inBlock && lang.BlockComment is { } bc0)
        {
            var end = line.IndexOf(bc0.Close, StringComparison.Ordinal);
            if (end < 0) { result.Add((Token.Comment, line)); return result; }
            Emit(Token.Comment, 0, end + bc0.Close.Length);
            i = end + bc0.Close.Length;
            inBlock = false;
        }
        while (i < line.Length)
        {
            var c = line[i];
            if (lang.LineComment is { } lc && string.CompareOrdinal(line, i, lc, 0, lc.Length) == 0
                && (lang != Shell || i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                Emit(Token.Comment, i, line.Length);
                return result;
            }
            if (lang.BlockComment is { } bc && string.CompareOrdinal(line, i, bc.Open, 0, bc.Open.Length) == 0)
            {
                var end = line.IndexOf(bc.Close, i + bc.Open.Length, StringComparison.Ordinal);
                if (end < 0) { Emit(Token.Comment, i, line.Length); inBlock = true; return result; }
                Emit(Token.Comment, i, end + bc.Close.Length);
                i = end + bc.Close.Length;
                continue;
            }
            if (lang.Quotes.Contains(c))
            {
                var j = i + 1;
                while (j < line.Length && line[j] != c) j += line[j] == '\\' ? 2 : 1;
                j = Math.Min(line.Length, j + 1);
                Emit(Token.String, i, j);
                i = j;
                continue;
            }
            if (char.IsDigit(c) && (i == 0 || !char.IsLetterOrDigit(line[i - 1]) && line[i - 1] != '_'))
            {
                var m = NumberRegex().Match(line[i..]);
                if (m.Success) { Emit(Token.Number, i, i + m.Length); i += m.Length; continue; }
            }
            if (char.IsLetter(c) || c is '_' or '$' or '@')
            {
                var m = WordRegex().Match(line[i..]);
                var word = m.Value;
                var end = i + word.Length;
                if (lang.Keywords.Contains(word)) Emit(Token.Keyword, i, end);
                else if (lang == Markup && i > 0 && (line[i - 1] == '<' || line[i - 1] == '/')) Emit(Token.Keyword, i, end);
                else if (lang == Json || lang == Yaml || lang == Css) { /* keys and values stay plain; strings carry colour */ }
                else if (end < line.Length && line[end] == '(') Emit(Token.Function, i, end);
                else if (lang.TypesByCase && char.IsUpper(word[0]) && word.Any(char.IsLower)) Emit(Token.Type, i, end);
                i = Math.Max(end, i + 1);
                continue;
            }
            i++;
        }
        Flush(line.Length);
        return result;
    }

    /// <summary>Appends a line's coloured runs (brushes resolved now, from the current theme).</summary>
    public static void AddRuns(InlineCollection inlines, string line, Language? lang, ref bool inBlock)
    {
        foreach (var (kind, text) in Tokenize(line, lang, ref inBlock))
        {
            var run = new Run(text);
            if (Brush(kind) is { } b) run.Foreground = b;
            if (kind == Token.Comment) run.FontStyle = FontStyle.Italic;
            inlines.Add(run);
        }
    }

    /// <summary>A token's colour in the current theme (null: the text colour); the Files pane's code view draws with it.</summary>
    public static IBrush? BrushFor(Token kind) => Brush(kind);

    private static IBrush? Brush(Token kind)
    {
        var key = kind switch
        {
            Token.Keyword => "GuiCodeKeyword",
            Token.String => "GuiCodeString",
            Token.Comment => "GuiCodeComment",
            Token.Number => "GuiCodeNumber",
            Token.Type => "GuiCodeType",
            Token.Function => "GuiCodeFunction",
            _ => null,
        };
        return key is not null && Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var v) == true ? v as IBrush : null;
    }
}
