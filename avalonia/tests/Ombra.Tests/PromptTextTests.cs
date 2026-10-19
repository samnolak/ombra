using Avalonia.Headless.XUnit;
using Ombra.App;
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class PromptTextTests
{
    [Fact]
    public void Cleanup_removes_hidden_controls_without_corrupting_language_or_emoji()
    {
        var text = "می\u200Cروم 👩\u200D💻 ❤️ 漢\U000E0100\r\n\tword\u00A0boundary\u2028next\u202E\u200B\0\U000E0061";
        var cleaned = PromptText.Clean(text);
        Assert.Equal("می\u200Cروم 👩\u200D💻 ❤️ 漢\U000E0100\n\tword boundary\nnext", cleaned.Text);
        Assert.Equal(6, cleaned.Removed);
        Assert.Equal(cleaned.Text, PromptText.Clean(cleaned.Text).Text);
        Assert.Equal(0, PromptText.Clean(cleaned.Text).Removed);
    }

    [AvaloniaTheory]
    [InlineData("😀")]
    [InlineData("e\u0301")]
    [InlineData("👩‍💻")]
    public async Task Pasted_text_preview_clips_graphemes_without_changing_the_sent_text(string glyph)
    {
        await using var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        for (var cut = 1; cut < glyph.Length; cut++)
        {
            var head = new string('a', 120 - cut);
            var source = head + glyph + " rest";
            var paste = new PastedTextViewModel(source, vm);
            Assert.Equal(head + "…", paste.Preview);
            Assert.Equal(source, paste.Text);
        }
    }

    [AvaloniaFact]
    public async Task Cleanup_cannot_silently_turn_a_draft_into_a_local_command()
    {
        await using var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        vm.ComposerText = "\u200B/hotkeys";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal("/hotkeys", vm.ComposerText);
        Assert.False(vm.IsShortcutsOpen);
        Assert.Empty(vm.Session.Snapshot().Items.OfType<UserItem>());
        await vm.SendCommand.ExecuteAsync(null);
        Assert.True(vm.IsShortcutsOpen);
        Assert.Equal("", vm.ComposerText);
    }

    [AvaloniaFact]
    public async Task Restored_paste_is_cleaned_without_losing_other_composer_input()
    {
        await using var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        vm.ComposerText = "Review this";
        vm.PastedTexts.Add(new PastedTextViewModel("first\r\nsecond\u202E", vm));
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal("Review this", vm.ComposerText);
        Assert.Equal("first\nsecond", Assert.Single(vm.PastedTexts).Text);
        Assert.Empty(vm.Session.Snapshot().Items.OfType<UserItem>());
    }

    [AvaloniaFact]
    public async Task Pet_cleanup_preserves_the_small_composer_for_confirmation()
    {
        await using var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        vm.Pets.ChatText = "\u202E/hotkeys";
        vm.Pets.SendChatCommand.Execute(null);
        Assert.Equal("/hotkeys", vm.Pets.ChatText);
        Assert.False(vm.IsShortcutsOpen);
        Assert.Empty(vm.Session.Snapshot().Items.OfType<UserItem>());
    }
}
