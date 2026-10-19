using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ombra.App;
using Ombra.App.Controls;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>Annotations: comments left on points and areas of the previewed page, sent to the agent with the next message.</summary>
public sealed class PreviewAnnotationTests
{
    private static PreviewViewModel Opened(string url = "http://localhost:5173/")
    {
        var vm = new PreviewViewModel();
        vm.OnNavigationCompleted(new Uri(url), true, false, false);
        return vm;
    }

    private static string Message(PreviewViewModel vm, string comment = "Make it blue", string? token = null, string id = "a1",
        string selector = "#login > button.btn", string url = "http://localhost:5173/", string source = "src/Login.tsx:42",
        string kind = "point", int width = 0, int height = 0, object[]? inside = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "annotation", ["token"] = token ?? vm.AnnotationToken, ["id"] = id, ["comment"] = comment, ["url"] = url, ["title"] = "Shop",
            ["mark"] = new Dictionary<string, object>
            {
                ["kind"] = kind, ["x"] = 452.4, ["y"] = 400, ["width"] = width, ["height"] = height, ["pageX"] = 452, ["pageY"] = 1200,
                ["offsetX"] = 40, ["offsetY"] = 20, ["viewportWidth"] = 1280, ["viewportHeight"] = 800,
            },
            ["element"] = new Dictionary<string, object>
            {
                ["selector"] = selector, ["tag"] = "button", ["html"] = "<button class=\"btn\" type=\"submit\">", ["text"] = "Sign in",
                ["source"] = source, ["x"] = 412.4, ["y"] = 380, ["width"] = 120, ["height"] = 40,
            },
            ["inside"] = inside ?? [],
        });

    [Fact]
    public void Only_messages_from_our_script_become_comments()
    {
        var vm = Opened();
        Assert.Null(vm.OnPageMessage(Message(vm, token: "forged")));
        Assert.Null(vm.OnPageMessage("{not json"));
        Assert.Null(vm.OnPageMessage(""));
        Assert.Null(vm.OnPageMessage(Message(vm, comment: "   ")));
        Assert.Null(vm.OnPageMessage(Message(vm, selector: "")));
        Assert.Null(vm.OnPageMessage(Message(vm, kind: "element")));
        Assert.Null(vm.OnPageMessage(Message(vm, kind: "area"))); // an area needs a size
        Assert.Null(vm.OnPageMessage(new string('x', 70_000)));
        Assert.Empty(vm.Marks.Items);

        Assert.Equal("annotation", vm.OnPageMessage(Message(vm)));
        Assert.Null(vm.OnPageMessage(Message(vm))); // the same id twice
        Assert.Equal("annotation", vm.OnPageMessage(Message(vm, comment: "Less padding", id: "a2", selector: "main > p:nth-of-type(2)", source: "")));
        Assert.Equal([1, 2], vm.Marks.Items.Select(a => a.Number));
        var first = vm.Marks.Items[0];
        Assert.Equal(("Make it blue", 452, 1280, 412), (first.Note, first.Page!.Mark.X, first.Page.Mark.ViewportWidth, first.Page.Element.X));
        Assert.Equal(Loc.F("Point on {0}", "button — Sign in"), first.TargetLabel);
        Assert.Equal(Loc.Plural(2, "1 comment", "{0} comments"), vm.Marks.Title);

        first.RemoveCommand.Execute(null);
        Assert.Equal(1, vm.Marks.Items.Single().Number); // renumbered like the pins
        Assert.Equal("ready", vm.OnPageMessage(JsonSerializer.Serialize(new { type = "ready", token = vm.AnnotationToken })));
    }

    [Fact]
    public void Annotate_mode_needs_a_page_and_the_page_can_end_it()
    {
        var empty = new PreviewViewModel();
        Assert.False(empty.ToggleAnnotateCommand.CanExecute(null));
        var vm = Opened();
        vm.ToggleAnnotateCommand.Execute(null);
        Assert.True(vm.IsAnnotating);
        Assert.Equal("annotate-exit", vm.OnPageMessage(JsonSerializer.Serialize(new { type = "annotate-exit", token = vm.AnnotationToken })));
        Assert.False(vm.IsAnnotating);
    }

    [Fact]
    public void Comments_are_limited_in_number_and_length()
    {
        var vm = Opened();
        for (var i = 0; i < LocalMarks.MaxMarks; i++) Assert.Equal("annotation", vm.OnPageMessage(Message(vm, id: "a" + i)));
        Assert.Null(vm.OnPageMessage(Message(vm, id: "over")));
        Assert.Equal(Loc.F("At most {0} comments at a time: send or clear some first.", LocalMarks.MaxMarks), vm.Message);
        vm.ClearAnnotationsCommand.Execute(null);
        vm.OnPageMessage(Message(vm, comment: new string('c', 5000), selector: new string('s', 900)));
        var a = vm.Marks.Items.Single();
        Assert.True(a.Note.Length <= 2001 && a.Page!.Element.Selector.Length <= 401);
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("e\u0301")]
    [InlineData("👩‍💻")]
    public void Annotation_comments_and_element_fields_keep_whole_graphemes(string glyph)
    {
        for (var cut = 1; cut < glyph.Length; cut++)
        {
            var vm = Opened();
            var commentHead = new string('c', 2000 - cut);
            var sourceHead = new string('s', 400 - cut);
            Assert.Equal("annotation", vm.OnPageMessage(Message(vm,
                comment: commentHead + glyph + " rest", source: sourceHead + glyph + " rest")));
            var annotation = Assert.Single(vm.Marks.Items);
            Assert.Equal(commentHead + "…", annotation.Note);
            Assert.Equal(sourceHead + "…", annotation.Page!.Element.Source);
            annotation.BeginEditCommand.Execute(null);
            annotation.EditText = sourceHead + new string('d', 1600) + glyph + " rest";
            annotation.SaveEditCommand.Execute(null);
            Assert.Equal(sourceHead + new string('d', 1600) + "…", annotation.Note);
        }
    }

    [Fact]
    public void A_comment_can_be_changed_or_removed_from_its_pin_or_its_chip()
    {
        var vm = Opened();
        vm.OnPageMessage(Message(vm));
        vm.OnPageMessage(Message(vm, comment: "Less padding", id: "a2", url: "http://localhost:5173/about", selector: "main > p"));
        var edits = 0;
        vm.Marks.Edited += () => edits++;

        // From the pin on the page
        string Page(string type, string id, string? comment = null) =>
            JsonSerializer.Serialize(new { type, token = vm.AnnotationToken, id, comment });
        Assert.Equal("annotation-update", vm.OnPageMessage(Page("annotation-update", "a1", "Make it red")));
        Assert.Equal("Make it red", vm.Marks.Items[0].Note);
        Assert.Null(vm.OnPageMessage(Page("annotation-update", "nope", "x"))); // an unknown comment
        Assert.Null(vm.OnPageMessage(JsonSerializer.Serialize(new { type = "annotation-update", token = "forged", id = "a1", comment = "x" })));
        Assert.Equal("annotation-remove", vm.OnPageMessage(Page("annotation-remove", "a1")));
        Assert.Equal(1, vm.Marks.Items.Single().Number); // renumbered

        // From the chip: an empty text changes nothing, Cancel keeps it, Save keeps the new text and tells the pins
        var chip = vm.Marks.Items[0];
        chip.BeginEditCommand.Execute(null);
        Assert.Equal(("Less padding", true), (chip.EditText, chip.IsEditing));
        chip.EditText = "   ";
        chip.SaveEditCommand.Execute(null);
        Assert.Equal("Less padding", chip.Note);
        chip.BeginEditCommand.Execute(null);
        chip.EditText = "No padding";
        chip.CancelEditCommand.Execute(null);
        Assert.Equal("Less padding", chip.Note);
        chip.BeginEditCommand.Execute(null);
        chip.EditText = " No padding ";
        chip.SaveEditCommand.Execute(null);
        Assert.Equal(("No padding", false), (chip.Note, chip.IsEditing));
        Assert.Equal(2, edits);
        Assert.Contains("1. No padding", LocalMarks.PageBlock(vm.Marks.Models())); // the agent gets the new text

        // Resolved: it stays listed (its pin too) but is not sent; reopened, it is sent again
        chip.ToggleResolvedCommand.Execute(null);
        Assert.Equal((MarkStatus.Resolved, 3), (chip.Status, edits));
        Assert.Equal("", LocalMarks.PageBlock(vm.Marks.Models()));
        Assert.Single(JsonDocument.Parse(vm.PinsJson(new Uri("http://localhost:5173/"))).RootElement.EnumerateArray());
        chip.ToggleResolvedCommand.Execute(null);
        Assert.Contains("1. No padding", LocalMarks.PageBlock(vm.Marks.Models()));

        // The chip names its page and opens it
        Assert.Equal("localhost:5173/about", chip.Where);
        Assert.StartsWith("Shop — localhost:5173/about\n" + Loc.F("Point on {0}", "button — Sign in"), chip.Tooltip);
        var shown = 0;
        vm.ShowRequested += () => shown++;
        vm.Marks.ShowRequested += m => vm.ShowMark(m);
        chip.ShowCommand.Execute(null);
        Assert.Equal(1, shown);
        Assert.Equal("http://localhost:5173/about", vm.Address);
    }

    [Fact]
    public void Pins_and_the_prompt_say_where_each_mark_is_and_what_holds_it()
    {
        var vm = Opened();
        vm.OnPageMessage(Message(vm));
        vm.OnPageMessage(Message(vm, comment: "Remove this", id: "a2", url: "http://localhost:5173/about", selector: "footer", source: ""));
        vm.OnPageMessage(Message(vm, comment: "Tighter cards", id: "a3", selector: "section.cards", kind: "area", width: 240, height: 120,
            inside: [new { selector = "#buy", tag = "button", text = "Buy" }, new { selector = "main > h2", tag = "h2", text = "" }]));
        Assert.Equal(Loc.F("Area {0}×{1} in {2}", 240, 120, "button — Sign in"), vm.Marks.Items[2].TargetLabel);
        vm.OnPageMessage(Message(vm, comment: "Elsewhere", id: "a4", url: "http://localhost:3000/"));
        var pins = JsonDocument.Parse(vm.PinsJson(new Uri("http://localhost:5173/#top"))).RootElement;
        // The site's pins, each with its page (the script draws a pin on its own page only); another site's never
        Assert.Equal(["http://localhost:5173/", "http://localhost:5173/about", "http://localhost:5173/"],
            pins.EnumerateArray().Select(p => p.GetProperty("url").GetString()));
        Assert.Equal(("point", "#login > button.btn", 40, 20, 1200), (pins[0].GetProperty("kind").GetString(), pins[0].GetProperty("selector").GetString(),
            pins[0].GetProperty("offsetX").GetInt32(), pins[0].GetProperty("offsetY").GetInt32(), pins[0].GetProperty("pageY").GetInt32()));
        Assert.Equal(("area", 240, 120), (pins[2].GetProperty("kind").GetString(), pins[2].GetProperty("width").GetInt32(), pins[2].GetProperty("height").GetInt32()));
        vm.Marks.Remove(vm.Marks.Items.Single(a => a.Id == "a4"));

        var prompt = LocalMarks.PageBlock(vm.Marks.Models());
        Assert.Contains("Comments on the page http://localhost:5173/ (\"Shop\")", prompt);
        Assert.Contains("1. Make it blue\n   Point: 452,400 in a 1280×800 viewport (452,1200 on the page)\n", prompt);
        Assert.Contains("   On: <button class=\"btn\" type=\"submit\"> with the text \"Sign in\"", prompt);
        Assert.Contains("Selector: #login > button.btn", prompt);
        Assert.Contains("Source: src/Login.tsx:42", prompt);
        Assert.Contains("Element box: 120×40 at 412,380", prompt);
        Assert.Contains("3. Tighter cards\n   Area: 240×120 at 452,400 in a 1280×800 viewport (452,1200 on the page)\n   Within: ", prompt);
        Assert.Contains("Inside the area: button \"Buy\" (#buy); main > h2", prompt);
        Assert.Contains("Comments on the page http://localhost:5173/about", prompt);
        Assert.Contains("2. Remove this", prompt);
        Assert.EndsWith("make the changes the comments ask for.", prompt);
    }

    [Theory]
    [InlineData("http://localhost:5173/a", "http://localhost:5173/a#top", true)]      // an anchor is the same page
    [InlineData("http://localhost:5173/a", "http://localhost:5173/b", false)]
    [InlineData("http://localhost:5173/a?tab=1", "http://localhost:5173/a?tab=2", false)]
    [InlineData("http://localhost:5173/#/cart", "http://localhost:5173/#/orders", false)] // hash routes are pages
    [InlineData("http://localhost:5173/#!/cart", "http://localhost:5173/#!/cart", true)]
    [InlineData("http://localhost:5173/#/cart", "http://localhost:5173/", false)]
    public void A_comment_belongs_to_its_page_route_included_anchor_not(string a, string b, bool same) =>
        Assert.Equal(same, PreviewViewModel.SamePage(new Uri(a), new Uri(b)));

    [Fact]
    public void A_route_change_inside_the_page_moves_the_address_on_the_same_site_only()
    {
        var vm = Opened();
        string Location(string url, string? token = null) => JsonSerializer.Serialize(new { type = "location", token = token ?? vm.AnnotationToken, url });
        Assert.Equal("location", vm.OnPageMessage(Location("http://localhost:5173/orders")));
        Assert.Equal("http://localhost:5173/orders", vm.CurrentUrl?.AbsoluteUri);
        Assert.Null(vm.OnPageMessage(Location("https://evil.example/")));
        Assert.Null(vm.OnPageMessage(Location("http://localhost:5173/x", token: "forged")));
        Assert.Equal("http://localhost:5173/orders", vm.CurrentUrl?.AbsoluteUri);
    }

    [Fact]
    public void The_page_script_carries_the_token_and_parses()
    {
        var vm = new PreviewViewModel();
        var script = PreviewPanel.AnnotateScriptFor(vm.AnnotationToken);
        Assert.DoesNotContain("__OMP_TOKEN__", script);
        Assert.Contains("})(\"" + vm.AnnotationToken + "\");", script);
        Assert.NotEqual(vm.AnnotationToken, new PreviewViewModel().AnnotationToken);
        // A syntax check where Node is at hand (the real engines run it in the E2E check)
        var node = new[] { "/opt/node22/bin/node", "/usr/bin/node", "/usr/local/bin/node" }.FirstOrDefault(File.Exists);
        if (node is null) return;
        var file = Path.Combine(TestProcesses.TempDir("annotate"), "annotate.js");
        File.WriteAllText(file, script);
        using var p = Process.Start(new ProcessStartInfo(node, ["--check", file]) { RedirectStandardError = true })!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
    }

    [Fact]
    public void Short_messages_are_not_folded()
    {
        var row = new UserRowViewModel(new UserItem(1, "short", true));
        Assert.False(row.IsLong);
        Assert.Equal("short", row.DisplayText);
    }

    [AvaloniaFact]
    public async Task Comments_go_with_the_next_message_and_are_cleared()
    {
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var w = new Ombra.App.Views.MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (vm.Phase != SessionPhase.Ready && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        Assert.False(vm.SendCommand.CanExecute(null));

        var preview = vm.Preview;
        preview.OnNavigationCompleted(new Uri("http://localhost:5173/"), true, false, false);
        preview.OnPageMessage(Message(preview));
        preview.OnPageMessage(Message(preview, comment: "Less padding", id: "a2", selector: "main > p"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.SendCommand.CanExecute(null)); // comments alone are a message
        Assert.True(w.FindControl<ItemsControl>("MarksList")!.IsEffectivelyVisible); // one chip per comment, like files

        vm.ComposerText = "Please fix these";
        vm.SendCommand.Execute(null); // sent with the message, like attachments (Codex)
        while (!vm.Rows.OfType<UserRowViewModel>().Any() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        var sent = vm.Rows.OfType<UserRowViewModel>().First().Text;
        Assert.StartsWith("Please fix these\n\nComments on the page http://localhost:5173/", sent);
        Assert.Contains("Selector: #login > button.btn", sent);
        Assert.Empty(vm.Marks.Items);
        var row = vm.Rows.OfType<UserRowViewModel>().First();
        // The bubble: the user's words and a chip; the machine-written block only on request (design review T1)
        Assert.True(row.HasComments);
        Assert.Equal("Please fix these", row.DisplayText);
        Assert.Equal(Loc.F("{0} on {1}", Loc.F("{0} comments", 2), "localhost:5173"), row.CommentsLabel);
        row.ToggleFoldCommand.Execute(null);
        Assert.Equal(row.Text, row.DisplayText);

        // Comments alone: a first line says what to do (it also names a new session)
        while (vm.Phase != SessionPhase.Ready && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        preview.OnPageMessage(Message(preview, id: "a3"));
        Dispatcher.UIThread.RunJobs();
        vm.SendCommand.Execute(null);
        while (vm.Rows.OfType<UserRowViewModel>().Count() < 2 && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        var alone = vm.Rows.OfType<UserRowViewModel>().Last();
        Assert.StartsWith("Please make the changes my comments on the page ask for.\n\nComments on the page", alone.Text);
        Assert.False(alone.HasOwnText); // just the chip
        Assert.Equal(Loc.F("{0} on {1}", Loc.T("1 comment"), "localhost:5173"), alone.CommentsLabel);
        Assert.False(w.FindControl<ItemsControl>("MarksList")!.IsEffectivelyVisible);
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>Marks from every place — a line in Changes, the editor, a diff in the conversation, a page — are one
    /// numbered list and one request: code first, then pages, resolved marks left out and kept.</summary>
    [AvaloniaFact]
    public async Task Marks_from_every_place_flow_into_one_request()
    {
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var w = new Ombra.App.Views.MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (vm.Phase != SessionPhase.Ready && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }

        Assert.True(vm.AddCodeMark(new CodeAnchor("src/app.py", 41, false, "print('x')", CodeMarkOrigin.Review), "log it"));
        Assert.True(vm.AddCodeMark(new CodeAnchor("src/app.py", 3, false, "a = 1\nb = 2", CodeMarkOrigin.Editor, EndLine: 4, Before: ["import os"], After: ["c = 3"]), "merge these"));
        vm.CommentOnChangeCommand.Execute(new DiffLineComment("src/util.py", 7, true, "old()", ["before()"], ["after()"], "why removed?"));
        var preview = vm.Preview;
        preview.OnNavigationCompleted(new Uri("http://localhost:5173/"), true, false, false);
        preview.OnPageMessage(Message(preview));
        vm.AddCodeMark(new CodeAnchor("src/done.py", 1, false, "x"), "already done");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([1, 2, 3, 4, 5], vm.Marks.Items.Select(m => m.Number));
        Assert.Equal(CodeMarkOrigin.Conversation, vm.Marks.Items[2].Code!.Origin);
        vm.Marks.Items[4].ToggleResolvedCommand.Execute(null);
        Assert.True(w.FindControl<ItemsControl>("MarksList")!.IsEffectivelyVisible);
        Assert.Equal(5, w.FindControl<ItemsControl>("MarksList")!.ItemCount);

        vm.SendCommand.Execute(null);
        while (!vm.Rows.OfType<UserRowViewModel>().Any() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        var sent = vm.Rows.OfType<UserRowViewModel>().First().Text;
        Assert.StartsWith(LocalMarks.AllOnlyLine + "\n\n" + LocalMarks.CodeMarker, sent);
        Assert.Contains("1. src/app.py, line 41: log it\n   > print('x')\n", sent);
        Assert.Contains("2. src/app.py, lines 3-4 (in the editor): merge these\n   > a = 1\n   > b = 2\n   Lines before it:\n     import os\n   Lines after it:\n     c = 3\n", sent);
        Assert.Contains("3. src/util.py, line 7 of the last commit (a removed line) (on a change in the conversation): why removed?\n   > old()\n", sent);
        Assert.Contains("\n\n" + LocalMarks.PageMarker + "http://localhost:5173/", sent);
        Assert.Contains("\n4. Make it blue\n", sent);
        Assert.DoesNotContain("already done", sent);
        Assert.True(sent.IndexOf(LocalMarks.CodeMarker, StringComparison.Ordinal) < sent.IndexOf(LocalMarks.PageMarker, StringComparison.Ordinal));
        // The resolved mark stays; the sent ones are gone
        var left = Assert.Single(vm.Marks.Items);
        Assert.Equal(("already done", MarkStatus.Resolved, 1), (left.Note, left.Status, left.Number));
        Assert.False(vm.SendCommand.CanExecute(null)); // a resolved mark alone is not a message
        await vm.DisposeAsync();
        w.Close();
    }
}
