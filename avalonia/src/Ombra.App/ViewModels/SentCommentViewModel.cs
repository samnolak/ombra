using CommunityToolkit.Mvvm.ComponentModel;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// One comment sent with a message (<see cref="UserRowViewModel.Comments"/>): a row with its number, note and place;
/// opened, the details the request quoted. Its place opens from the row (the page in the Browser, the code in Files).
/// </summary>
public sealed partial class SentCommentViewModel(SentComment comment, bool showPlace) : ObservableObject
{
    public SentComment Model => comment;
    public string Number => comment.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Note => comment.Note.Length > 0 ? comment.Note : Loc.T("(no note)");
    public bool HasNote => comment.Note.Length > 0;
    /// <summary>
    /// The line under the note: what was marked on the page (<c>button "Sign in"</c>, <c>div.k-glow-content</c>) or the
    /// code place; the page's address before it only when the message's comments are on more than one place (the chip
    /// names the one).
    /// </summary>
    public string Where { get; } = Caption(comment, showPlace);

    private static string Caption(SentComment c, bool showPlace)
    {
        if (c.Url is null) return c.Where;
        var target = c.Details.FirstOrDefault(d => d.StartsWith("On: ", StringComparison.Ordinal) || d.StartsWith("Within: ", StringComparison.Ordinal)
            || d.StartsWith("Over: ", StringComparison.Ordinal)) is { } line ? Element(line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..]) : "";
        return showPlace && target.Length > 0 ? $"{c.Where} · {target}" : target.Length > 0 ? target : c.Where;
    }

    /// <summary><c>&lt;button class="btn primary" …&gt; with the text "Sign in"</c> → <c>button "Sign in"</c>; no text → <c>button.btn</c>.</summary>
    private static string Element(string quoted)
    {
        var m = System.Text.RegularExpressions.Regex.Match(quoted, "^<([A-Za-z][\\w-]*)([^>]*)>(?: with the text \"(.*)\")?$");
        if (!m.Success) return quoted;
        if (m.Groups[3].Success) return $"{m.Groups[1].Value} \"{m.Groups[3].Value}\"";
        var cls = System.Text.RegularExpressions.Regex.Match(m.Groups[2].Value, "class=\"([^\" ]+)");
        return cls.Success ? $"{m.Groups[1].Value}.{cls.Groups[1].Value}" : m.Groups[1].Value;
    }

    /// <summary>
    /// A page comment's details as key / value rows (where, selector, source, box, what the area holds); the element
    /// line the caption already names is left out. A code comment has none: its lines are <see cref="Quote"/>.
    /// </summary>
    public IReadOnlyList<SentCommentField> Fields { get; } = comment.Url is null ? [] : FieldsOf(comment.Details);

    /// <summary>A code comment's quoted lines (and the lines around them), as sent.</summary>
    public string Quote => comment.Url is null ? string.Join('\n', comment.Details) : "";
    public bool HasFields => Fields.Count > 0;
    public bool HasQuote => Quote.Length > 0;

    private static SentCommentField[] FieldsOf(IReadOnlyList<string> details) =>
    [
        .. details.Select(d => d.IndexOf(": ", StringComparison.Ordinal) is var at and > 0 ? (Key: d[..at], Value: d[(at + 2)..]) : (Key: "", Value: d))
            .Where(f => f.Key is not ("On" or "Within" or "Over"))
            .Select(f => new SentCommentField(f.Key switch
            {
                "Point" => Loc.T("Point"),
                "Area" => Loc.T("Area"),
                "Sketch" => Loc.T("Sketch"),
                "Selector" => Loc.T("Selector"),
                "Source" => Loc.T("Source"),
                "Element box" => Loc.T("Box"),
                "Inside the area" or "Under the sketch" => Loc.T("Inside"),
                "Strokes (SVG path, CSS px on the page)" => Loc.T("Strokes"),
                var other => other,
            }, f.Value)),
    ];
    /// <summary>The page or the code place can be opened.</summary>
    public bool CanOpen => comment.Url is not null || comment.Path is not null;
    public string OpenLabel => comment.Url is not null ? Loc.T("Open the page") : Loc.T("Open in the Files pane");
    public string ToggleName => Loc.F("Comment {0}: {1}", Number, Note);

    /// <summary>Its details are shown (one comment of the message at a time).</summary>
    [ObservableProperty] private bool _isExpanded;
}
