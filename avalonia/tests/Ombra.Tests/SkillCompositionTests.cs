using Avalonia.Headless.XUnit;
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>A skill goes with whatever else the message has: text before it, images, other skills, a run under way.</summary>
public sealed class SkillCompositionTests
{
    private static ImageAttachment Image() => new("shot.png", "image/png",
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jf9sAAAAASUVORK5CYII="));

    [Theory]
    [InlineData("/skill:a fix it", "/skill:a fix it")]
    [InlineData("fix /skill:a and /skill:b please", "fix /skill:a and skill://b please")]
    [InlineData("/skill:a /skill:b /skill:ns/c", "/skill:a skill://b skill://ns/c")]
    [InlineData("/model fake /skill:a", "/model fake /skill:a")] // another command's arguments
    [InlineData("! echo /skill:a /skill:b", "! echo /skill:a /skill:b")] // a shell line
    [InlineData("see /skill:a/b/c", "see /skill:a/b/c")] // a path, no skill
    public void Omp_runs_one_skill_per_message_and_the_others_reach_the_model_as_references(string text, string sent) =>
        Assert.Equal(sent, SkillTokens.ForOmp(text));

    [AvaloniaFact]
    public async Task A_skill_can_follow_text_and_an_image_from_the_plus_menu_or_typed_mid_message()
    {
        var e = await SessionAreaTests.OpenAsync();
        var vm = e.Vm;
        await SessionAreaTests.Until(() => vm.Phase == SessionPhase.Ready, "ready");
        Assert.True(vm.TryAddImage(Image(), "test"));
        vm.ComposerText = "look at this";

        // "+" → Commands and skills after what is written: a "/" at the end lists the skills (only they can go there)
        vm.StartSlashCommandCommand.Execute(null);
        Assert.Equal("look at this /", vm.ComposerText);
        Assert.True(vm.IsCommandMenuOpen);
        Assert.Equal(["/skill:systematic-debugging"], vm.CommandSuggestions.Select(s => s.Label));
        vm.ChooseSuggestionCommand.Execute(vm.CommandSuggestions[0]);
        Assert.Equal("look at this /skill:systematic-debugging ", vm.ComposerText);
        Assert.False(vm.IsCommandMenuOpen);

        // Typed in the middle: the word at the caret is completed in place, the rest stays; a path opens nothing
        vm.ComposerText = "check /sys then the logs";
        vm.ComposerCaretIndex = "check /sys".Length;
        Assert.Equal(["/skill:systematic-debugging"], vm.CommandSuggestions.Select(s => s.Label));
        Assert.True(vm.AcceptSuggestion(enter: true));
        Assert.Equal("check /skill:systematic-debugging then the logs", vm.ComposerText);
        vm.ComposerText = "see /tmp/out.log";
        vm.ComposerCaretIndex = vm.ComposerText.Length;
        Assert.False(vm.IsCommandMenuOpen);

        // Sent with the image
        vm.ComposerText = "look at this /skill:systematic-debugging";
        vm.SendCommand.Execute(null);
        await SessionAreaTests.Until(() => vm.Rows.OfType<UserRowViewModel>().Any(), "sent");
        var row = vm.Rows.OfType<UserRowViewModel>().Single();
        Assert.Equal("look at this /skill:systematic-debugging", row.Text);
        Assert.True(row.HasImages);
        await SessionAreaTests.Close(e);
    }

    [AvaloniaFact]
    public async Task A_skill_sent_while_omp_works_is_queued_as_a_prompt_so_omp_runs_it()
    {
        // omp expands /skill: only on prompt; steer and follow_up pass the text to the model as it is
        var e = await SessionAreaTests.OpenAsync("slow-stream");
        var vm = e.Vm;
        await SessionAreaTests.Until(() => vm.Phase == SessionPhase.Ready, "ready");
        await SessionAreaTests.Send(e, "start working");
        await SessionAreaTests.Until(() => vm.Phase == SessionPhase.Running, "running");
        await SessionAreaTests.Send(e, "/skill:systematic-debugging then this");
        await SessionAreaTests.Until(() => SessionAreaTests.Sent(e).Contains("/skill:systematic-debugging then this"), "queued as a prompt");
        await SessionAreaTests.Until(() => vm.HasQueued, "queued");
        await SessionAreaTests.Close(e);
    }
}
