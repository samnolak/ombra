using System.Globalization;
using System.Text;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.App.Services;

namespace OmpGui.App.ViewModels;

/// <summary>What the viewer shows for a file.</summary>
public enum ViewerState { Loading, Text, Image, Binary, TooLarge, Missing, Error }

/// <summary>
/// The file open in the Files pane: its text (line numbers, coloured by language), a Markdown file rendered or as
/// source, an image, or a plain state saying why it cannot be shown here (binary, too large, gone) with a way out.
/// Reading happens off the UI thread (<see cref="Load"/>).
/// </summary>
public sealed partial class FileViewerViewModel : ObservableObject
{
    /// <summary>Text files up to this size are read whole; larger ones ask first and then show their start.</summary>
    public const long MaxTextBytes = 1024 * 1024;
    public const int HeadBytes = 256 * 1024;
    public const int MaxLines = 10_000;
    public const int MaxLineChars = 2_000;
    public const long MaxImageBytes = 40L * 1024 * 1024;
    /// <summary>Wider images are decoded at this width (the pane never shows more).</summary>
    private const int MaxImageWidth = 2400;

    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico" };
    public static readonly IReadOnlySet<string> MarkdownExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdx" };

    public FileViewerViewModel(FilesViewModel owner, string fullPath, string? relativePath)
    {
        Owner = owner;
        FullPath = fullPath;
        RelativePath = relativePath ?? fullPath;
        Name = Path.GetFileName(fullPath);
        var slash = RelativePath.Replace('\\', '/').LastIndexOf('/');
        Folder = relativePath is null ? Path.GetDirectoryName(fullPath) ?? "" : slash < 0 ? "" : RelativePath[..slash];
        IsMarkdown = MarkdownExtensions.Contains(Path.GetExtension(fullPath));
    }

