using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OmpGui.App.ViewModels;

/// <summary>What identifies a commented element, as the page reported it (Controls/Preview/annotate.js).</summary>
public sealed record PageElement(string Selector, string Tag, string Html, string Text, string Source,
    int X, int Y, int Width, int Height, int ViewportWidth, int ViewportHeight);

/// <summary>A comment left on an element of the previewed page, numbered like its pin on the page.</summary>
public sealed partial class AnnotationViewModel(string id, Uri page, string pageTitle, PageElement element, string comment, PreviewViewModel owner)
    : ObservableObject
{
    public string Id { get; } = id;
    public Uri Page { get; } = page;
    public string PageTitle { get; } = pageTitle;
    public PageElement Element { get; } = element;
    public string Comment { get; } = comment;

    [ObservableProperty] private int _number;

    /// <summary>The element in a few words for the list: "button.primary — Sign in".</summary>
    public string ElementLabel => Element.Text.Length > 0 ? $"{Element.Tag} — {Element.Text}" : Element.Selector;

    [RelayCommand]
    private void Remove() => owner.RemoveAnnotation(this);
}

/// <summary>
/// Annotations (Codex's page comments): in annotate mode the user clicks elements of the previewed page and comments on
/// them; the comments go with the next message, each with what finds the element in the code (selector, opening tag,
/// text, box, the source file where the dev build says it). The page reports through the web view's message bridge;
/// only messages with this panel's token count, and only as many and as long as <see cref="MaxAnnotations"/> and the
/// field limits allow.
/// </summary>
public sealed partial class PreviewViewModel
{
    public const int MaxAnnotations = 20;

    /// <summary>How the comments block begins in a sent message (the conversation shows it as a chip).</summary>
    public const string PromptMarker = "Comments on the page ";

    /// <summary>The first line when the comments go without words of the user's own.</summary>
    public const string CommentsOnlyLine = "Please make the changes my comments on the page ask for.";
    private const int MaxComment = 2000, MaxField = 400;

    /// <summary>Shared with the injected script; a message without it did not come from our script.</summary>
    internal string AnnotationToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public ObservableCollection<AnnotationViewModel> Annotations { get; } = [];

    /// <summary>Clicking the page comments on an element instead of using it.</summary>
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

    /// <summary>
    /// A message from the page's script. Returns what it was: "annotation", "annotate-exit", "ready", or null when it
    /// is not ours (no or a wrong token, not JSON, missing fields) and was ignored.
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
                case "annotation":
                    if (Annotations.Count >= MaxAnnotations)
                    {
                        Message = $"At most {MaxAnnotations} comments at a time: send or clear some first.";
                        return null;
                    }
                    var comment = Str(j, "comment")?.Trim() ?? "";
                    if (comment.Length == 0 || !j.TryGetProperty("element", out var e) || e.ValueKind != JsonValueKind.Object) return null;
                    if (!Uri.TryCreate(Str(j, "url"), UriKind.Absolute, out var page) || !IsAllowed(page)) page = CurrentUrl;
                    if (page is null) return null;
                    var element = new PageElement(
                        Cap(Str(e, "selector")), Cap(Str(e, "tag"), 40), Cap(Str(e, "html")), Cap(Str(e, "text")), Cap(Str(e, "source")),
                        Int(e, "x"), Int(e, "y"), Int(e, "width"), Int(e, "height"), Int(e, "viewportWidth"), Int(e, "viewportHeight"));
                    if (element.Selector.Length == 0) return null;
                    var id = Cap(Str(j, "id"), 40);
                    if (id.Length == 0 || Annotations.Any(a => a.Id == id)) return null;
                    Annotations.Add(new AnnotationViewModel(id, page, Cap(Str(j, "title"), 200), element, Cap(comment, MaxComment), this));
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
            .Select(a => new PinDto(a.Id, a.Number, a.Element.Selector, a.Comment)).ToArray();
        return JsonSerializer.Serialize(pins, PreviewAnnotationsJson.Default.PinDtoArray);
    }

    private static bool SamePage(Uri a, Uri b) =>
        Uri.Compare(a, b, UriComponents.HttpRequestUrl, UriFormat.Unescaped, StringComparison.Ordinal) == 0;

    /// <summary>The comments as a message for the agent: per page, each comment with what finds its element in the code.</summary>
    public static string BuildPrompt(IReadOnlyList<AnnotationViewModel> annotations)
    {
        if (annotations.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var page in annotations.GroupBy(a => a.Page.GetLeftPart(UriPartial.Query)))
        {
            if (sb.Length > 0) sb.Append('\n');
            var title = page.First().PageTitle;
            sb.Append(CultureInfo.InvariantCulture, $"{PromptMarker}{page.Key}{(title.Length > 0 ? $" (\"{title}\")" : "")}, left in the preview.");
            sb.Append(" The element details are quoted from the page:\n");
            foreach (var a in page)
            {
                var e = a.Element;
                sb.Append(CultureInfo.InvariantCulture, $"\n{a.Number}. {a.Comment}\n");
                sb.Append(CultureInfo.InvariantCulture, $"   Element: {e.Html}");
                if (e.Text.Length > 0) sb.Append(CultureInfo.InvariantCulture, $" with the text \"{e.Text}\"");
                sb.Append('\n');
                sb.Append(CultureInfo.InvariantCulture, $"   Selector: {e.Selector}\n");
                if (e.Source.Length > 0) sb.Append(CultureInfo.InvariantCulture, $"   Source: {e.Source}\n");
                sb.Append(CultureInfo.InvariantCulture, $"   Box: {e.Width}×{e.Height} at {e.X},{e.Y} in a {e.ViewportWidth}×{e.ViewportHeight} viewport\n");
            }
        }
        sb.Append("\nFind these elements in the project's source and make the changes the comments ask for.");
        return sb.ToString();
    }
}

internal sealed record PinDto(string id, int number, string selector, string comment);

[System.Text.Json.Serialization.JsonSerializable(typeof(PinDto[]))]
internal sealed partial class PreviewAnnotationsJson : System.Text.Json.Serialization.JsonSerializerContext;
