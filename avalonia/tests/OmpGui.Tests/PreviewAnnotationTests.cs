using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OmpGui.App;
using OmpGui.App.Controls;
using OmpGui.App.ViewModels;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>Annotations: comments left on elements of the previewed page, sent to the agent with the next message.</summary>
public sealed class PreviewAnnotationTests
{
    private static PreviewViewModel Opened(string url = "http://localhost:5173/")
    {
        var vm = new PreviewViewModel();
        vm.OnNavigationCompleted(new Uri(url), true, false, false);
        return vm;
    }

    private static string Message(PreviewViewModel vm, string comment = "Make it blue", string? token = null, string id = "a1",
        string selector = "#login > button.btn", string url = "http://localhost:5173/", string source = "src/Login.tsx:42") =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "annotation", ["token"] = token ?? vm.AnnotationToken, ["id"] = id, ["comment"] = comment, ["url"] = url, ["title"] = "Shop",
            ["element"] = new Dictionary<string, object>
            {
                ["selector"] = selector, ["tag"] = "button", ["html"] = "<button class=\"btn\" type=\"submit\">", ["text"] = "Sign in",
                ["source"] = source, ["x"] = 412.4, ["y"] = 380, ["width"] = 120, ["height"] = 40, ["viewportWidth"] = 1280, ["viewportHeight"] = 800,
            },
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
        Assert.Null(vm.OnPageMessage(new string('x', 70_000)));
        Assert.Empty(vm.Annotations);

        Assert.Equal("annotation", vm.OnPageMessage(Message(vm)));
        Assert.Null(vm.OnPageMessage(Message(vm))); // the same id twice
        Assert.Equal("annotation", vm.OnPageMessage(Message(vm, comment: "Less padding", id: "a2", selector: "main > p:nth-of-type(2)", source: "")));
        Assert.Equal([1, 2], vm.Annotations.Select(a => a.Number));
        var first = vm.Annotations[0];
        Assert.Equal(("Make it blue", 412, 1280), (first.Comment, first.Element.X, first.Element.ViewportWidth));
        Assert.Equal("button — Sign in", first.ElementLabel);
        Assert.Equal("2 comments", vm.AnnotationsTitle);

        first.RemoveCommand.Execute(null);
        Assert.Equal(1, vm.Annotations.Single().Number); // renumbered like the pins
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
        vm.IsAnnotating = true;
        vm.CloseCommand.Execute(null);
        Assert.False(vm.IsAnnotating);
    }

    [Fact]
    public void Comments_are_limited_in_number_and_length()
    {
        var vm = Opened();
        for (var i = 0; i < PreviewViewModel.MaxAnnotations; i++) Assert.Equal("annotation", vm.OnPageMessage(Message(vm, id: "a" + i)));
        Assert.Null(vm.OnPageMessage(Message(vm, id: "over")));
        Assert.Contains("At most", vm.Message);
        vm.ClearAnnotationsCommand.Execute(null);
        vm.OnPageMessage(Message(vm, comment: new string('c', 5000), selector: new string('s', 900)));
        var a = vm.Annotations.Single();
        Assert.True(a.Comment.Length <= 2001 && a.Element.Selector.Length <= 401);
    }

    [Fact]
    public void Pins_and_the_prompt_say_what_finds_each_element()
    {
        var vm = Opened();
        vm.OnPageMessage(Message(vm));
        vm.OnPageMessage(Message(vm, comment: "Remove this", id: "a2", url: "http://localhost:5173/about", selector: "footer", source: ""));
        var pins = JsonDocument.Parse(vm.PinsJson(new Uri("http://localhost:5173/#top"))).RootElement;
        Assert.Equal(1, pins.GetArrayLength()); // only the pins of the page shown (the fragment does not matter)
        Assert.Equal("#login > button.btn", pins[0].GetProperty("selector").GetString());

        var prompt = PreviewViewModel.BuildPrompt(vm.Annotations);
        Assert.Contains("Comments on the page http://localhost:5173/ (\"Shop\")", prompt);
        Assert.Contains("1. Make it blue", prompt);
        Assert.Contains("Element: <button class=\"btn\" type=\"submit\"> with the text \"Sign in\"", prompt);
        Assert.Contains("Selector: #login > button.btn", prompt);
        Assert.Contains("Source: src/Login.tsx:42", prompt);
        Assert.Contains("Box: 120×40 at 412,380 in a 1280×800 viewport", prompt);
        Assert.Contains("Comments on the page http://localhost:5173/about", prompt);
        Assert.Contains("2. Remove this", prompt);
        Assert.EndsWith("make the changes the comments ask for.", prompt);
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
        var w = new OmpGui.App.Views.MainWindow { DataContext = vm, Width = 1180, Height = 760 };
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
        Assert.True(w.FindControl<ItemsControl>("PageCommentsChip")!.IsEffectivelyVisible); // one chip per comment, like files

        vm.ComposerText = "Please fix these";
        vm.SendCommand.Execute(null); // sent with the message, like attachments (Codex)
        while (!vm.Rows.OfType<UserRowViewModel>().Any() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        var sent = vm.Rows.OfType<UserRowViewModel>().First().Text;
        Assert.StartsWith("Please fix these\n\nComments on the page http://localhost:5173/", sent);
        Assert.Contains("Selector: #login > button.btn", sent);
        Assert.Empty(preview.Annotations);
        var row = vm.Rows.OfType<UserRowViewModel>().First();
        // The bubble: the user's words and a chip; the machine-written block only on request (design review T1)
        Assert.True(row.HasComments);
        Assert.Equal("Please fix these", row.DisplayText);
        Assert.Equal("2 comments on localhost:5173", row.CommentsLabel);
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
        Assert.Equal("1 comment on localhost:5173", alone.CommentsLabel);
        Assert.False(w.FindControl<ItemsControl>("PageCommentsChip")!.IsEffectivelyVisible);
        await vm.DisposeAsync();
        w.Close();
    }
}