    public FilesViewModel Owner { get; }
    public string FullPath { get; }
    /// <summary>Relative to the project ('/'-separated), or the full path for a file outside it.</summary>
    public string RelativePath { get; }
    public string Name { get; }
    /// <summary>The folder it is in, relative to the project ("" at the top).</summary>
    public string Folder { get; }
    public bool IsMarkdown { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCode), nameof(ShowMarkdown), nameof(MarkdownText), nameof(ShowImage), nameof(ShowState), nameof(IsLoading), nameof(IsMissing), nameof(CanShowHead), nameof(CanToggleSource))]
    private ViewerState _state = ViewerState.Loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkdownText))]
    private string _text = "";

    /// <summary>What the Markdown view renders: the text of a Markdown file shown rendered, else nothing (rendering any
    /// other text as Markdown, even hidden, cost seconds for a large file).</summary>
    public string? MarkdownText => ShowMarkdown ? Text : null;

    /// <summary>The line to show and mark (opened from a change in the conversation); set again to scroll to it again.</summary>
    [ObservableProperty] private int? _targetLine;

    [ObservableProperty] private Bitmap? _image;

    /// <summary>"214 lines · 6.1 KB", "1280 × 720 · 145 KB".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private string _info = "";

    /// <summary>Under the name: the folder it is in and what it is ("src/app · 214 lines · 6.1 KB").</summary>
    public string Subtitle => string.Join(" · ", new[] { Folder, Info }.Where(s => s.Length > 0));

    /// <summary>What was left out ("Showing the first 10,000 of 25,302 lines").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = "";

    [ObservableProperty] private string _stateTitle = "";
    [ObservableProperty] private string _stateDetail = "";

    /// <summary>A Markdown file as its source instead of rendered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCode), nameof(ShowMarkdown), nameof(MarkdownText), nameof(SourceTip))]
    private bool _showSource;

    /// <summary>Only the start of a large file was read.</summary>
    public bool IsHead { get; private set; }
    public long Size { get; private set; }
    public DateTime Stamp { get; private set; }

    public bool IsLoading => State == ViewerState.Loading;
    public bool IsMissing => State == ViewerState.Missing;
    public bool ShowCode => State == ViewerState.Text && (!IsMarkdown || ShowSource);
    public bool ShowMarkdown => State == ViewerState.Text && IsMarkdown && !ShowSource;
    public bool ShowImage => State == ViewerState.Image;
    public bool ShowState => State is ViewerState.Binary or ViewerState.TooLarge or ViewerState.Missing or ViewerState.Error;
    public bool CanShowHead => State == ViewerState.TooLarge;
    public bool CanToggleSource => IsMarkdown && State == ViewerState.Text;
    public bool HasNotice => Notice.Length > 0;
    public string SourceTip => ShowSource ? "Show it rendered" : "Show the Markdown source";

    [RelayCommand]
    private void ToggleSource() => ShowSource = !ShowSource;

    /// <summary>A file too large to read whole: its first 256 KB.</summary>
    [RelayCommand]
    private Task ShowHeadAsync() => Owner.ReloadViewerAsync(this, head: true);

    /// <summary>Scrolls to <paramref name="line"/> again even when it is the line already shown.</summary>
    public void JumpTo(int? line)
    {
        TargetLine = null;
        TargetLine = line;
    }

    /// <summary>The result of reading a file (made off the UI thread).</summary>
    public sealed record Loaded(ViewerState State, string Text = "", string Info = "", string Notice = "", string Title = "", string Detail = "",
        Bitmap? Image = null, long Size = 0, DateTime Stamp = default, bool Head = false);

    internal void Apply(Loaded r)
    {
        var old = Image;
        Image = r.Image;
        if (!ReferenceEquals(old, r.Image)) DisposeLater(old);
        Text = r.Text;
        Info = r.Info;
        Notice = r.Notice;
        StateTitle = r.Title;
        StateDetail = r.Detail;
        Size = r.Size;
        Stamp = r.Stamp;
        IsHead = r.Head;
        State = r.State;
    }

    /// <summary>The viewer was closed or replaced: its image goes once the view no longer draws it.</summary>
    internal void Release()
    {
        var old = Image;
        Image = null;
        DisposeLater(old);
    }

    private static void DisposeLater(Bitmap? bitmap)
    {
        if (bitmap is not null) Avalonia.Threading.Dispatcher.UIThread.Post(bitmap.Dispose, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Reads <paramref name="path"/>: text (whole, or its start with <paramref name="head"/>), an image, or a state.</summary>
    public static Loaded Load(string path, bool head, CancellationToken ct)
    {
        FileInfo fi;
        try
        {
            fi = new FileInfo(path);
            if (!fi.Exists)
                return new(ViewerState.Missing, Title: "This file is not there any more",
                    Detail: "It was deleted or moved. If git still has it, it shows in the list struck through.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(ViewerState.Error, Title: "Can't read this file", Detail: e.Message);
        }
        var size = fi.Length;
        var stamp = fi.LastWriteTimeUtc;
        try
        {
            if (ImageExtensions.Contains(fi.Extension)) return LoadImage(fi, ct);
            byte[] bytes;
            using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var want = size > MaxTextBytes ? (head ? HeadBytes : 8192) : (int)size;
                bytes = new byte[want];
                var read = 0;
                while (read < want)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = s.Read(bytes, read, want - read);
                    if (n == 0) break;
                    read += n;
                }
                if (read < want) Array.Resize(ref bytes, read);
            }
            if (IsBinary(bytes))
                return new(ViewerState.Binary, Title: "Binary file", Detail: $"{ProjectFiles.Size(size)}. It isn't text, so it can't be shown here.", Size: size, Stamp: stamp,
                    Info: ProjectFiles.Size(size));
            if (size > MaxTextBytes && !head)
                return new(ViewerState.TooLarge, Title: "Too large to show here", Detail: $"{ProjectFiles.Size(size).Replace(' ', ' ')}. Show its first {HeadBytes / 1024} KB here, or open it in an app.",
                    Size: size, Stamp: stamp, Info: ProjectFiles.Size(size));
            var (text, lines, notes) = Decode(bytes, cutLast: size > bytes.Length);
            if (size > bytes.Length) notes.Insert(0, $"Showing the first {ProjectFiles.Size(bytes.Length)} of {ProjectFiles.Size(size)}");
            if (size == 0) notes.Add("This file is empty");
            var info = size == 0 ? "Empty file" : string.Create(CultureInfo.InvariantCulture, $"{(lines == 1 ? "1 line" : $"{lines:N0} lines")} · {ProjectFiles.Size(size)}");
            return new(ViewerState.Text, text, info, string.Join(" · ", notes), Size: size, Stamp: stamp, Head: size > bytes.Length);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(ViewerState.Error, Title: "Can't read this file", Detail: e.Message, Size: size, Stamp: stamp);
        }
    }

    private static Loaded LoadImage(FileInfo fi, CancellationToken ct)
    {
        var size = fi.Length;
        if (size > MaxImageBytes)
            return new(ViewerState.TooLarge, Title: "Too large to show here", Detail: $"{ProjectFiles.Size(size)}. Open it in an app instead.", Size: size, Stamp: fi.LastWriteTimeUtc);
        try
        {
            using var s = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bmp = new Bitmap(s);
            var (w, h) = (bmp.PixelSize.Width, bmp.PixelSize.Height);
            ct.ThrowIfCancellationRequested();
            if (w > MaxImageWidth)
            {
                bmp.Dispose();
                s.Position = 0;
                bmp = Bitmap.DecodeToWidth(s, MaxImageWidth);
            }
            return new(ViewerState.Image, Image: bmp, Info: string.Create(CultureInfo.InvariantCulture, $"{w} × {h} · {ProjectFiles.Size(size)}"), Size: size, Stamp: fi.LastWriteTimeUtc);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            return new(ViewerState.Error, Title: "Can't show this image", Detail: $"{ProjectFiles.Size(size)}. The image could not be decoded ({e.Message.Split('\n')[0]}).",
                Size: size, Stamp: fi.LastWriteTimeUtc);
        }
    }

    /// <summary>Text has no NUL bytes (UTF-16 with a byte order mark aside) and few control characters.</summary>
    internal static bool IsBinary(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF)) return false;
        var sample = bytes[..Math.Min(bytes.Length, 8192)];
        if (sample.IndexOf((byte)0) >= 0) return true;
        var control = 0;
        foreach (var b in sample)
            if (b < 0x09 || b is > 0x0D and < 0x20 && b != 0x1B) control++;
        return sample.Length > 0 && control * 10 > sample.Length;
    }

    /// <summary>
    /// The bytes as text (a byte order mark picks UTF-8 / UTF-16, else UTF-8), '\n' line ends, at most
    /// <see cref="MaxLines"/> lines of at most <see cref="MaxLineChars"/> characters (tabs as 4 spaces); with
    /// <paramref name="cutLast"/> the last line (cut mid-way) is dropped. Also says what was left out.
    /// </summary>
    internal static (string Text, int Lines, List<string> Notes) Decode(byte[] bytes, bool cutLast = false)
    {
        string text;
        using (var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
            text = reader.ReadToEnd();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var count = lines.Length;
        if (count > 1 && lines[^1].Length == 0) count--; // the newline at the end of the file
        if (cutLast && count > 1) count--;
        var notes = new List<string>();
        var shown = Math.Min(count, MaxLines);
        if (count > MaxLines) notes.Add(string.Create(CultureInfo.InvariantCulture, $"Showing the first {MaxLines:N0} of {count:N0} lines"));
        var sb = new StringBuilder(Math.Min(text.Length + 16, 4 * 1024 * 1024));
        var cut = false;
        for (var i = 0; i < shown; i++)
        {
            var line = lines[i].Contains('\t') ? lines[i].Replace("\t", "    ") : lines[i];
            if (line.Length > MaxLineChars)
            {
                line = line[..MaxLineChars] + " …";
                cut = true;
            }
            if (i > 0) sb.Append('\n');
            sb.Append(line);
        }
        if (cut) notes.Add(string.Create(CultureInfo.InvariantCulture, $"Lines longer than {MaxLineChars:N0} characters are cut"));
        return (sb.ToString(), Math.Max(shown, 1), notes);
    }
}
