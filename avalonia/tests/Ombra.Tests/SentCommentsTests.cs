using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>A sent message's comments read back one by one (LocalMarks.ParseSent) from the request BuildRequest wrote.</summary>
public sealed class SentCommentsTests
{
    private static LocalMark Page(string url, string note, PageMarkKind kind = PageMarkKind.Area) => new(LocalMarks.NewId(), note,
        Page: new PageAnchor(url, "Kiosk", new PageMark(kind, 109, 141, 250, 79, 109, 141, 0, 0, 545, 1220, kind == PageMarkKind.Sketch ? [[1, 2, 3, 4]] : null),
            new PageElement("div.panel > div.k-glow-content", "div", "<div class=\"k-glow-content\">", "", "", 20, 146, 505, 929), []));

    private static string Block(string request)
    {
        var code = request.IndexOf(LocalMarks.CodeMarker, StringComparison.Ordinal);
        return request[(code >= 0 ? code : request.IndexOf(LocalMarks.PageMarker, StringComparison.Ordinal))..];
    }

    [Fact]
    public void Every_comment_comes_back_with_its_number_note_place_and_details()
    {
        LocalMark[] marks =
        [
            new(LocalMarks.NewId(), "Rename this", Code: new CodeAnchor("src/app.py", 41, false, "def a():", CodeMarkOrigin.Editor, EndLine: 43)),
            Page("http://localhost:4322/", "поставь сюда поле поиска\nаккуратно, по центру"),
            new(LocalMarks.NewId(), "resolved, not sent", MarkStatus.Resolved, Code: new CodeAnchor("x.cs", 1, false, "x")),
            Page("http://localhost:4322/", ""),
            Page("https://example.com/a/", "a sketch", PageMarkKind.Sketch),
            new(LocalMarks.NewId(), "whole file", Code: new CodeAnchor("README.md", null, false, "")),
        ];
        var request = LocalMarks.BuildRequest("", marks);

        var sent = LocalMarks.ParseSent(Block(request));

        Assert.Equal([1, 6, 2, 4, 5], sent.Select(c => c.Number)); // code first, then pages, as sent; the resolved one never went
        var code = sent[0];
        Assert.Equal(("Rename this", "src/app.py", 41, (string?)null), (code.Note, code.Path, code.Line, code.Url));
        Assert.Equal("src/app.py, lines 41-43 (in the editor)", code.Where);
        Assert.Contains("> def a():", code.Details);
        Assert.Equal(("whole file", "README.md", (int?)null), (sent[1].Note, sent[1].Path, sent[1].Line));
        var area = sent[2];
        Assert.Equal("поставь сюда поле поиска\nаккуратно, по центру", area.Note); // a note's later lines stay with it
        Assert.Equal(("localhost:4322", "http://localhost:4322/"), (area.Where, area.Url));
        Assert.StartsWith("Area: 250×79 at 109,141", area.Details[0]);
        Assert.Contains("Selector: div.panel > div.k-glow-content", area.Details);
        Assert.Equal("", sent[3].Note); // "(no note)" is no note
        Assert.Equal(("a sketch", "example.com/a"), (sent[4].Note, sent[4].Where)); // "Sketch: …" heading off the note
        Assert.DoesNotContain(sent, c => c.Note.StartsWith("Find these places", StringComparison.Ordinal) || c.Details.Any(d => d.StartsWith("Make the", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_block_without_numbered_comments_gives_none()
    {
        Assert.Empty(LocalMarks.ParseSent(LocalMarks.PageMarker + "http://x/ (\"X\"), left in the preview.\nsomething the old format said"));
    }
}
