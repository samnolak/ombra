using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace OmpGui.App.Controls;

public enum PathKind { File, Folder, Image }

/// <summary>A path named in the conversation that exists: its full path, the line it points at, what it is.</summary>
public sealed record PathTarget(string FullPath, int? Line, PathKind Kind);

/// <summary>What a window does with paths in its conversation: finds them, opens them (MainViewModel).</summary>
public interface IPathLinkHandler
{
    /// <summary>The project the paths are relative to (the preview shows them relative to it); null without one.</summary>
    string? ProjectRoot { get; }

    /// <summary>The existing file or folder <paramref name="text"/> names (relative to the project, absolute, "~/…",
    /// with an optional ":42" or "#L42"); null when it names none.</summary>
    PathTarget? ResolvePath(string text);

    /// <summary>Opens it: an image in the image viewer, a file in the Files pane (at its line), a folder in the tree.</summary>
    void OpenPath(PathTarget target);
}

/// <summary>
/// Paths in the agent's text (inline code like <c>src/app.ts:42</c>, links to local files): the handler is inherited
/// down the visual tree from the window (<see cref="HandlerProperty"/>), so the Markdown view needs no view model. A
/// path is looked up only when the pointer rests on it, so text that merely looks like one costs nothing.
/// </summary>
public static partial class PathLinks
{
    public static readonly AttachedProperty<IPathLinkHandler?> HandlerProperty =
        AvaloniaProperty.RegisterAttached<Visual, IPathLinkHandler?>("Handler", typeof(PathLinks), inherits: true);

    public static IPathLinkHandler? GetHandler(Visual v) => v.GetValue(HandlerProperty);
    public static void SetHandler(Visual v, IPathLinkHandler? value) => v.SetValue(HandlerProperty, value);

    public static readonly IReadOnlySet<string> ImageExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico" };

    private const int PreviewLines = 12, PreviewEntries = 12, ThumbnailPixels = 360, MaxTextBytes = 64 * 1024;

