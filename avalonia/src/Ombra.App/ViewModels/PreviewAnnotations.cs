using Ombra.App.Localization;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.ClientCore;
using Ombra.ClientCore.Teams;

namespace Ombra.App.ViewModels;

/// <summary>
/// Annotations (Codex's page comments): in annotate mode the user clicks a point of the previewed page or drags over an
/// area, or with the brush (<see cref="AnnotateTool"/>) draws a rough sketch over it, and comments on it; each becomes a
/// local page mark in the chat's one mark list (<see cref="Marks"/>, shared with the message box) and goes with the next
/// message with where the mark is and what finds the element holding it in the code (selector, opening tag, text, box,
/// the source file where the dev build says it) and, for an area or a sketch, the elements it shows; a sketch also with
/// its picture (the page around it with its strokes, taken when it is made). The page reports through the web view's
/// message bridge; only messages with this panel's token count, and only as many and as long as
/// <see cref="LocalMarks.MaxMarks"/> and the field limits allow. The mode and the tool stay as the user switches tabs:
/// the page shown gets them.
/// </summary>
public sealed partial class PreviewViewModel
{
    private const int MaxField = 400, MaxInside = 6, MaxStrokes = 64, MaxSketchPoints = 1200;

    /// <summary>Shared with the injected script; a message without it did not come from our script.</summary>
    internal string AnnotationToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>The chat's marks (the main view model passes its own); the page's pins are its page marks.</summary>
    public MarkCollection Marks { get; init; } = new();

    /// <summary>Clicking the page marks a point, dragging marks an area (or draws, <see cref="AnnotateTool"/>), to comment on instead of using the page.</summary>
    [ObservableProperty] private bool _isAnnotating;

    /// <summary>What a drag on the page does in annotate mode: mark (a point or an area) or draw a sketch. The page's
    /// toolbar and its B key switch it; the tab shown next gets it.</summary>
    [ObservableProperty] private AnnotateTool _annotateTool;

    /// <summary>The tool as the page script names it.</summary>
    internal string AnnotateToolName => AnnotateTool == AnnotateTool.Brush ? "brush" : "mark";

    private bool CanAnnotate() => HasPage && !IsEngineUnavailable;

    [RelayCommand(CanExecute = nameof(CanAnnotate))]
    private void ToggleAnnotate() => IsAnnotating = !IsAnnotating;

    /// <summary>Removes the page marks (marks on code stay).</summary>
    [RelayCommand]
    private void ClearAnnotations()
    {
        foreach (var m in Marks.Items.Where(m => m.IsPage).ToList()) Marks.Remove(m);
    }

    /// <summary>The preview should be shown (a mark's page was asked for).</summary>
    public event Action? ShowRequested;

    /// <summary>Opens the page a mark was left on (its tab, if one shows it), where its pin is.</summary>
    internal void ShowMark(MarkViewModel m)
    {
        if (m.PageUri is { } page) ShowPage(page);
    }

    /// <summary>Shows <paramref name="page"/>: the tab already on it, else the shown tab goes there.</summary>
    internal void ShowPage(Uri page)
    {
        ShowRequested?.Invoke();
        if (CurrentUrl is { } now && SamePage(now, page)) return;
        if (Tabs.FirstOrDefault(t => !t.IsClosed && t.Url is { } u && SamePage(u, page)) is { } tab) SelectTabCommand.Execute(tab);
        else NavigateCommand.Execute(page.AbsoluteUri);
    }

    /// <summary>Team members' page marks addressed to this member (MainViewModel.SharedMarks.cs): drawn as their own pins, not editable.</summary>
    public IReadOnlyList<SharedMark> SharedPins { get; private set; } = [];

    /// <summary>The pins to draw changed without the chat's marks changing (<see cref="SharedPins"/>, <see cref="PendingReveal"/>).</summary>
    public event Action? PinsChanged;

    /// <summary>A shared pin to scroll to when the page has it (the panel clears it once shown).</summary>
    public string? PendingReveal { get; set; }

    internal void SetSharedPins(IReadOnlyList<SharedMark> pins)
    {
        SharedPins = pins;
        PinsChanged?.Invoke();
    }

    /// <summary>Opens a team member's page mark at its pin.</summary>
    internal void RevealShared(SharedMark mark)
    {
        if (mark.Mark.Page is not { } p || !Uri.TryCreate(p.Url, UriKind.Absolute, out var page)) return;
        PendingReveal = SharedPinId(mark);
        ShowPage(page);
        PinsChanged?.Invoke();
    }

    internal static string SharedPinId(SharedMark m) => "team-" + m.Item.AuthorMemberId + "-" + m.Item.ItemId;

    private MarkViewModel? PageMark(string? id) => id is null ? null : Marks.Items.FirstOrDefault(m => m.IsPage && m.Id == id);

