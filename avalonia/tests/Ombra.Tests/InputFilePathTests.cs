using Avalonia.Headless.XUnit;
using Ombra.App;
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class InputFilePathTests
{
    [AvaloniaTheory]
    [InlineData("both ' and \" quotes")]
    [InlineData(@"back\slash")]
    public async Task Unrepresentable_file_path_preserves_input_without_writing_or_submitting(string folder)
    {
        if (OperatingSystem.IsWindows()) return; // These names are not valid Windows directories.
        var root = TestProcesses.TempDir("input-reference-path");
        var settings = new ClientSettingsStore(Path.Combine(root, folder, "config.json"));
        var session = new SessionController(TestProcesses.FakeFactory("normal", Path.Combine(root, "sessions")), new LaunchRequest(root));
        await using var vm = new MainViewModel(session, new AppArgs(), settings: settings);
        await session.StartAsync();
        vm.Apply(session.Snapshot());
        const string text = "keep this request";
        var paste = new string('"', 600_000);
        vm.ComposerText = text;
        vm.TryAddPastedText(paste);
        await vm.SendCommand.ExecuteAsync(null);
        Assert.True(vm.HasOversizedInput);

        await vm.SendAsFileCommand.ExecuteAsync(null);

        Assert.Equal(text, vm.ComposerText);
        Assert.Equal(paste, Assert.Single(vm.PastedTexts).Text);
        Assert.Empty(session.Snapshot().Items.OfType<UserItem>());
        Assert.False(Directory.Exists(DraftStore.PathFor(settings.Path) + ".input-files"));
        Assert.NotEmpty(vm.ComposerMessage);
        Assert.True(vm.HasOversizedInput);
    }
}
