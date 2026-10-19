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
/// area and comments on it; each becomes a local page mark in the chat's one mark list (<see cref="Marks"/>, shared with
/// the message box) and goes with the next message with where the mark is and what finds the element holding it in the
/// code (selector, opening tag, text, box, the source file where the dev build says it) and, for an area, the elements
/// it shows. The page reports through the web view's message bridge; only messages with this panel's token count, and
/// only as many and as long as <see cref="LocalMarks.MaxMarks"/> and the field limits allow.
/// </summary>
public sealed partial class PreviewViewModel
{
    private const int MaxField = 400, MaxInside = 6;

    /// <summary>Shared with the injected script; a message without it did not come from our script.</summary>
    internal string AnnotationToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>The chat's marks (the main view model passes its own); the page's pins are its page marks.</summary>
    public MarkCollection Marks { get; init; } = new();

    /// <summary>Clicking the page marks a point, dragging marks an area, to comment on instead of using the page.</summary>
    [ObservableProperty] private bool _isAnnotating;

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
                    PageMarkKind? kind = Str(m, "kind") switch { "point" => PageMarkKind.Point, "area" => PageMarkKind.Area, _ => null };
                    if (kind is not { } k) return null;
                    var mark = new PageMark(k, Int(m, "x"), Int(m, "y"), k == PageMarkKind.Area ? Int(m, "width") : 0, k == PageMarkKind.Area ? Int(m, "height") : 0,
                        Int(m, "pageX"), Int(m, "pageY"), Int(m, "offsetX"), Int(m, "offsetY"), Int(m, "viewportWidth"), Int(m, "viewportHeight"));
                    if (k == PageMarkKind.Area && (mark.Width <= 0 || mark.Height <= 0)) return null;
                    var element = new PageElement(
                        Cap(Str(e, "selector")), Cap(Str(e, "tag"), 40), Cap(Str(e, "html")), Cap(Str(e, "text")), Cap(Str(e, "source")),
                        Int(e, "x"), Int(e, "y"), Int(e, "width"), Int(e, "height"));
                    if (element.Selector.Length == 0) return null;
                    PageInsideElement[] inside = k == PageMarkKind.Area && j.TryGetProperty("inside", out var ins) && ins.ValueKind == JsonValueKind.Array
                        ? [.. ins.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                            .Select(x => new PageInsideElement(Cap(Str(x, "selector")), Cap(Str(x, "tag"), 40), Cap(Str(x, "text"))))
                            .Where(x => x.Selector.Length > 0).Take(MaxInside)]
                        : [];
                    var id = Cap(Str(j, "id"), 40);
                    if (id.Length == 0 || Marks.Items.Any(a => a.Id == id)) return null;
                    if (!Marks.Add(new LocalMark(id, comment, Page: new PageAnchor(page.AbsoluteUri, Cap(Str(j, "title"), 200), mark, element, inside)))) return null;
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
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)
                ? (int)Math.Clamp(Math.Round(d), -1_000_000, 1_000_000) : 0;
    }

    /// <summary>
    /// The pins for the site shown, each with its page, as JSON for the script's setPins. The script draws only the pins
    /// of the page it is on: a single-page app changes its address without a navigation the panel sees (pushState, a
    /// hash route), and the script follows that itself. Comments made on other sites never reach the page.
    /// </summary>
    internal string PinsJson(Uri? page)
    {
        var pins = Marks.Items.Where(a => page is not null && a.PageUri is { } u && SameSite(u, page))
            .Select(a => new PinDto(a.Id, a.Number, a.PageUri!.AbsoluteUri, a.Page!.Mark.Kind == PageMarkKind.Area ? "area" : "point", a.Page.Element.Selector,
                a.Page.Mark.OffsetX, a.Page.Mark.OffsetY, a.Page.Mark.Width, a.Page.Mark.Height, a.Page.Mark.PageX, a.Page.Mark.PageY, a.Note, false))
            .Concat(SharedPins.Select((s, i) => (s, i)).Where(x => page is not null && Uri.TryCreate(x.s.Mark.Page!.Url, UriKind.Absolute, out var u) && SameSite(u, page))
                .Select(x => new PinDto(SharedPinId(x.s), x.i + 1, x.s.Mark.Page!.Url, x.s.Mark.Page.Mark.Kind == PageMarkKind.Area ? "area" : "point",
                    x.s.Mark.Page.Element.Selector, x.s.Mark.Page.Mark.OffsetX, x.s.Mark.Page.Mark.OffsetY, x.s.Mark.Page.Mark.Width, x.s.Mark.Page.Mark.Height,
                    x.s.Mark.Page.Mark.PageX, x.s.Mark.Page.Mark.PageY, x.s.Mark.Note, true)))
            .ToArray();
        return JsonSerializer.Serialize(pins, PreviewAnnotationsJson.Default.PinDtoArray);
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

internal sealed record PinDto(string id, int number, string url, string kind, string selector, int offsetX, int offsetY, int width, int height,
    int pageX, int pageY, string comment, bool shared);

[System.Text.Json.Serialization.JsonSerializable(typeof(PinDto[]))]
internal sealed partial class PreviewAnnotationsJson : System.Text.Json.Serialization.JsonSerializerContext;
