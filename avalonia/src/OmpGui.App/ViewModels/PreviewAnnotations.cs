using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OmpGui.App.ViewModels;

/// <summary>The element that holds a mark, as the page reported it (Controls/Preview/annotate.js); its box in the viewport.</summary>
public sealed record PageElement(string Selector, string Tag, string Html, string Text, string Source, int X, int Y, int Width, int Height);

public enum PageMarkKind { Point, Area }

/// <summary>
/// Where the user marked the page: a point (no size) or an area, at <see cref="X"/>,<see cref="Y"/> in the viewport
/// and <see cref="PageX"/>,<see cref="PageY"/> on the page; <see cref="OffsetX"/>,<see cref="OffsetY"/> from the
/// holding element's corner (the pin follows that element).
/// </summary>
public sealed record PageMark(PageMarkKind Kind, int X, int Y, int Width, int Height, int PageX, int PageY, int OffsetX, int OffsetY,
    int ViewportWidth, int ViewportHeight);

/// <summary>An element an area shows (one of a few the page found under it).</summary>
public sealed record PageInsideElement(string Selector, string Tag, string Text);

/// <summary>A comment left on a point or an area of the previewed page, numbered like its pin on the page.</summary>
public sealed partial class AnnotationViewModel(string id, Uri page, string pageTitle, PageMark mark, PageElement element,
    IReadOnlyList<PageInsideElement> inside, string comment, PreviewViewModel owner) : ObservableObject
{
    public string Id { get; } = id;
    public Uri Page { get; } = page;
    public string PageTitle { get; } = pageTitle;
    public PageMark Mark { get; } = mark;
    /// <summary>The element under the point, or the smallest one holding the whole area.</summary>
    public PageElement Element { get; } = element;
    /// <summary>For an area: elements under it (at most a few); empty for a point.</summary>
    public IReadOnlyList<PageInsideElement> Inside { get; } = inside;
    /// <summary>What the user wrote; changed from the chip's editor or from the pin on the page.</summary>
    [ObservableProperty] private string _comment = comment;

    [ObservableProperty] private int _number;

    /// <summary>The chip's editor is open (<see cref="EditText"/> is what it shows).</summary>
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editText = "";

    /// <summary>The mark in a few words: "Point on button — Sign in", "Area 240×120 in section".</summary>
    public string TargetLabel
    {
        get
        {
            var on = Element.Text.Length > 0 ? $"{Element.Tag} — {Element.Text}" : Element.Selector;
            return Mark.Kind == PageMarkKind.Area ? $"Area {Mark.Width}×{Mark.Height} in {on}" : $"Point on {on}";
        }
    }

    /// <summary>The page it was left on, as the chip shows it: "localhost:5173/about".</summary>
    public string PageLabel => Page.Authority + (Page.AbsolutePath == "/" ? "" : Page.AbsolutePath.TrimEnd('/'));

    /// <summary>The chip's tooltip: where (page and mark) and what was written.</summary>
    public string Tooltip => $"{(PageTitle.Length > 0 ? $"{PageTitle} — " : "")}{PageLabel}\n{TargetLabel}\n\n{Comment}";

    partial void OnCommentChanged(string value) => OnPropertyChanged(nameof(Tooltip));

    [RelayCommand]
    private void Remove() => owner.RemoveAnnotation(this);

    [RelayCommand]
    private void BeginEdit()
    {
        EditText = Comment;
        IsEditing = true;
    }

    /// <summary>Keeps the edited text (an empty one changes nothing: × removes a comment).</summary>
    [RelayCommand]
    private void SaveEdit()
    {
        IsEditing = false;
        owner.EditAnnotation(this, EditText);
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    /// <summary>Opens the page it was left on in the preview (its tab, if one shows it), where its pin is.</summary>
    [RelayCommand]
    private void Show() => owner.ShowAnnotation(this);
}

/// <summary>
/// Annotations (Codex's page comments): in annotate mode the user clicks a point of the previewed page or drags over an
/// area and comments on it; the comments go with the next message, each with where the mark is and what finds the
/// element holding it in the code (selector, opening tag, text, box, the source file where the dev build says it) and,
/// for an area, the elements it shows. The page reports through the web view's message bridge; only messages with this
/// panel's token count, and only as many and as long as <see cref="MaxAnnotations"/> and the field limits allow.
/// </summary>
public sealed partial class PreviewViewModel
{
    public const int MaxAnnotations = 20;

    /// <summary>How the comments block begins in a sent message (the conversation shows it as a chip).</summary>
    public const string PromptMarker = "Comments on the page ";

    /// <summary>The first line when the comments go without words of the user's own.</summary>
    public const string CommentsOnlyLine = "Please make the changes my comments on the page ask for.";
    private const int MaxComment = 2000, MaxField = 400, MaxInside = 6;

    /// <summary>Shared with the injected script; a message without it did not come from our script.</summary>
    internal string AnnotationToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public ObservableCollection<AnnotationViewModel> Annotations { get; } = [];

    /// <summary>Clicking the page marks a point, dragging marks an area, to comment on instead of using the page.</summary>
    [ObservableProperty] private bool _isAnnotating;

    public bool HasAnnotations => Annotations.Count > 0;
    public string AnnotationsTitle => Annotations.Count == 1 ? "1 comment" : $"{Annotations.Count} comments";

    private bool CanAnnotate() => HasPage && !IsEngineUnavailable;

    [RelayCommand(CanExecute = nameof(CanAnnotate))]
    private void ToggleAnnotate() => IsAnnotating = !IsAnnotating;

    [RelayCommand]
    private void ClearAnnotations() => Annotations.Clear();

    private void OnAnnotationsChanged()
    {
        for (var i = 0; i < Annotations.Count; i++) Annotations[i].Number = i + 1;
        OnPropertyChanged(nameof(HasAnnotations));
        OnPropertyChanged(nameof(AnnotationsTitle));
    }

    internal void RemoveAnnotation(AnnotationViewModel a) => Annotations.Remove(a);

    /// <summary>The page or the chip changed a comment's text; the pins follow (<see cref="AnnotationEdited"/>).</summary>
    internal void EditAnnotation(AnnotationViewModel a, string text)
    {
        var comment = text.Trim();
        if (comment.Length == 0 || comment == a.Comment) return;
        a.Comment = comment.Length <= MaxComment ? comment : comment[..MaxComment] + "…";
        AnnotationEdited?.Invoke();
    }

    /// <summary>A comment's text changed (the page's pins show it as their tooltip).</summary>
    public event Action? AnnotationEdited;

    /// <summary>The preview should be shown (a comment's page was asked for).</summary>
    public event Action? ShowRequested;

    internal void ShowAnnotation(AnnotationViewModel a)
    {
        ShowRequested?.Invoke();
        if (CurrentUrl is { } now && SamePage(now, a.Page)) return;
        if (Tabs.FirstOrDefault(t => !t.IsClosed && t.Url is { } u && SamePage(u, a.Page)) is { } tab) SelectTabCommand.Execute(tab);
        else NavigateCommand.Execute(a.Page.AbsoluteUri);
    }

    /// <summary>
    /// A message from the page's script. Returns what it was: "annotation", "annotation-update", "annotation-remove",
    /// "annotate-exit", "ready", or null when it is not ours (no or a wrong token, not JSON, missing fields, an unknown
    /// comment) and was ignored.
    /// </summary>
    public string? OnPageMessage(string? body)
    {
        if (string.IsNullOrEmpty(body) || body.Length > 64_000) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var j = doc.RootElement;
            if (j.ValueKind != JsonValueKind.Object || Str(j, "token") != AnnotationToken) return null;
            switch (Str(j, "type"))
            {
                case "ready":
                    return "ready";
                case "annotate-exit":
                    IsAnnotating = false;
                    return "annotate-exit";
                case "annotation-update":
                    if (Annotations.FirstOrDefault(a => a.Id == Str(j, "id")) is not { } edited || Str(j, "comment") is not { } text) return null;
                    EditAnnotation(edited, text);
                    return "annotation-update";
                case "annotation-remove":
                    if (Annotations.FirstOrDefault(a => a.Id == Str(j, "id")) is not { } removed) return null;
                    Annotations.Remove(removed);
                    return "annotation-remove";
                case "annotation":
                    if (Annotations.Count >= MaxAnnotations)
                    {
                        Message = $"At most {MaxAnnotations} comments at a time: send or clear some first.";
                        return null;
                    }
                    var comment = Str(j, "comment")?.Trim() ?? "";
                    if (comment.Length == 0 || !j.TryGetProperty("element", out var e) || e.ValueKind != JsonValueKind.Object
                        || !j.TryGetProperty("mark", out var m) || m.ValueKind != JsonValueKind.Object) return null;
                    if (!Uri.TryCreate(Str(j, "url"), UriKind.Absolute, out var page) || !IsAllowed(page)) page = CurrentUrl;
                    if (page is null) return null;
                    PageMarkKind? kind = Str(m, "kind") switch { "point" => PageMarkKind.Point, "area" => PageMarkKind.Area, _ => null };
                    if (kind is not { } k) return null;
                    var mark = new PageMark(k, Int(m, "x"), Int(m, "y"), k == PageMarkKind.Area ? Int(m, "width") : 0, k == PageMarkKind.Area ? Int(m, "height") : 0,
                        Int(m, "pageX"), Int(m, "pageY"), Int(m, "offsetX"), Int(m, "offsetY"), Int(m, "viewportWidth"), Int(m, "viewportHeight"));
                    if (k == PageMarkKind.Area && (mark.Width <= 0 || mark.Height <= 0)) return null;
                    var element = new PageElement(
                        Cap(Str(e, "selector")), Cap(Str(e, "tag"), 40), Cap(Str(e, "html")), Cap(Str(e, "text")), Cap(Str(e, "source")),
                        Int(e, "x"), Int(e, "y"), Int(e, "width"), Int(e, "height"));
                    if (element.Selector.Length == 0) return null;
                    IReadOnlyList<PageInsideElement> inside = k == PageMarkKind.Area && j.TryGetProperty("inside", out var ins) && ins.ValueKind == JsonValueKind.Array
                        ? [.. ins.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                            .Select(x => new PageInsideElement(Cap(Str(x, "selector")), Cap(Str(x, "tag"), 40), Cap(Str(x, "text"))))
                            .Where(x => x.Selector.Length > 0).Take(MaxInside)]
                        : [];
                    var id = Cap(Str(j, "id"), 40);
                    if (id.Length == 0 || Annotations.Any(a => a.Id == id)) return null;
                    Annotations.Add(new AnnotationViewModel(id, page, Cap(Str(j, "title"), 200), mark, element, inside, Cap(comment, MaxComment), this));
                    return "annotation";
            }
        }
        catch (JsonException)
        {
        }
        return null;

        static string Cap(string? s, int max = MaxField) => s is null ? "" : s.Length <= max ? s : s[..max] + "…";
        static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int Int(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)
                ? (int)Math.Clamp(Math.Round(d), -1_000_000, 1_000_000) : 0;
    }

    /// <summary>The pins for the page shown: the comments made on it, as JSON for the script's setPins.</summary>
    internal string PinsJson(Uri? page)
    {
        var pins = Annotations.Where(a => page is not null && SamePage(a.Page, page))
            .Select(a => new PinDto(a.Id, a.Number, a.Mark.Kind == PageMarkKind.Area ? "area" : "point", a.Element.Selector,
                a.Mark.OffsetX, a.Mark.OffsetY, a.Mark.Width, a.Mark.Height, a.Mark.PageX, a.Mark.PageY, a.Comment)).ToArray();
        return JsonSerializer.Serialize(pins, PreviewAnnotationsJson.Default.PinDtoArray);
    }

    private static bool SamePage(Uri a, Uri b) =>
        Uri.Compare(a, b, UriComponents.HttpRequestUrl, UriFormat.Unescaped, StringComparison.Ordinal) == 0;

    /// <summary>The comments as a message for the agent: per page, each comment with its mark and what finds it in the code.</summary>
    public static string BuildPrompt(IReadOnlyList<AnnotationViewModel> annotations)
    {
        if (annotations.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var page in annotations.GroupBy(a => a.Page.GetLeftPart(UriPartial.Query)))
        {
            if (sb.Length > 0) sb.Append('\n');
            var title = page.First().PageTitle;
            sb.Append(CultureInfo.InvariantCulture, $"{PromptMarker}{page.Key}{(title.Length > 0 ? $" (\"{title}\")" : "")}, left in the preview.");
            sb.Append(" Each marks a point or an area of the page; the element details are quoted from the page:\n");
            foreach (var a in page)
            {
                var (m, e) = (a.Mark, a.Element);
                sb.Append(CultureInfo.InvariantCulture, $"\n{a.Number}. {a.Comment}\n");
                var onPage = m.PageX != m.X || m.PageY != m.Y ? string.Create(CultureInfo.InvariantCulture, $" ({m.PageX},{m.PageY} on the page)") : "";
                if (m.Kind == PageMarkKind.Area)
                    sb.Append(CultureInfo.InvariantCulture, $"   Area: {m.Width}×{m.Height} at {m.X},{m.Y} in a {m.ViewportWidth}×{m.ViewportHeight} viewport{onPage}\n");
                else
                    sb.Append(CultureInfo.InvariantCulture, $"   Point: {m.X},{m.Y} in a {m.ViewportWidth}×{m.ViewportHeight} viewport{onPage}\n");
                sb.Append(CultureInfo.InvariantCulture, $"   {(m.Kind == PageMarkKind.Area ? "Within" : "On")}: {e.Html}");
                if (e.Text.Length > 0) sb.Append(CultureInfo.InvariantCulture, $" with the text \"{e.Text}\"");
                sb.Append('\n');
                sb.Append(CultureInfo.InvariantCulture, $"   Selector: {e.Selector}\n");
                if (e.Source.Length > 0) sb.Append(CultureInfo.InvariantCulture, $"   Source: {e.Source}\n");
                sb.Append(CultureInfo.InvariantCulture, $"   Element box: {e.Width}×{e.Height} at {e.X},{e.Y}\n");
                if (a.Inside.Count > 0)
                    sb.Append("   Inside the area: ").AppendJoin("; ", a.Inside.Select(i => i.Text.Length > 0 ? $"{i.Tag} \"{i.Text}\" ({i.Selector})" : i.Selector)).Append('\n');
            }
        }
        sb.Append("\nFind these places in the project's source and make the changes the comments ask for.");
        return sb.ToString();
    }
}

internal sealed record PinDto(string id, int number, string kind, string selector, int offsetX, int offsetY, int width, int height,
    int pageX, int pageY, string comment);

[System.Text.Json.Serialization.JsonSerializable(typeof(PinDto[]))]
internal sealed partial class PreviewAnnotationsJson : System.Text.Json.Serialization.JsonSerializerContext;