    /// <summary>
    /// Whether <paramref name="text"/> could be a path worth looking up: one token with a folder separator or a file
    /// extension, not a URL. Cheap; the file system decides (<see cref="IPathLinkHandler.ResolvePath"/>).
    /// </summary>
    public static bool LooksLikePath(string text)
    {
        if (text.Length is < 2 or > 400 || text.AsSpan().IndexOfAny(" \t\r\n<>|\"*?") >= 0) return false;
        if (text.Contains("://", StringComparison.Ordinal) && !text.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return false;
        var (path, _) = SplitLine(text);
        return path.Contains('/') || path.Contains('\\') || path.StartsWith('~') || ExtensionPattern().IsMatch(path);
    }

    /// <summary>"src/a.ts:42", "src/a.ts:42:7", "src/a.ts#L42" → the path and the line.</summary>
    public static (string Path, int? Line) SplitLine(string text)
    {
        var m = LinePattern().Match(text);
        return m.Success && int.TryParse(m.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var line) && line > 0
            ? (text[..m.Index], line)
            : (text, null);
    }

    /// <summary>Resolves against <paramref name="root"/> (the project) and checks the file system.</summary>
    public static PathTarget? Resolve(string? root, string text)
    {
        if (!LooksLikePath(text)) return null;
        var (path, line) = SplitLine(text);
        if (Services.ProjectFiles.Resolve(root, path) is not { } resolved) return null;
        var full = Path.TrimEndingDirectorySeparator(resolved);
        if (Directory.Exists(full)) return new PathTarget(full, null, PathKind.Folder);
        if (!File.Exists(full)) return null;
        return new PathTarget(full, line, ImageExtensions.Contains(Path.GetExtension(full)) ? PathKind.Image : PathKind.File);
    }

    [GeneratedRegex(@"(?::(?<line>\d{1,7})(?::\d{1,5})?|#L(?<line>\d{1,7})(?:-L?\d{1,7})?)$")]
    private static partial Regex LinePattern();

    [GeneratedRegex(@"^[\w.@+-]*[\w-]\.[A-Za-z][A-Za-z0-9]{0,9}$")]
    private static partial Regex ExtensionPattern();

    // ── The preview shown while the pointer rests on a path ──

    /// <summary>
    /// What a path shows on hover: its name and where it is, then an image's thumbnail, a folder's first entries or a
    /// text file's first lines (around its line when it names one), and what a click does. Read off the UI thread.
    /// </summary>
    public static Control Preview(PathTarget target, string? root)
    {
        var shown = root is not null && Services.ProjectFiles.Relative(root, target.FullPath) is { Length: > 0 } rel ? rel : target.FullPath;
        var body = new StackPanel { Spacing = 8, MaxWidth = 420 };
        body.Children.Add(new TextBlock
        {
            Text = shown + (target.Line is { } l ? string.Create(CultureInfo.InvariantCulture, $":{l}") : "") + (target.Kind == PathKind.Folder ? "/" : ""),
            FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.PrefixCharacterEllipsis,
        });
        var detail = new TextBlock { Classes = { "tertiary" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        body.Children.Add(detail);
        var content = new ContentControl();
        body.Children.Add(content);
        body.Children.Add(new TextBlock
        {
            Classes = { "tertiary" }, FontSize = 12,
            Text = target.Kind switch
            {
                PathKind.Image => "Click to open in the image viewer",
                PathKind.Folder => "Click to show in the Files pane",
                _ => target.Line is null ? "Click to open in the Files pane" : "Click to open in the Files pane at this line",
            },
        });
        _ = FillAsync(target, detail, content);
        return body;
    }

    private static async Task FillAsync(PathTarget target, TextBlock detail, ContentControl content)
    {
        try
        {
            switch (target.Kind)
            {
                case PathKind.Image:
                {
                    var data = await Task.Run(() => new FileInfo(target.FullPath) is { Length: <= 40 * 1024 * 1024 } ? File.ReadAllBytes(target.FullPath) : null);
                    var thumb = data is null ? null : await Task.Run(() => Services.ImageAttachments.DecodeThumbnail(data, ThumbnailPixels));
                    detail.Text = Services.ProjectFiles.Size(data?.LongLength ?? new FileInfo(target.FullPath).Length) + (thumb is null ? "" : $" · {thumb.PixelSize.Width}×{thumb.PixelSize.Height} preview");
                    if (thumb is not null)
                        content.Content = new Image { Source = thumb, MaxWidth = 400, MaxHeight = 260, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
                    break;
                }
                case PathKind.Folder:
                {
                    var entries = await Task.Run(() => Services.ProjectFiles.List(target.FullPath));
                    detail.Text = entries.Count == 1 ? "Folder · 1 item" : $"Folder · {entries.Count} items";
                    var list = new StringBuilder();
                    foreach (var e in entries.Take(PreviewEntries)) list.Append(e.Name).Append(e.IsDirectory ? "/\n" : "\n");
                    if (entries.Count > PreviewEntries) list.Append(CultureInfo.InvariantCulture, $"… {entries.Count - PreviewEntries} more");
                    if (list.Length > 0) content.Content = Mono(list.ToString().TrimEnd('\n'));
                    break;
                }
                default:
                {
                    var (size, lines, binary) = await Task.Run(() => ReadHead(target.FullPath, target.Line));
                    detail.Text = binary ? $"Binary file · {Services.ProjectFiles.Size(size)}" : Services.ProjectFiles.Size(size);
                    if (!binary && lines.Length > 0) content.Content = Mono(lines);
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Dispatcher.UIThread.Post(() => detail.Text = e.Message);
        }
    }

    private static Border Mono(string text) => new()
    {
        Classes = { "path-peek-code" },
        Child = new TextBlock { Text = text, Classes = { "mono" }, FontSize = 12, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis },
    };

    /// <summary>
    /// The file's size and up to <see cref="PreviewLines"/> lines: from the top, or around <paramref name="line"/> (each
    /// numbered then); binary when its head has a NUL byte or is not UTF-8.
    /// </summary>
    internal static (long Size, string Lines, bool Binary) ReadHead(string path, int? line)
    {
        using var stream = File.OpenRead(path);
        var size = stream.Length;
        var buffer = new byte[(int)Math.Min(size, MaxTextBytes)];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var head = buffer.AsSpan(0, read);
        if (head.Contains((byte)0)) return (size, "", true);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(head); }
        catch (DecoderFallbackException) when (read == MaxTextBytes) { text = Encoding.UTF8.GetString(head); } // cut inside a character
        catch (DecoderFallbackException) { return (size, "", true); }
        var all = text.Replace("\r\n", "\n").Split('\n');
        var from = line is { } l ? Math.Clamp(l - 4, 1, Math.Max(1, all.Length)) : 1;
        var shown = all.Skip(from - 1).Take(PreviewLines).Select(s => s.Replace("\t", "    "));
        if (line is null) return (size, string.Join('\n', shown).TrimEnd(), false);
        var width = (from + PreviewLines).ToString(CultureInfo.InvariantCulture).Length;
        return (size, string.Join('\n', shown.Select((s, i) =>
            $"{(from + i == line ? "›" : " ")}{(from + i).ToString(CultureInfo.InvariantCulture).PadLeft(width)}  {s}")).TrimEnd(), false);
    }
}