    /// <summary>
    /// A message from the page's script. Returns what it was: "annotation", "annotation-update", "annotation-remove",
    /// "annotate-exit", "ready", "location", or null when it is not ours (no or a wrong token, not JSON, missing fields, an
    /// unknown comment, another site's address) and was ignored.
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
                case "location":
                    // A single-page app moved to another route (pushState, a hash route): no navigation the panel sees,
                    // so the address follows from here (the same site only), and "show this comment" finds the page
                    if (!Uri.TryCreate(Str(j, "url"), UriKind.Absolute, out var moved) || CurrentUrl is not { } was
                        || !SameSite(moved, was) || !IsAllowed(moved)) return null;
                    CurrentUrl = moved;
                    Address = Display(moved);
                    return "location";
                case "annotate-exit":
                    IsAnnotating = false;
                    return "annotate-exit";
                case "annotate-tool":
                    AnnotateTool = Str(j, "tool") == "brush" ? AnnotateTool.Brush : AnnotateTool.Mark;
                    return "annotate-tool";
                case "annotation-update":
                    if (PageMark(Str(j, "id")) is not { } edited || Str(j, "comment") is not { } text) return null;
                    Marks.Edit(edited, text);
                    return "annotation-update";
                case "annotation-remove":
                    if (PageMark(Str(j, "id")) is not { } removed) return null;
                    Marks.Remove(removed);
                    return "annotation-remove";
                case "annotation":
                    if (Marks.Items.Count >= LocalMarks.MaxMarks)
                    {
                        Message = Loc.F("At most {0} comments at a time: send or clear some first.", LocalMarks.MaxMarks);
                        return null;
                    }
                    var comment = Str(j, "comment")?.Trim() ?? "";
                    if (comment.Length == 0 || !j.TryGetProperty("element", out var e) || e.ValueKind != JsonValueKind.Object
                        || !j.TryGetProperty("mark", out var m) || m.ValueKind != JsonValueKind.Object) return null;
                    if (!Uri.TryCreate(Str(j, "url"), UriKind.Absolute, out var page) || !IsAllowed(page)) page = CurrentUrl;
                    if (page is null) return null;
                    PageMarkKind? kind = Str(m, "kind") switch { "point" => PageMarkKind.Point, "area" => PageMarkKind.Area, "sketch" => PageMarkKind.Sketch, _ => null };
                    if (kind is not { } k) return null;
                    var sized = k != PageMarkKind.Point;
                    var strokes = k == PageMarkKind.Sketch ? Strokes(m) : null;
                    if (k == PageMarkKind.Sketch && strokes is null) return null;
                    var mark = new PageMark(k, Int(m, "x"), Int(m, "y"), sized ? Int(m, "width") : 0, sized ? Int(m, "height") : 0,
                        Int(m, "pageX"), Int(m, "pageY"), Int(m, "offsetX"), Int(m, "offsetY"), Int(m, "viewportWidth"), Int(m, "viewportHeight"), strokes);
                    if (sized && (mark.Width <= 0 || mark.Height <= 0)) return null;
                    var element = new PageElement(
                        Cap(Str(e, "selector")), Cap(Str(e, "tag"), 40), Cap(Str(e, "html")), Cap(Str(e, "text")), Cap(Str(e, "source")),
                        Int(e, "x"), Int(e, "y"), Int(e, "width"), Int(e, "height"));
                    if (element.Selector.Length == 0) return null;
                    PageInsideElement[] inside = sized && j.TryGetProperty("inside", out var ins) && ins.ValueKind == JsonValueKind.Array
                        ? [.. ins.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                            .Select(x => new PageInsideElement(Cap(Str(x, "selector")), Cap(Str(x, "tag"), 40), Cap(Str(x, "text"))))
                            .Where(x => x.Selector.Length > 0).Take(MaxInside)]
                        : [];
                    var id = Cap(Str(j, "id"), 40);
                    if (id.Length == 0 || Marks.Items.Any(a => a.Id == id)) return null;
                    if (!Marks.Add(new LocalMark(id, comment, Page: new PageAnchor(page.AbsoluteUri, Cap(Str(j, "title"), 200), mark, element, inside)))) return null;
                    if (k == PageMarkKind.Sketch) _ = TakeSketchPictureAsync(ActiveTab, id, mark);
                    return "annotation";
            }
        }
        catch (JsonException)
        {
        }
        return null;

        static string Cap(string? s, int max = MaxField) => s is null ? "" : s.Length <= max ? s : s[..GraphemeText.PrefixLength(s, max)] + "…";
        static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int Int(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) ? Num(v) : 0;
        static int Num(JsonElement v) =>
            v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? (int)Math.Clamp(Math.Round(d), -1_000_000, 1_000_000) : 0;
        // A sketch's strokes: [[x0, y0, x1, y1, …], …] in page px; null when there are none or too many (the page
        // simplifies them well below the limits)
        static int[][]? Strokes(JsonElement m)
        {
            if (!m.TryGetProperty("strokes", out var s) || s.ValueKind != JsonValueKind.Array || s.GetArrayLength() is 0 or > MaxStrokes) return null;
            var strokes = new int[s.GetArrayLength()][];
            var points = 0;
            var i = 0;
            foreach (var stroke in s.EnumerateArray())
            {
                if (stroke.ValueKind != JsonValueKind.Array || stroke.GetArrayLength() is var n && (n < 2 || n % 2 != 0)) return null;
                if ((points += n / 2) > MaxSketchPoints) return null;
                strokes[i++] = [.. stroke.EnumerateArray().Select(Num)];
            }
            return strokes;
        }
    }

    /// <summary>
    /// A sketch's picture: the page shown (which has just drawn the sketch as a saved one, its comment box closed) through
    /// the tab's own snapshot (<see cref="PreviewTab.CaptureScreenshot"/>: the web view's on the built-in engine, the
    /// pane's frame on Nightly), cropped around the sketch. Taken now, while the sketch is in view, and kept with the
    /// mark; without one (no snapshot on this engine, the page went) the request gives the strokes as a path.
    /// </summary>
    private async Task TakeSketchPictureAsync(PreviewTab tab, string id, PageMark mark)
    {
        if (tab.CaptureScreenshot is not { } capture) return;
        try
        {
            await Task.Delay(SketchPictureDelay); // the page repaints without the comment box first
            var shot = await capture(CancellationToken.None);
            var image = await Task.Run(() => Services.ImageAttachments.SketchPicture($"sketch-{id}.png", shot.Png, mark.ViewportWidth,
                mark.X, mark.Y, mark.Width, mark.Height));
            if (image is not null && Marks.Items.FirstOrDefault(m => m.Id == id) is { } taken) Marks.SetImage(taken, image);
        }
        catch (Exception e) when (e is AgentBrowserException or InvalidOperationException or IOException or OperationCanceledException or TimeoutException)
        {
            // No picture: the strokes still go as a path
        }
    }

    /// <summary>How long after a sketch's comment the picture is taken (tests set 0).</summary>
    internal static TimeSpan SketchPictureDelay { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// The pins for the site shown, each with its page, as JSON for the script's setPins. The script draws only the pins
    /// of the page it is on: a single-page app changes its address without a navigation the panel sees (pushState, a
    /// hash route), and the script follows that itself. Comments made on other sites never reach the page.
    /// </summary>
    internal string PinsJson(Uri? page)
    {
        var pins = Marks.Items.Where(a => page is not null && a.PageUri is { } u && SameSite(u, page))
            .Select(a => Pin(a.Id, a.Number, a.PageUri!.AbsoluteUri, a.Page!, a.Note, false))
            .Concat(SharedPins.Select((s, i) => (s, i)).Where(x => page is not null && Uri.TryCreate(x.s.Mark.Page!.Url, UriKind.Absolute, out var u) && SameSite(u, page))
                .Select(x => Pin(SharedPinId(x.s), x.i + 1, x.s.Mark.Page!.Url, x.s.Mark.Page, x.s.Mark.Note, true)))
            .ToArray();
        return JsonSerializer.Serialize(pins, PreviewAnnotationsJson.Default.PinDtoArray);

        static PinDto Pin(string id, int number, string url, PageAnchor p, string note, bool shared) =>
            new(id, number, url, p.Mark.Kind switch { PageMarkKind.Area => "area", PageMarkKind.Sketch => "sketch", _ => "point" }, p.Element.Selector,
                p.Mark.OffsetX, p.Mark.OffsetY, p.Mark.Width, p.Mark.Height, p.Mark.PageX, p.Mark.PageY, note, shared,
                p.Mark.Kind == PageMarkKind.Sketch ? p.Mark.Strokes : null);
    }

    private static bool SameSite(Uri a, Uri b) =>
        Uri.Compare(a, b, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>The same page: address and query, and the fragment when it is a route (<c>#/cart</c>, <c>#!/cart</c>);
    /// an anchor (<c>#top</c>) is the same page. annotate.js's pageKey decides the same way.</summary>
    internal static bool SamePage(Uri a, Uri b) =>
        Uri.Compare(a, b, UriComponents.HttpRequestUrl, UriFormat.Unescaped, StringComparison.Ordinal) == 0
        && RouteOf(a) == RouteOf(b);

    private static string RouteOf(Uri u) => u.Fragment is ['#', '/', ..] or ['#', '!', '/', ..] ? u.Fragment : "";
}

/// <summary>What a drag does in annotate mode.</summary>
public enum AnnotateTool { Mark, Brush }

internal sealed record PinDto(string id, int number, string url, string kind, string selector, int offsetX, int offsetY, int width, int height,
    int pageX, int pageY, string comment, bool shared, int[][]? strokes);

[System.Text.Json.Serialization.JsonSerializable(typeof(PinDto[]))]
internal sealed partial class PreviewAnnotationsJson : System.Text.Json.Serialization.JsonSerializerContext;
