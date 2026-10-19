using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class CompositionInputTests
{
    private static async Task<(MainWindow Window, MainViewModel Vm)> OpenAsync(string scenario = "normal")
    {
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake(scenario)), new AppArgs());
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        window.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        return (window, vm);
    }

    private static async Task Until(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException(description);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static TextInputMethodClient InputClient(MainWindow window, TextBox box)
    {
        Assert.True(box.Focus());
        Assert.Same(box, window.FocusManager!.GetFocusedElement());
        // This is the platform's routed client request, not a test composition flag or presenter mutation.
        var request = new TextInputMethodClientRequestedEventArgs
        {
            RoutedEvent = InputElement.TextInputMethodClientRequestedEvent,
        };
        box.RaiseEvent(request);
        Assert.NotNull(request.Client);
        Assert.True(request.Client.SupportsPreedit);
        Assert.IsType<TextPresenter>(request.Client.TextViewVisual);
        return request.Client;
    }

    private static void Key(MainWindow window, Avalonia.Input.Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preedit_escape_never_aborts_but_escape_after_cancel_or_commit_does(bool commit)
    {
        var (window, vm) = await OpenAsync("slow-stream");
        try
        {
            vm.ComposerText = "keep working";
            await vm.SendCommand.ExecuteAsync(null);
            await Until(() => vm.IsRunning && vm.Rows.OfType<AssistantRowViewModel>().Any(), "streaming");
            var composer = window.FindControl<TextBox>("Composer")!;
            var client = InputClient(window, composer);
            client.SetPreeditText("にほん", 3);
            Key(window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
            Assert.True(vm.IsRunning);
            Assert.Equal(SessionPhase.Running, vm.Session.Snapshot().Phase);
            Assert.Equal("にほん", ((TextPresenter)client.TextViewVisual).PreeditText);
            Assert.Empty(vm.ComposerText);

            client.SetPreeditText(null);
            if (commit) window.KeyTextInput("日本");
            Key(window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
            await Until(() => vm.Phase == SessionPhase.Ready, "ordinary Escape aborts after composition ends");
            Assert.Equal(commit ? "日本" : "", vm.ComposerText);
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Preedit_enter_preserves_selection_replacement_until_commit_then_sends()
    {
        var (window, vm) = await OpenAsync();
        try
        {
            var composer = window.FindControl<TextBox>("Composer")!;
            vm.ComposerText = "before replace after";
            var client = InputClient(window, composer);
            composer.SelectionStart = 7;
            composer.SelectionEnd = 14;
            client.SetPreeditText("にほん", 3);
            var draft = composer.Text;
            var caret = composer.CaretIndex;
            Key(window, Avalonia.Input.Key.Enter, PhysicalKey.Enter);
            Assert.Equal(draft, composer.Text);
            Assert.Equal(caret, composer.CaretIndex);
            Assert.Empty(vm.Session.Snapshot().Items.OfType<UserItem>());
            Assert.Empty(vm.Session.Snapshot().Queued);

            client.SetPreeditText(null);
            window.KeyTextInput("日本");
            Assert.Equal("before 日本 after", composer.Text);
            Assert.Equal(9, composer.CaretIndex);
            Key(window, Avalonia.Input.Key.Enter, PhysicalKey.Enter);
            await Until(() => vm.Session.Snapshot().Items.OfType<UserItem>().Any(u => u.Confirmed), "committed message sent");
            Assert.Equal("before 日本 after", Assert.Single(vm.Session.Snapshot().Items.OfType<UserItem>()).Text);
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Preedit_owns_suggestion_navigation_and_escape_until_cancelled()
    {
        var (window, vm) = await OpenAsync();
        try
        {
            var composer = window.FindControl<TextBox>("Composer")!;
            vm.ComposerText = "/";
            await Until(() => vm.IsCommandMenuOpen && vm.CommandSuggestions.Count > 1, "suggestions");
            var client = InputClient(window, composer);
            composer.CaretIndex = composer.Text!.Length;
            client.SetPreeditText("に", 1);
            var selected = vm.SelectedSuggestionIndex;
            foreach (var (key, physical) in new[]
            {
                (Avalonia.Input.Key.Down, PhysicalKey.ArrowDown),
                (Avalonia.Input.Key.Up, PhysicalKey.ArrowUp),
                (Avalonia.Input.Key.Tab, PhysicalKey.Tab),
                (Avalonia.Input.Key.Enter, PhysicalKey.Enter),
                (Avalonia.Input.Key.Escape, PhysicalKey.Escape),
            })
            {
                Key(window, key, physical);
                Assert.True(vm.IsCommandMenuOpen);
                Assert.Equal(selected, vm.SelectedSuggestionIndex);
                Assert.Equal("/", vm.ComposerText);
            }
            client.SetPreeditText(null);
            Key(window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
            Assert.False(vm.IsCommandMenuOpen);
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Other_focused_editors_keep_composition_keys_and_focus_change_releases_guard()
    {
        var (window, vm) = await OpenAsync("slow-stream");
        try
        {
            vm.ComposerText = "keep working";
            await vm.SendCommand.ExecuteAsync(null);
            await Until(() => vm.IsRunning && vm.Rows.OfType<AssistantRowViewModel>().Any(), "streaming");
            vm.StartRenameCommand.Execute(null);
            var rename = window.FindControl<TextBox>("RenameBox")!;
            await Until(() => rename.IsFocused, "rename focus");
            var client = InputClient(window, rename);
            client.SetPreeditText("名前", 2);
            Key(window, Avalonia.Input.Key.Enter, PhysicalKey.Enter);
            Key(window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
            Assert.True(vm.IsRenaming);
            Assert.True(vm.IsRunning);
            client.SetPreeditText(null);
            Key(window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
            Assert.False(vm.IsRenaming);
            Assert.True(vm.IsRunning);

            var composer = window.FindControl<TextBox>("Composer")!;
            Assert.True(composer.Focus());
            Key(window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
            await Until(() => vm.Phase == SessionPhase.Ready, "Escape on newly focused composer aborts");
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Clipboard_crlf_replaces_selection_and_submission_normalizes_restored_text_and_keeps_images()
    {
        var (window, vm) = await OpenAsync();
        try
        {
            var composer = window.FindControl<TextBox>("Composer")!;
            composer.Focus();
            vm.ComposerText = "before replace after";
            composer.SelectionStart = 7;
            composer.SelectionEnd = 14;
            await window.Clipboard!.SetTextAsync("one\r\ntwo");
            window.KeyPress(Avalonia.Input.Key.V,
                (RawInputModifiers)window.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers, PhysicalKey.V, null);
            await Until(() => composer.Text == "before one\ntwo after", "clipboard replacement normalized");
            Assert.Equal(14, composer.CaretIndex);
            Assert.Equal(composer.SelectionStart, composer.SelectionEnd);

            vm.ComposerText = "draft\r\nsecond";
            vm.PastedTexts.Add(new PastedTextViewModel("restored\r\npaste", vm));
            var image = new ImageAttachment("dot.png", "image/png",
                Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jf9sAAAAASUVORK5CYII="));
            Assert.True(vm.TryAddImage(image, "test"));
            await vm.SendCommand.ExecuteAsync(null);
            await Until(() => vm.Session.Snapshot().Items.OfType<UserItem>().Any(u => u.Confirmed), "normalized message sent");
            var sent = Assert.Single(vm.Session.Snapshot().Items.OfType<UserItem>());
            Assert.Equal("draft\nsecond\n\nrestored\npaste", sent.Text);
            Assert.Equal(image.Data, Assert.Single(sent.Images).Data);
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Typing_in_the_conversation_after_the_platform_asked_it_for_an_input_client_goes_to_the_composer()
    {
        var (window, vm) = await OpenAsync();
        try
        {
            var transcript = window.FindControl<ListBox>("Transcript")!;
            Assert.True(transcript.Focus());
            // macOS asks every newly focused element for its text input client; the conversation has none to give
            var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            transcript.RaiseEvent(request);
            Assert.Null(request.Client);
            window.KeyTextInput("h");
            await Until(() => vm.ComposerText == "h", "the character went to the composer");
            Assert.Same(window.FindControl<TextBox>("Composer"), window.FocusManager!.GetFocusedElement());
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }
}
