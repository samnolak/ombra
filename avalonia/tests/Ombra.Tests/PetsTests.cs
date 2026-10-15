using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Controls;
using Ombra.App.Pets;
using Ombra.App.Pets.Oni;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>
/// The pet, ONI: what the agent does → what it shows (held a moment against flicker, reactions that end by
/// themselves), the view (frames only while it is seen and moving, the sheets shared and let go), carrying it, the
/// pet on the message box (bubble, its message box, where it hides), on the desktop, the settings round trip, and the
/// layout of every screen with a pet.
/// </summary>
public sealed class PetsTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("OMBRA_REVIEW_DIR");

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Run(double seconds, OniPet pet, double step = 1 / 60.0)
        {
            for (var t = 0.0; t < seconds - 1e-9; t += step)
            {
                Now += TimeSpan.FromSeconds(step);
                pet.Sync();
            }
        }
    }

    private static (OniPet Pet, ManualClock Clock) NewPet()
    {
        var clock = new ManualClock();
        return (new OniPet(OniParityTests.Spec, clock), clock);
    }

    // ───────────────────────────── What it shows ─────────────────────────────

    [Fact]
    public void Thinking_and_working_in_quick_turns_do_not_flicker()
    {
        var (pet, clock) = NewPet();
        var changes = new List<(DateTimeOffset At, PetMood Mood)>();
        var shown = pet.Mood;
        for (var i = 0; i < 60; i++)
        {
            clock.Now += TimeSpan.FromMilliseconds(100);
            pet.SetBase(i % 2 == 0 ? PetMood.Thinking : PetMood.Working);
            if (pet.Mood != shown) changes.Add((clock.Now, shown = pet.Mood));
        }
        // Idle → busy once, then it stays with what it showed first
        Assert.Single(changes);
        Assert.True(changes[0].Mood is PetMood.Thinking or PetMood.Working);
    }

    [Fact]
    public void A_short_idle_between_tools_is_not_shown_but_a_real_change_is()
    {
        var (pet, clock) = NewPet();
        pet.SetBase(PetMood.Working);
        clock.Run(1, pet);
        Assert.Equal(PetMood.Working, pet.Mood);
        pet.SetBase(PetMood.Idle);
        clock.Run(.3, pet);
        pet.SetBase(PetMood.Working);
        clock.Run(2, pet);
        Assert.Equal(PetMood.Working, pet.Mood);
        Assert.Equal("working", pet.Director.Cue);
        // Thinking that lasts: shown once it held for a moment
        pet.SetBase(PetMood.Thinking);
        Assert.Equal(PetMood.Working, pet.Mood);
        Assert.Equal(clock.Now + OniPet.WithinBusy, pet.NextChange());
        clock.Run(1, pet);
        Assert.Equal(PetMood.Thinking, pet.Mood);
    }

    [Fact]
    public void A_question_shows_at_once_and_a_finished_run_celebrates_then_rests()
    {
        var (pet, clock) = NewPet();
        pet.SetBase(PetMood.Thinking);
        clock.Run(1, pet);
        pet.SetBase(PetMood.Waiting);
        Assert.Equal(PetMood.Waiting, pet.Mood);
        Assert.Equal("waiting", pet.Director.Cue);
        pet.SetBase(PetMood.Thinking);
        clock.Run(2, pet);
        // The run ends: joy at once, shown for its while once the pose is reached, then idle
        pet.SetBase(PetMood.Idle);
        pet.Done();
        Assert.Equal(PetMood.Done, pet.Mood);
        var visible = 0.0;
        for (var i = 0; i < 8 * 60; i++)
        {
            clock.Run(1 / 60.0, pet);
            if (pet.Director is { State: "joy", Mode: "loop" }) visible += 1 / 60.0;
        }
        Assert.InRange(visible, 1.6, 2.5);
        Assert.Equal(PetMood.Idle, pet.Mood);
    }

    [Fact]
    public void A_failed_tool_winces_briefly_omp_stopping_stays_and_a_cancel_never_celebrates()
    {
        var (pet, clock) = NewPet();
        pet.SetBase(PetMood.Working);
        clock.Run(1, pet);
        pet.ToolFailed();
        Assert.Equal(PetMood.Error, pet.Mood);
        clock.Run(OniDirector.ToolErrorFor + .1, pet);
        Assert.Equal(PetMood.Working, pet.Mood); // a transient error: back to work
        // A cancel: back to idle, no joy, the wince gone
        pet.ToolFailed();
        pet.Aborted();
        Assert.Equal(PetMood.Idle, pet.Mood);
        Assert.Null(pet.Director.Flashing);
        // omp stopped: an error that stays, no sleeping through a crash
        pet.SetBase(PetMood.Error);
        Assert.Equal(PetMood.Error, pet.Mood);
        clock.Run(30, pet, step: .5);
        clock.Now += TimeSpan.FromMinutes(30);
        pet.Sync();
        Assert.Equal(PetMood.Error, pet.Mood);
        // A question and a persistent fault win over a run's end
        pet.Done();
        Assert.Equal(PetMood.Error, pet.Mood);
    }

    [Fact]
    public void Nothing_happening_puts_it_to_sleep_and_the_user_wakes_it()
    {
        var (pet, clock) = NewPet();
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(OniDirector.SleepAfter), pet.NextChange());
        clock.Now += TimeSpan.FromSeconds(OniDirector.SleepAfter - 1);
        pet.Sync();
        Assert.Equal(PetMood.Idle, pet.Mood);
        clock.Now += TimeSpan.FromSeconds(1);
        pet.Sync();
        Assert.Equal(PetMood.Sleeping, pet.Mood);
        Assert.Null(pet.NextChange()); // asleep: no timer at all
        pet.Typed();
        Assert.Equal(PetMood.Idle, pet.Mood);
        // Work wakes it too, at once (the busy family's settle aside)
        pet.Sleep();
        Assert.Equal(PetMood.Sleeping, pet.Mood);
        pet.SetBase(PetMood.Working);
        clock.Run(1, pet);
        Assert.Equal(PetMood.Working, pet.Mood);
    }

    [Fact]
    public void Carried_from_any_state_it_lands_back_into_what_is_happening_now()
    {
        foreach (var start in new[] { "idle", "thinking", "working", "waiting", "sleep", "error", "done" })
        {
            var (pet, clock) = NewPet();
            pet.Show(start);
            clock.Run(1, pet);
            var before = (pet.Director.State, pet.Director.Phase);
            pet.Carry(true);
            // The drawing stays put the moment it is taken; then it hangs from the pointer
            Assert.Equal(before, (pet.Director.State, pet.Director.Phase));
            Assert.Equal("dragging", pet.Director.Cue);
            // omp asks something while it is held: shown once it is put down
            pet.SetBase(PetMood.Waiting);
            Assert.Equal("dragging", pet.Director.Cue);
            clock.Run(2, pet);
            Assert.Equal("dragging", pet.Director.State);
            pet.Carry(false);
            Assert.Equal("waiting", pet.Director.Cue);
            clock.Run(3, pet);
            Assert.Equal(("waiting", "loop"), (pet.Director.State, pet.Director.Mode));
        }
    }

    [Fact]
    public void Every_mood_stays_on_screen_for_the_minimum_under_random_input()
    {
        var rnd = new Random(7);
        var (pet, clock) = NewPet();
        var shownAt = clock.Now;
        var shown = pet.Mood;
        var moods = new[] { PetMood.Idle, PetMood.Thinking, PetMood.Working };
        for (var i = 0; i < 3000; i++)
        {
            clock.Now += TimeSpan.FromMilliseconds(rnd.Next(20, 400));
            pet.SetBase(moods[rnd.Next(moods.Length)]);
            if (pet.Mood == shown) continue;
            Assert.True(clock.Now - shownAt >= OniPet.MinShow, $"{shown} lasted {(clock.Now - shownAt).TotalMilliseconds} ms");
            (shown, shownAt) = (pet.Mood, clock.Now);
        }
    }

    private static SessionSnapshot Snap(params TranscriptItem[] items) =>
        new(1, SessionPhase.Running, items, "m", 1, "s", 0, 0, null, null, null, []);

    [Fact]
    public void The_agent_state_maps_to_a_base_mood()
    {
        var user = new UserItem(1, "hi", true);
        Assert.Equal(PetMood.Idle, PetMoods.BaseMood(Snap(user), SessionPhase.Ready, false));
        Assert.Equal(PetMood.Thinking, PetMoods.BaseMood(Snap(user), SessionPhase.Running, false));
        Assert.Equal(PetMood.Thinking, PetMoods.BaseMood(Snap(user, new AssistantItem(2, "", "hmm", true, null, null)), SessionPhase.Running, false));
        Assert.Equal(PetMood.Thinking, PetMoods.BaseMood(Snap(user, new AssistantItem(2, "Writing", "", true, null, null)), SessionPhase.Running, false));
        var tool = new ToolItem(3, "t1", "bash", "{}", ToolStatus.Running, null);
        Assert.Equal(PetMood.Working, PetMoods.BaseMood(Snap(user, new AssistantItem(2, "", "", false, "toolUse", null), tool), SessionPhase.Running, false));
        Assert.Equal(PetMood.Waiting, PetMoods.BaseMood(Snap(user, tool), SessionPhase.Running, true));
        Assert.Equal(PetMood.Error, PetMoods.BaseMood(Snap(user), SessionPhase.Faulted, false));
        Assert.Equal(PetMood.Idle, PetMoods.BaseMood(Snap(user), SessionPhase.Starting, false));
    }

    // ───────────────────────────── Carrying ─────────────────────────────

    [Fact]
    public void Carried_it_hangs_swings_and_comes_down_on_the_spot_under_it_with_dust()
    {
        var (pet, _) = NewPet();
        // The window's floor is well below: it is put down where it is let go, not there
        var carry = new OniCarry(pet, new Point(300, 400), 80, new Rect(52, 88, 600, 412), reduced: false);
        carry.Grab(new Point(300, 360));
        Assert.True(pet.Director.Carried);
        // Held still, it is lifted a little above where it was
        for (var i = 0; i < 60; i++) carry.Step(1 / 60.0);
        Assert.InRange(carry.Body.Y, 400 - 80 * .22 - 1, 400 - 80 * .22 + 1);
        Assert.True(carry.GroundDy > 15);
        // A quick move to the right: the feet trail left (+lean)
        var maxLean = 0.0;
        for (var i = 1; i <= 12; i++)
        {
            carry.Move(new Point(300 + i * 15, 360));
            carry.Step(1 / 60.0);
            maxLean = Math.Max(maxLean, carry.Angle);
        }
        Assert.InRange(maxLean, .02, .18);
        for (var i = 0; i < 30; i++) carry.Step(1 / 60.0);
        var settled = false;
        carry.Settled += () => settled = true;
        carry.Release();
        var dust = false;
        for (var i = 0; i < 240 && !settled; i++)
        {
            carry.Step(1 / 60.0);
            if (!(carry.DustAge < OniCarry.DustFor)) continue;
            dust = true;
            Assert.Equal(0, carry.GroundDy, 3); // the dust and the shadow are at its feet
        }
        Assert.True(settled);
        Assert.True(dust, "no dust on landing");
        Assert.False(pet.Director.Carried);
        // Down where it was held, on the ground under the pointer
        Assert.Equal(400, carry.Body.Y, 3);
        Assert.InRange(carry.Body.X, 470, 490);
    }

    [Fact]
    public void With_reduced_motion_it_follows_exactly_and_is_put_down_at_once()
    {
        var (pet, _) = NewPet();
        var carry = new OniCarry(pet, new Point(300, 400), 80, new Rect(52, 88, 600, 312), reduced: true);
        var settled = false;
        carry.Settled += () => settled = true;
        carry.Grab(new Point(300, 360));
        carry.Move(new Point(420, 300));
        Assert.Equal(new Point(420, 340), carry.Body);
        Assert.Equal(0, carry.Angle);
        carry.Release();
        Assert.True(settled);
        Assert.False(pet.Director.Carried);
        Assert.Equal(new Point(420, 340), carry.Body);
    }

    // ───────────────────────────── The view ─────────────────────────────

    private static async Task Settle(int ms = 250)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    private static async Task Until(Func<bool> c, string what, int seconds = 20)
    {
        var d = DateTime.UtcNow.AddSeconds(seconds);
        while (!c())
        {
            if (DateTime.UtcNow > d) throw new TimeoutException("never: " + what);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task It_moves_only_while_seen_rests_when_asleep_and_lets_its_sheets_go()
    {
        var (pet, clock) = NewPet();
        var view = new OniView { Pet = pet, HeightPx = 80 };
        var host = new Border { Child = view };
        var w = new Window { Width = 300, Height = 220, Content = host };
        w.Show();
        await Until(() => view.Atlases is { IsReady: true }, "sheets decoded");
        await Settle(100);
        Assert.Equal(OniView.BoxFor(80), view.Bounds.Size);
        Assert.True(view.IsPlaying);
        host.IsVisible = false; // an ancestor hides it (settings page, pet switched off)
        await Settle(100);
        Assert.False(view.IsPlaying);
        host.IsVisible = true;
        await Settle(100);
        Assert.True(view.IsPlaying);
        w.WindowState = WindowState.Minimized; // minimised: no frames
        await Settle(100);
        Assert.False(view.IsPlaying);
        w.WindowState = WindowState.Normal;
        await Settle(100);
        Assert.True(view.IsPlaying);
        view.Reduced = true; // less motion: the still, no frames
        await Settle(50);
        Assert.False(view.IsPlaying);
        Assert.True(view.LastScene?.Still);
        view.Reduced = false;
        await Settle(50);
        Assert.True(view.IsPlaying);
        // Asleep for a while: it rests on its still; woken, it plays again
        pet.Sleep();
        clock.Run(3, pet); // the sleep entry plays out
        clock.Now += TimeSpan.FromSeconds(OniView.SleepRest + 5);
        pet.Sync();
        await Settle(100);
        Assert.False(view.IsPlaying);
        pet.Clicked();
        await Settle(50);
        Assert.True(view.IsPlaying);
        // Another size, another set of sheets; each is let go once no view holds it (sizes no other test uses)
        view.HeightPx = 200;
        await Until(() => view.Atlases is { IsReady: true, Level: 224 }, "sheets for 200");
        var first = view.Atlases!;
        Assert.True(first.Bytes > 0);
        view.HeightPx = 150;
        await Until(() => view.Atlases is { IsReady: true, Level: 160 }, "sheets for 150");
        var second = view.Atlases!;
        await Until(() => first.IsFreed, "the first sheets freed", 10);
        Assert.False(second.IsFreed);
        w.Close();
        await Settle(50);
        Assert.False(view.IsPlaying); // detached
        await Until(() => second.IsFreed, "the last sheets freed", 10);
    }

    [AvaloniaFact]
    public async Task Only_the_pets_box_takes_the_pointer_and_every_size_draws()
    {
        var (pet, _) = NewPet();
        var view = new OniView { Pet = pet, HeightPx = 64, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Margin = new Thickness(60, 60, 0, 0) };
        var w = new Window { Width = 320, Height = 260, Content = view };
        w.Show();
        await Until(() => view.Atlases is { IsReady: true }, "sheets decoded");
        foreach (var h in new[] { 64.0, 80, 96 })
        {
            view.HeightPx = h;
            await Until(() => view.Atlases is { IsReady: true } a && a.Level == OniAtlases.LevelFor(h), "sheets");
            await Settle(50);
            var box = OniView.BoxFor(h);
            Assert.Equal(box, view.Bounds.Size);
            // The middle of the box is the pet; the spirits and the sign drawn around it are not
            Assert.Same(view, w.InputHitTest(new Point(60 + box.Width / 2, 60 + box.Height / 2)));
            Assert.NotSame(view, w.InputHitTest(new Point(60 + box.Width / 2, 60 - 10)));
            using var frame = w.CaptureRenderedFrame()!;
            Assert.True(Inked(frame, new PixelRect(60, 60, (int)box.Width, (int)box.Height)) > box.Width * box.Height * .1, $"nothing drawn at {h}");
        }
        w.Close();
    }

    /// <summary>How many pixels in <paramref name="area"/> are not the window's background.</summary>
    private static int Inked(Bitmap frame, PixelRect area)
    {
        var size = frame.PixelSize;
        var buf = new byte[size.Width * size.Height * 4];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(buf, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect(size), pin.AddrOfPinnedObject(), buf.Length, size.Width * 4); }
        finally { pin.Free(); }
        var bg = (buf[0], buf[1], buf[2]);
        var n = 0;
        for (var y = area.Y; y < Math.Min(size.Height, area.Bottom); y++)
            for (var x = area.X; x < Math.Min(size.Width, area.Right); x++)
            {
                var i = (y * size.Width + x) * 4;
                if (Math.Abs(buf[i] - bg.Item1) + Math.Abs(buf[i + 1] - bg.Item2) + Math.Abs(buf[i + 2] - bg.Item3) > 24) n++;
            }
        return n;
    }

    // ───────────────────────────── On the message box ─────────────────────────────

    private static async Task<(MainWindow w, MainViewModel vm)> Open(string scenario, double width = 1180, double height = 760, ClientSettingsStore? store = null, string? pace = null)
    {
        Ombra.App.Platform.ReducedMotion.Override = false;
        var sessions = TestProcesses.TempDir("pets");
        var factory = TestProcesses.FakeFactory(scenario, sessions);
        Func<LaunchRequest, Ombra.Rpc.OmpLaunchSpec> launch = pace is null ? factory : req =>
        {
            var spec = factory(req);
            var env = new Dictionary<string, string?>(spec.Environment) { ["FAKE_OMP_PACE_MS"] = pace };
            return spec with { Environment = env };
        };
        var s = new SessionController(launch, new LaunchRequest(TestProcesses.TempDir("pets-project")));
        var vm = new MainViewModel(s, new AppArgs(), settings: store);
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase is SessionPhase.Ready or SessionPhase.Faulted, "ready");
        await Settle();
        return (w, vm);
    }

    private static async Task Close(MainWindow w, MainViewModel vm)
    {
        await vm.DisposeAsync();
        w.Close();
    }

    private static Rect Box(Visual v, Visual root) =>
        v.TransformToVisual(root) is { } m ? new Rect(v.Bounds.Size).TransformToAABB(m) : default;

    /// <summary>The pet and its bubble cover no text, field or button of the window (they are in their own band).</summary>
    private static List<string> PetCovers(MainWindow w)
    {
        var perch = w.FindControl<PetPerch>("PetPerch")!;
        var found = new List<string>();
        if (!perch.IsEffectivelyVisible) return found;
        var own = perch.GetVisualDescendants().OfType<Visual>().Where(v => v is OniView || v.Name == "PetBubble")
            .Where(v => v.IsEffectivelyVisible).Select(v => Box(v, w)).ToList();
        foreach (var c in w.GetVisualDescendants().OfType<Control>())
        {
            if (c is not (TextBlock or TextBox or Button or Icon) || !c.IsEffectivelyVisible || c.Bounds.Width < 1 || perch.IsVisualAncestorOf(c)) continue;
            if (c.FindAncestorOfType<ScrollViewer>() is { } sv && Box(sv, w).Intersect(Box(c, w)) is { Width: < 1 }) continue; // scrolled out of view
            var r = Box(c, w);
            foreach (var o in own)
                if (o.Intersect(r) is { Width: > 0.5, Height: > 0.5 } x)
                    found.Add($"{c.GetType().Name} {(c as TextBlock)?.Text ?? c.Name} under the pet ({x})");
        }
        return found;
    }

    /// <summary>Carries the pet with the pointer from <paramref name="from"/> to <paramref name="to"/> in steps, holds it there a moment, and puts it down.</summary>
    private static async Task Carry(Window w, Point from, Point to, bool waitToRest = true)
    {
        w.MouseDown(from, MouseButton.Left);
        for (var i = 1; i <= 6; i++)
        {
            w.MouseMove(from + (to - from) * (i / 6.0));
            await Settle(30);
        }
        await Settle(400); // held still: no speed left when let go
        w.MouseUp(to, MouseButton.Left);
        if (waitToRest) await Settle(1600);
    }

    [AvaloniaFact]
    public async Task Clicking_the_pet_opens_its_message_box_and_Enter_sends_to_the_conversation()
    {
        var (w, vm) = await Open("normal");
        var composer = w.FindControl<TextBox>("Composer")!;
        composer.Focus();
        await Settle(100);
        Assert.True(composer.IsFocused);
        var perch = w.FindControl<PetPerch>("PetPerch")!;
        var pet = perch.Stage.Pet;
        var chat = perch.Stage.PetChatBox;
        Assert.True(pet.IsEffectivelyVisible);
        Assert.False(chat.IsEffectivelyVisible);
        // The pet sits on the message box's top edge, at its right end
        var petBox = Box(pet, w);
        var boxBox = Box(w.FindControl<Border>("ComposerBox")!, w);
        Assert.Equal(boxBox.Top + 1, petBox.Bottom, 0.5);
        Assert.InRange(boxBox.Right - petBox.Right, 16, 24);
        var center = petBox.Center;
        var opened = 0;
        vm.Pets.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PetsViewModel.IsChatOpen) && vm.Pets.IsChatOpen) opened++;
        };
        w.MouseDown(center, MouseButton.Left);
        w.MouseUp(center, MouseButton.Left);
        await Settle(100);
        // A click: the message box opens beside the pet with the keyboard in it, and the pet says something
        Assert.Equal(1, opened);
        Assert.True(vm.Pets.IsChatOpen);
        Assert.True(chat.IsEffectivelyVisible);
        Assert.True(chat.IsFocused, "the pet's message box did not take the keyboard");
        Assert.True(vm.Pets.IsBubbleOpen);
        Assert.False(string.IsNullOrWhiteSpace(vm.Pets.BubbleText));
        Assert.True(vm.Pets.Oni.Director.Attention > .5, "the pet did not notice the click");
        var bubble = perch.Stage.PetBubble;
        Assert.True(bubble.IsEffectivelyVisible);
        Assert.True(Box(bubble, w).Right <= petBox.Left + 1, "the bubble covers the pet");
        var chatBox = Box(perch.Stage.PetChat, w);
        Assert.True(chatBox.Bottom <= petBox.Top, "the message box covers the pet");
        Assert.False(chatBox.Intersects(Box(bubble, w)), "the message box covers the bubble");
        Assert.Empty(PetCovers(w));
        // Esc closes it and gives the keyboard back; what was typed stays for the next time
        w.KeyTextInput("draft");
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Settle(100);
        Assert.False(vm.Pets.IsChatOpen);
        Assert.True(composer.IsFocused);
        Assert.Equal("draft", vm.Pets.ChatText);
        // A click again opens it; a click on the pet while it is open closes it
        w.MouseDown(center, MouseButton.Left);
        w.MouseUp(center, MouseButton.Left);
        await Settle(100);
        Assert.True(chat.IsFocused);
        w.MouseDown(center, MouseButton.Left);
        w.MouseUp(center, MouseButton.Left);
        await Settle(100);
        Assert.False(vm.Pets.IsChatOpen);
        Assert.False(vm.Pets.IsBubbleOpen);
        Assert.True(composer.IsFocused);
        // Enter sends it as the message box would, and leaves the message box's own draft alone
        vm.ComposerText = "not this";
        w.MouseDown(center, MouseButton.Left);
        w.MouseUp(center, MouseButton.Left);
        await Settle(100);
        chat.SelectAll();
        w.KeyTextInput("hello from the pet");
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => vm.Rows.OfType<UserRowViewModel>().Any(u => u.Text == "hello from the pet"), "sent from the pet");
        Assert.False(vm.Pets.IsChatOpen);
        Assert.Equal("", vm.Pets.ChatText);
        Assert.Equal("not this", vm.ComposerText);
        Assert.True(composer.IsFocused);
        await Close(w, vm);
    }

    [AvaloniaFact]
    public async Task Inside_the_window_the_pet_is_carried_anywhere_in_it_and_stays_inside_it()
    {
        var path = Path.Combine(TestProcesses.TempDir("pets-place"), "ombra.local.json");
        var (w, vm) = await Open("normal", store: new ClientSettingsStore(path));
        vm.Pets.SetRoamCommand.Execute("window");
        var perch = w.FindControl<PetPerch>("PetPerch")!;
        var pet = perch.Stage.Pet;
        var composer = w.FindControl<Border>("ComposerBox")!;
        var from = Box(pet, w).Center;
        // A press that moves a little is still a click
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(from + new Vector(3, 3));
        w.MouseUp(from + new Vector(3, 3), MouseButton.Left);
        await Settle(100);
        Assert.True(vm.Pets.IsChatOpen);
        Assert.False(vm.Pets.IsRoaming);
        vm.Pets.CloseChat();
        // Carried to the window's top left: it hangs from the pointer, is put down there, the band goes back to the conversation
        var to = new Point(200, 180);
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(from + new Vector(-10, -10));
        await Settle(60);
        Assert.Equal("dragging", vm.Pets.Oni.Director.Cue);
        Assert.False(vm.Pets.IsChatOpen);
        w.MouseUp(from + new Vector(-10, -10), MouseButton.Left);
        await Settle(1600);
        await Carry(w, Box(pet, w).Center, to);
        Assert.False(vm.Pets.IsChatOpen); // a carry is not a click
        Assert.False(vm.Pets.Oni.Director.Carried);
        Assert.Null(perch.Carry);
        Assert.True(vm.Pets.IsRoaming);
        Assert.Equal(to.X, Box(pet, w).Center.X, 3);
        Assert.Equal(to.Y, Box(pet, w).Center.Y, 3);
        Assert.Equal(6, composer.Margin.Top);
        // Past the window's edge: it stops where what is drawn around it (spirits, the sign, the dust) still fits in the
        // window's content
        await Carry(w, Box(pet, w).Center, Box(pet, w).Center + new Vector(5000, 5000));
        var content = ((Control)w.Content!).Bounds;
        var ov = OniView.OverflowFor(vm.Pets.PetHeight);
        Assert.Equal(content.Right - ov.Right, Box(pet, w).Right, 1);
        Assert.Equal(content.Bottom - ov.Bottom, Box(pet, w).Bottom, 1);
        // A smaller window keeps it inside, in the same corner
        w.Width = 800;
        w.Height = 600;
        await Settle(150);
        content = ((Control)w.Content!).Bounds;
        Assert.Equal(content.Right - ov.Right, Box(pet, w).Right, 1);
        Assert.Equal(content.Bottom - ov.Bottom, Box(pet, w).Bottom, 1);
        // The bubble follows it (on its left, room permitting)
        vm.Pets.TalkCommand.Execute(null);
        await Settle(100);
        var bubble = Box(perch.Stage.PetBubble, w);
        Assert.True(bubble.Right <= Box(pet, w).Left + 1 && bubble.Right >= Box(pet, w).Left - 4, "the bubble is not beside the pet");
        await Close(w, vm);

        // The place survives a restart
        (w, vm) = await Open("normal", width: 800, height: 600, store: new ClientSettingsStore(path));
        pet = w.FindControl<PetPerch>("PetPerch")!.Stage.Pet;
        Assert.True(vm.Pets.IsRoaming);
        content = ((Control)w.Content!).Bounds;
        Assert.Equal(content.Right - ov.Right, Box(pet, w).Right, 1);
        Assert.Equal(content.Bottom - ov.Bottom, Box(pet, w).Bottom, 1);
        // Reset position puts it back on the message box
        vm.Pets.ReturnToPerchCommand.Execute(null);
        await Settle(150);
        Assert.False(vm.Pets.IsRoaming);
        Assert.False(vm.Pets.ReturnToPerchCommand.CanExecute(null));
        var boxBox = Box(w.FindControl<Border>("ComposerBox")!, w);
        Assert.Equal(boxBox.Top + 1, Box(pet, w).Bottom, 0.5);
        await Close(w, vm);

        (w, vm) = await Open("normal", store: new ClientSettingsStore(path));
        Assert.False(vm.Pets.IsRoaming);
        await Close(w, vm);
    }

    [AvaloniaFact]
    public async Task Losing_the_pointer_mid_carry_puts_the_pet_down_without_opening_its_message_box()
    {
        var (w, vm) = await Open("normal");
        vm.Pets.SetRoamCommand.Execute("window");
        var perch = w.FindControl<PetPerch>("PetPerch")!;
        var pet = perch.Stage.Pet;
        IPointer? pointer = null;
        pet.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        var from = Box(pet, w).Center;
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(from + new Vector(-40, -60));
        await Settle(100);
        Assert.True(vm.Pets.Oni.Director.Carried);
        // The system takes the pointer away (another window, a system gesture): let go where it is
        pointer!.Capture(null);
        await Settle(1600);
        Assert.False(vm.Pets.Oni.Director.Carried);
        Assert.False(vm.Pets.IsChatOpen);
        Assert.True(vm.Pets.IsRoaming);
        // The release that follows is not a click either
        w.MouseUp(from + new Vector(-40, -60), MouseButton.Left);
        await Settle(100);
        Assert.False(vm.Pets.IsChatOpen);
        await Close(w, vm);
    }

    [AvaloniaFact]
    public async Task A_pet_put_down_close_to_its_perch_sits_on_it_again()
    {
        var (w, vm) = await Open("normal");
        vm.Pets.SetRoamCommand.Execute("window");
        var pet = w.FindControl<PetPerch>("PetPerch")!.Stage.Pet;
        vm.Pets.MoveTo(new Point(0.5, 0.5));
        await Settle(150);
        Assert.True(vm.Pets.IsRoaming);
        // Where the perch is now (the band is gone while the pet is elsewhere): its feet on the message box
        var box = Box(w.FindControl<Border>("ComposerBox")!, w);
        var size = Box(pet, w).Size;
        var perchCenter = new Point(box.Right - PetsViewModel.PetInset - size.Width / 2, box.Top + 1 - size.Height / 2);
        await Carry(w, Box(pet, w).Center, perchCenter + new Vector(6, -5));
        Assert.False(vm.Pets.IsRoaming);
        box = Box(w.FindControl<Border>("ComposerBox")!, w);
        Assert.Equal(box.Top + 1, Box(pet, w).Bottom, 0.5);
        Assert.Equal(vm.Pets.PerchHeight - 1, w.FindControl<Border>("ComposerBox")!.Margin.Top);
        await Close(w, vm);
    }

    // ───────────────────────────── On the desktop ─────────────────────────────

    /// <summary>A MacBook (menu bar and Dock taken off its work area) with a 4K monitor at 2× on its right, set higher.</summary>
    private static readonly PetScreen Laptop = new("Built-in Retina Display", new PixelRect(0, 0, 1512, 982), new PixelRect(0, 33, 1512, 882), 1, true);
    private static readonly PetScreen External = new("DELL U2720Q", new PixelRect(1512, -200, 2560, 1440), new PixelRect(1512, -175, 2560, 1415), 2);

    private sealed class FakeScreens(params PetScreen[] all) : IPetScreens
    {
        public IReadOnlyList<PetScreen> All { get; private set; } = all;
        public event EventHandler? Changed;

        /// <summary>A monitor plugged in or out.</summary>
        public void Set(params PetScreen[] all)
        {
            All = all;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Carries the desktop pet with the mouse going from <paramref name="from"/> to <paramref name="to"/> on the desktop.
    /// The window moves every frame under the mouse: the app reads where the mouse is on the desktop from the system,
    /// here the simulated one.
    /// </summary>
    private static async Task CarryOnDesktop(PetWindow pw, PixelPoint from, PixelPoint to)
    {
        var mouse = from;
        Ombra.App.Platform.SystemPointer.Override = () => mouse;
        try
        {
            pw.MouseDown(pw.PointToClient(from), MouseButton.Left);
            for (var i = 1; i <= 6; i++)
            {
                mouse = new PixelPoint(from.X + (to.X - from.X) * i / 6, from.Y + (to.Y - from.Y) * i / 6);
                pw.MouseMove(pw.PointToClient(mouse));
                await Settle(30);
            }
            await Settle(400); // held still: no speed left when let go
            pw.MouseUp(pw.PointToClient(to), MouseButton.Left);
            await Settle(1600);
        }
        finally { Ombra.App.Platform.SystemPointer.Override = () => null; }
    }

    [Fact]
    public void On_the_desktop_the_pet_stays_on_the_work_areas_and_crosses_only_where_monitors_touch()
    {
        var pet = new Size(72, 48);
        // Side by side, the right one set higher: the edge they share is x = 1512, from y = 33 to 915
        IReadOnlyList<PetScreen> both = [Laptop, External];
        Assert.Equal(new PixelPoint(1480, 400), PetDesktop.Clamp(both, new PixelPoint(1480, 400), pet)); // straddles the shared edge
        Assert.True(PetDesktop.Covered(both, new PixelRect(1480, 400, 72, 48)));
        // Past the bottom of the laptop, into its Dock: pulled back above the Dock
        Assert.Equal(new PixelPoint(700, 915 - 48), PetDesktop.Clamp(both, new PixelPoint(700, 950), pet));
        // Straddling the laptop's bottom-right corner, part of it over nothing: onto the monitor its middle is on
        Assert.Equal(new PixelPoint(1512, 900), PetDesktop.Clamp(both, new PixelPoint(1500, 900), pet));
        // Under the menu bar, off the top of the laptop: just below the menu bar
        Assert.Equal(new PixelPoint(300, 33), PetDesktop.Clamp(both, new PixelPoint(300, -40), pet));
        // On the 2× monitor the pet takes twice the desktop pixels: kept whole inside its right edge
        Assert.Equal(new PixelPoint(4072 - 144, 100), PetDesktop.Clamp(both, new PixelPoint(4060, 100), pet));
        // In a gap between two monitors that do not touch: onto the nearest one
        IReadOnlyList<PetScreen> apart = [Laptop, External with { Bounds = new PixelRect(1700, 0, 2560, 1440), WorkingArea = new PixelRect(1700, 0, 2560, 1440) }];
        Assert.Equal(new PixelPoint(1700, 300), PetDesktop.Clamp(apart, new PixelPoint(1650, 300), pet));
        Assert.Equal(new PixelPoint(1512 - 72, 300), PetDesktop.Clamp(apart, new PixelPoint(1500, 300), pet));
        // One above the other, the laptop's menu bar between their work areas: the pet goes up onto the upper one and
        // back down, never resting on the menu bar
        var upper = new PetScreen("LG UltraFine", new PixelRect(0, -1080, 1920, 1080), new PixelRect(0, -1080, 1920, 1080), 1);
        IReadOnlyList<PetScreen> stacked = [Laptop, upper];
        Assert.Equal(new PixelPoint(100, -48), PetDesktop.Clamp(stacked, new PixelPoint(100, -30), pet));
        Assert.Equal(new PixelPoint(100, 33), PetDesktop.Clamp(stacked, new PixelPoint(100, 20), pet));
        Assert.Equal(new PixelPoint(100, -600), PetDesktop.Clamp(stacked, new PixelPoint(100, -600), pet));

        // Saved relative to its monitor: the monitor moved in the arrangement, the pet moves with it
        var place = PetDesktop.Place(both, new PixelPoint(2000, 100), pet);
        Assert.Equal((External.Name, 488, 300), (place.Screen, place.X, place.Y));
        IReadOnlyList<PetScreen> moved = [Laptop, External with { Bounds = new PixelRect(-2560, 0, 2560, 1440), WorkingArea = new PixelRect(-2560, 0, 2560, 1415) }];
        Assert.Equal(new PixelPoint(-2560 + 488, 300), PetDesktop.Resolve(moved, place, pet));
        // Its monitor gone: onto the nearest one that is left
        Assert.Equal(new PixelPoint(1512 - 72, 100), PetDesktop.Resolve([Laptop], place, pet));
    }

    [AvaloniaFact]
    public async Task On_the_desktop_the_pet_goes_anywhere_on_any_monitor_and_stays_where_put_down()
    {
        var path = Path.Combine(TestProcesses.TempDir("pets-desktop"), "ombra.local.json");
        var (w, vm) = await Open("normal", store: new ClientSettingsStore(path));
        w.Position = new PixelPoint(300, 60); // the app's window covers 300…1480 × 60…820 of the laptop
        var perch = w.FindControl<PetPerch>("PetPerch")!;
        var screens = new FakeScreens(Laptop, External);
        perch.Screens = screens;
        Assert.True(vm.Pets.IsRoamDesktop); // the default
        var composer = w.FindControl<Border>("ComposerBox")!;
        var pet = perch.Stage.Pet;
        var size = PetStage.PetSize(vm.Pets);
        var (hw, hh) = ((int)(size.Width / 2), (int)(size.Height / 2));

        // Carried off the message box: at once in a window of its own; out of the app's window, it follows the pointer
        var from = Box(pet, w).Center;
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(from + new Vector(-10, -10));
        var pw = perch.DesktopWindow!;
        Assert.NotNull(pw);
        Assert.True(pw.IsVisible);
        Assert.Equal(0, perch.Stage.Opacity); // not two pets
        var to = new Point(-120, 300);
        for (var i = 1; i <= 6; i++)
        {
            w.MouseMove(from + (to - from) * (i / 6.0));
            await Settle(30);
        }
        await Settle(400);
        w.MouseUp(to, MouseButton.Left);
        await Settle(1600);
        Assert.True(vm.Pets.IsOnDesktop);
        Assert.False(vm.Pets.Oni.Director.Carried);
        Assert.False(perch.IsEffectivelyVisible);
        Assert.Equal(1, perch.Stage.Opacity);
        // Its middle under the pointer (roundings to whole desktop pixels on the way)
        var pointer = w.PointToScreen(to);
        Assert.InRange(pw.At.X + size.Width / 2 - pointer.X, -3, 3);
        Assert.InRange(pw.At.Y + size.Height / 2 - pointer.Y, -3, 3);
        Assert.True(pw.At.X + size.Width < w.Position.X, "the pet is not outside the app's window");
        // The window is the pet and what is drawn around it, so clicks around it reach the apps below; it floats, out of the taskbar
        var overflow = OniView.OverflowFor(vm.Pets.PetHeight);
        Assert.Equal(new PixelPoint(pw.At.X - (int)overflow.Left, pw.At.Y - (int)overflow.Top), pw.Position);
        Assert.Equal((size.Width + overflow.Left + overflow.Right, size.Height + overflow.Top + overflow.Bottom), (pw.Width, pw.Height));
        Assert.True(pw.Topmost);
        Assert.False(pw.ShowInTaskbar);
        Assert.Equal(WindowDecorations.None, pw.WindowDecorations);
        Assert.Equal(6, composer.Margin.Top); // the message box gives the pet's band back
        Assert.Equal(Laptop.Name, vm.Pets.DesktopPlace!.Screen);

        // Carried by itself onto the 4K monitor, in desktop pixels
        await CarryOnDesktop(pw, new PixelPoint(pw.At.X + hw, pw.At.Y + hh), new PixelPoint(2500, 400));
        Assert.InRange(pw.At.X - (2500 - hw), -3, 3);
        Assert.InRange(pw.At.Y - (400 - hh), -3, 3);
        var at = pw.At;
        Assert.Equal((External.Name, at.X - 1512, at.Y + 200), (vm.Pets.DesktopPlace!.Screen, vm.Pets.DesktopPlace.X, vm.Pets.DesktopPlace.Y));

        // Its bubble opens beside it, on the monitor's work area; the pet stays put, the window grows around it
        vm.Pets.TalkCommand.Execute(null);
        await Settle(150);
        Assert.True(pw.Stage.PetBubble.IsEffectivelyVisible);
        Assert.Equal(at, pw.At);
        var petInWindow = pw.Stage.Pet.TranslatePoint(default, pw)!.Value;
        Assert.Equal(pw.At.X, pw.Position.X + (int)Math.Round(petInWindow.X * External.Scaling));
        Assert.Equal(pw.At.Y, pw.Position.Y + (int)Math.Round(petInWindow.Y * External.Scaling));
        Assert.True(Box(pw.Stage.PetBubble, pw).Right <= Box(pw.Stage.Pet, pw).Left + 1, "the bubble covers the pet");
        if (Dir is not null)
        {
            Directory.CreateDirectory(Path.Combine(Dir, "pets"));
            using var frame = pw.CaptureRenderedFrame();
            using var file = File.Create(Path.Combine(Dir, "pets", "desktop-bubble.png"));
            frame?.Save(file, new PngBitmapEncoderOptions());
        }
        vm.Pets.TalkCommand.Execute(null);
        await Settle(150);

        // The 4K monitor unplugged: the pet comes over to the laptop; plugged back, it goes back where it was left
        screens.Set(Laptop);
        await Settle(50);
        Assert.Equal(new PixelPoint(1512 - (int)(size.Width + overflow.Right), at.Y), pw.At);
        screens.Set(Laptop, External);
        await Settle(50);
        Assert.Equal(at, pw.At);

        // Nothing pops up over the pet in its little window (a tooltip there would take the next press)
        Assert.False(ToolTip.GetIsOpen(pw.Stage.Pet));

        // Carried down past the laptop's Dock: it stops on the Dock's edge
        await CarryOnDesktop(pw, new PixelPoint(pw.At.X + hw, pw.At.Y + hh), new PixelPoint(700, 2000));
        Assert.Equal(915 - (int)(size.Height + overflow.Bottom), pw.At.Y);
        Assert.Equal(Laptop.Name, vm.Pets.DesktopPlace!.Screen);

        // It keeps moving behind other apps only while omp is busy; it takes sheets for a screen of another density
        pw.Stage.Pet.InForeground = false;
        await Settle(50);
        Assert.False(pw.Stage.Pet.IsPlaying);
        vm.Pets.Oni.Show("working");
        await Settle(50);
        Assert.True(pw.Stage.Pet.IsPlaying);
        vm.Pets.Oni.Show("idle");
        await Settle(50);
        Assert.False(pw.Stage.Pet.IsPlaying);
        var level = pw.Stage.Pet.Atlases!.Level;
        pw.SetRenderScaling(2);
        await Until(() => pw.Stage.Pet.Atlases is { IsReady: true } a && a.Level == OniAtlases.LevelFor(vm.Pets.PetHeight * 2), "sheets at 2×");
        Assert.True(pw.Stage.Pet.Atlases!.Level > level);
        pw.SetRenderScaling(1);

        // It stays out while the settings page covers the conversation
        await vm.OpenSettingsAtAsync("pets");
        await Settle();
        Assert.True(pw.IsVisible);
        vm.CloseSettingsCommand.Execute(null);
        await Settle();

        // The place survives a restart
        await Close(w, vm);
        Assert.False(pw.IsVisible);
        (w, vm) = await Open("normal", store: new ClientSettingsStore(path));
        w.Position = new PixelPoint(100, 60);
        perch = w.FindControl<PetPerch>("PetPerch")!;
        perch.Screens = new FakeScreens(Laptop, External);
        await Settle(100);
        pw = perch.DesktopWindow!;
        Assert.True(pw.IsVisible);
        Assert.Equal(915 - (int)(size.Height + OniView.OverflowFor(vm.Pets.PetHeight).Bottom), pw.At.Y);
        Assert.False(perch.IsEffectivelyVisible);

        // Put down close to its perch on the message box, it sits there again
        composer = w.FindControl<Border>("ComposerBox")!;
        var perchAt = composer.PointToScreen(new Point(composer.Bounds.Width - PetsViewModel.PetInset - size.Width, 1 - size.Height));
        await CarryOnDesktop(pw, new PixelPoint(pw.At.X + hw, pw.At.Y + hh), new PixelPoint(perchAt.X + hw + 6, perchAt.Y + hh - 5));
        Assert.False(vm.Pets.IsRoaming);
        Assert.False(pw.IsVisible);
        Assert.True(perch.IsEffectivelyVisible);
        Assert.Equal(Box(composer, w).Top + 1, Box(perch.Stage.Pet, w).Bottom, 0.5);
        Assert.Equal(vm.Pets.PerchHeight - 1, composer.Margin.Top);
        await Close(w, vm);
    }

    [AvaloniaFact]
    public async Task A_pet_left_in_the_window_by_an_older_version_moves_to_the_same_spot_on_the_desktop()
    {
        var path = Path.Combine(TestProcesses.TempDir("pets-migrate"), "ombra.local.json");
        File.WriteAllText(path, "{ \"pet\": { \"x\": 0.25, \"y\": 0.4, \"chosen\": \"cat\", \"size\": \"small\" } }");
        var (w, vm) = await Open("normal", store: new ClientSettingsStore(path));
        var perch = w.FindControl<PetPerch>("PetPerch")!;
        await Until(() => vm.Pets.IsOnDesktop, "on the desktop");
        Assert.Equal("64", vm.Pets.Size); // the pixel pets' small
        var size = PetStage.PetSize(vm.Pets);
        var ov = OniView.OverflowFor(vm.Pets.PetHeight);
        // The fraction is of the room the window leaves the pet and what is drawn around it
        var inWindow = new Point(ov.Left + 0.25 * (perch.Bounds.Width - size.Width - ov.Left - ov.Right),
            ov.Top + 0.4 * (perch.Bounds.Height - size.Height - ov.Top - ov.Bottom));
        Assert.Equal(perch.PointToScreen(inWindow), perch.DesktopWindow!.At);
        Assert.True(perch.DesktopWindow.IsVisible);
        Assert.False(perch.IsEffectivelyVisible);
        await Close(w, vm);
        var pet = JsonNode.Parse(File.ReadAllText(path))!["pet"]!.AsObject();
        Assert.Null(pet["x"]);
        Assert.Null(pet["chosen"]);
        Assert.Equal("desktop", (string?)pet["roam"]);
        Assert.NotNull(pet["desktop"]);
    }

    [AvaloniaFact]
    public async Task The_desktop_pet_stays_when_a_quit_is_answered_with_keep_working()
    {
        var path = Path.Combine(TestProcesses.TempDir("pets-quit"), "ombra.local.json");
        File.WriteAllText(path, "{ \"pet\": { \"x\": 0.25, \"y\": 0.4 } }");
        var (w, vm) = await Open("slow-stream", store: new ClientSettingsStore(path));
        var perch = w.FindControl<PetPerch>("PetPerch")!;
        await Until(() => vm.Pets.IsOnDesktop && perch.DesktopWindow is { IsVisible: true }, "on the desktop");
        var pw = perch.DesktopWindow!;
        vm.ComposerText = "write a long answer";
        vm.SendCommand.Execute(null);
        await Until(() => vm.IsRunning, "running");

        // ⌘Q: Avalonia's lifetime closes every window without an owner, the pet's too, before it shuts down
        // (ClassicDesktopStyleApplicationLifetime.DoShutdown: CloseCore(ApplicationShutdown, isProgrammatic, ignoreCancel))
        var closeCore = typeof(Window).GetMethod("CloseCore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var window in new Window[] { w, pw })
            closeCore.Invoke(window, [WindowCloseReason.ApplicationShutdown, false, false]);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("quit", vm.SessionCard?.Kind);
        Assert.True(pw.IsVisible);

        // Keep working: the pet is still out where it was
        vm.SessionCard!.Secondary!.Command.Execute(null);
        await Settle(100);
        Assert.Same(pw, perch.DesktopWindow);
        Assert.True(pw.IsVisible);
        Assert.True(vm.Pets.IsOnDesktop);

        // Quitting for real closes it with the app
        await Close(w, vm);
        Assert.False(pw.IsVisible);
        Assert.Null(perch.DesktopWindow);
    }

    [AvaloniaFact]
    public async Task The_pet_follows_a_run_without_flicker_and_says_what_omp_is_doing()
    {
        var (w, vm) = await Open("session", pace: "70");
        var seen = new List<(DateTime At, PetMood Mood)> { (DateTime.UtcNow, vm.Pets.Mood) };
        var said = new List<string>();
        vm.Pets.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PetsViewModel.Mood)) seen.Add((DateTime.UtcNow, vm.Pets.Mood));
        };
        vm.ComposerText = "change the greeting";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Pets.Mood is PetMood.Thinking or PetMood.Working, "busy", 20);
        vm.Pets.TalkCommand.Execute(null);
        said.Add(vm.Pets.BubbleText);
        await Until(() => vm.Phase == SessionPhase.Ready, "run end", 60);
        await Until(() => vm.Pets.Mood == PetMood.Done, "done", 5);
        await Until(() => vm.Pets.Mood == PetMood.Idle, "idle again", 12);
        var moods = seen.Select(s => s.Mood).ToList();
        Assert.Contains(PetMood.Error, moods); // the scenario's ruff call fails: a wince
        Assert.Contains(moods, m => m is PetMood.Working);
        Assert.Equal(PetMood.Done, moods[^2]);
        // Base moods hold for a moment; only a reaction (the wince, the run's end) may cut in sooner
        for (var i = 2; i < seen.Count; i++)
            if (seen[i].Mood is not (PetMood.Error or PetMood.Done) && seen[i - 1].Mood is not (PetMood.Error or PetMood.Done))
                Assert.True(seen[i].At - seen[i - 1].At >= TimeSpan.FromMilliseconds(650), $"{seen[i - 1].Mood} → {seen[i].Mood} after {(seen[i].At - seen[i - 1].At).TotalMilliseconds:0} ms");
        // While busy the bubble says what the activity line says
        Assert.Matches("^(Thinking|Writing|Working|Running )", said[0]);
        await Close(w, vm);
    }

    [AvaloniaFact]
    public async Task A_stopped_run_goes_back_to_idle_without_celebrating()
    {
        var (w, vm) = await Open("slow-stream");
        var moods = new List<PetMood>();
        vm.Pets.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PetsViewModel.Mood)) moods.Add(vm.Pets.Mood);
        };
        vm.ComposerText = "write a long answer";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Pets.Mood is PetMood.Thinking or PetMood.Working, "busy", 20);
        vm.AbortCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready, "stopped", 20);
        await Settle(1200);
        Assert.Equal(PetMood.Idle, vm.Pets.Mood);
        Assert.DoesNotContain(PetMood.Done, moods);
        await Close(w, vm);
    }

    [AvaloniaFact]
    public async Task The_pet_waits_with_you_when_omp_asks()
    {
        var (w, vm) = await Open("approval");
        vm.ComposerText = "run it";
        vm.SendCommand.Execute(null);
        await Until(() => vm.HasDialog, "approval");
        await Until(() => vm.Pets.Mood == PetMood.Waiting, "waiting", 5);
        vm.Pets.TalkCommand.Execute(null);
        Assert.Contains("approval", vm.Pets.BubbleText);
        Assert.Empty(PetCovers(w));
        await Close(w, vm);
    }

    [AvaloniaFact]
    public async Task The_pet_hides_when_switched_off_in_settings_and_on_the_setup_screen()
    {
        var (w, vm) = await Open("normal");
        var perch = w.FindControl<PetPerch>("PetPerch")!;
        var composer = w.FindControl<Border>("ComposerBox")!;
        var pet = perch.Stage.Pet;
        Assert.True(perch.IsEffectivelyVisible);
        await Until(() => pet.IsPlaying, "moving");
        Assert.Equal(vm.Pets.PerchHeight - 1, composer.Margin.Top);
        await vm.OpenSettingsAtAsync("pets");
        await Settle();
        Assert.False(perch.IsEffectivelyVisible);
        Assert.False(pet.IsPlaying);
        vm.Pets.ShowPet = false;
        vm.CloseSettingsCommand.Execute(null);
        await Settle();
        Assert.False(perch.IsEffectivelyVisible);
        Assert.Equal(6, composer.Margin.Top); // the band goes with the pet
        vm.Pets.ShowPet = true;
        vm.Pets.SetSizeCommand.Execute("64");
        await Settle();
        Assert.True(perch.IsEffectivelyVisible);
        Assert.Equal(OniView.BoxFor(64), pet.Bounds.Size);
        Assert.Equal(OniView.BoxFor(64).Height - 1, composer.Margin.Top);
        await Close(w, vm);

        (w, vm) = await Open("no-models");
        Assert.True(vm.ShowSetupScreen);
        Assert.False(w.FindControl<PetPerch>("PetPerch")!.IsEffectivelyVisible);
        await Close(w, vm);
    }

    [AvaloniaFact]
    public async Task A_short_window_gives_the_pets_band_back_and_a_tall_one_brings_it_back()
    {
        var (w, vm) = await Open("normal", 800, PetsViewModel.MinWindowHeight - 40);
        var perch = w.FindControl<PetPerch>("PetPerch")!;
        var composer = w.FindControl<Border>("ComposerBox")!;
        var pet = perch.Stage.Pet;
        Assert.False(vm.Pets.HasRoom);
        Assert.False(perch.IsEffectivelyVisible);
        Assert.False(pet.IsPlaying);
        Assert.Equal(6, composer.Margin.Top); // as without a pet
        w.Height = PetsViewModel.MinWindowHeight + 80;
        await Until(() => perch.IsEffectivelyVisible, "the pet back in a taller window");
        await Until(() => pet.IsPlaying, "moving again");
        Assert.Equal(vm.Pets.PerchHeight - 1, composer.Margin.Top);
        vm.Pets.TalkCommand.Execute(null);
        Assert.True(vm.Pets.IsBubbleOpen);
        w.Height = 360;
        await Until(() => !perch.IsEffectivelyVisible, "the pet gone again");
        Assert.False(vm.Pets.IsBubbleOpen);
        Assert.Equal(6, composer.Margin.Top);
        await Close(w, vm);
    }

    [AvaloniaFact]
    public async Task Pet_settings_survive_a_restart_and_keep_the_rest_of_the_file()
    {
        var path = Path.Combine(TestProcesses.TempDir("pets-settings"), "ombra.local.json");
        File.WriteAllText(path, "{ \"theme\": \"dark\", \"somethingElse\": 42 }");
        var store = new ClientSettingsStore(path);
        var (w, vm) = await Open("normal", store: store);
        var p = vm.Pets;
        Assert.True(p.ShowPet); // on by default
        Assert.Equal("80", p.Size);
        Assert.False(p.ReduceMotion); // as the system says (here: no)
        Assert.True(p.IsRoamDesktop); // the default
        p.SetSizeCommand.Execute("96");
        p.SetRoamCommand.Execute("window");
        p.ReduceMotion = true;
        p.ShowPet = false;
        await Close(w, vm);

        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("dark", (string?)json["theme"]);
        Assert.Equal(42, (int?)json["somethingElse"]);
        Assert.Equal("96", (string?)json["pet"]!["size"]);

        (w, vm) = await Open("normal", store: new ClientSettingsStore(path));
        p = vm.Pets;
        Assert.False(p.ShowPet);
        Assert.Equal("96", p.Size);
        Assert.Equal(96, p.PetHeight);
        Assert.True(p.IsRoamWindow);
        Assert.True(p.ReduceMotion);
        await Close(w, vm);
    }

    // ───────────────────────────── Layout audit ─────────────────────────────

    private sealed class Audit
    {
        public readonly System.Text.StringBuilder Report = new();
        public int Count;

        public void Take(Window w, string screen, bool petCheck = true)
        {
            using (var frame = w.CaptureRenderedFrame())
            {
                if (Dir is not null)
                {
                    Directory.CreateDirectory(Path.Combine(Dir, "pets"));
                    using var f = File.Create(Path.Combine(Dir, "pets", screen + ".png"));
                    frame?.Save(f, new PngBitmapEncoderOptions());
                }
            }
            var found = LayoutAudit.Run(w);
            Count += found.Count;
            Report.Append(LayoutAudit.Report(screen, found));
            if (!petCheck || w is not MainWindow mw) return;
            foreach (var c in PetCovers(mw))
            {
                Count++;
                Report.AppendLine($"{screen}\tpet-covers\t{c}");
            }
        }
    }

    [AvaloniaTheory]
    [InlineData("light", 1180, 760)]
    [InlineData("dark", 1180, 760)]
    [InlineData("light", 640, 600)]
    [InlineData("dark", 640, 600)]
    public async Task The_main_window_with_a_pet_in_every_mood_is_aligned(string theme, double width, double height)
    {
        MainViewModel.ApplyTheme(theme);
        var a = new Audit();
        var tag = $"{theme}-{width}";
        try
        {
            var (w, vm) = await Open("markdown", width, height);
            a.Take(w, $"{tag}-new-session");
            vm.ComposerText = "explain the change";
            vm.SendCommand.Execute(null);
            await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<ToolRowViewModel>().Any(), "run");
            // The run's joy can start just after the run reads as over: let it pass, or it takes the Idle shot
            await Until(() => vm.Pets.Mood == PetMood.Idle, "settled", 12);
            foreach (var kind in new[] { "idle", "thinking", "working", "waiting", "done", "tool-error", "error", "sleep" })
            {
                vm.Pets.Oni.Show(kind);
                await Settle(900);
                a.Take(w, $"{tag}-pose-{kind}");
            }
            vm.Pets.Oni.Show("idle");
            vm.Pets.TalkCommand.Execute(null);
            await Settle(150);
            a.Take(w, $"{tag}-bubble");
            vm.Pets.SetSizeCommand.Execute("64");
            await Settle(150);
            a.Take(w, $"{tag}-bubble-64");
            vm.Pets.SetSizeCommand.Execute("96");
            await Settle(150);
            a.Take(w, $"{tag}-bubble-96");
            await Close(w, vm);

            (w, vm) = await Open("approval", width, height);
            vm.ComposerText = "run it";
            vm.SendCommand.Execute(null);
            await Until(() => vm.HasDialog && vm.Pets.Mood == PetMood.Waiting, "waiting");
            await Settle();
            a.Take(w, $"{tag}-approval");
            vm.Pets.TalkCommand.Execute(null);
            await Settle(150);
            a.Take(w, $"{tag}-approval-bubble");
            await Close(w, vm);

            (w, vm) = await Open("todo", width, height);
            vm.ComposerText = "plan it";
            vm.SendCommand.Execute(null);
            await Until(() => vm.Phase == SessionPhase.Ready && vm.HasTodos, "todos");
            vm.IsTodoExpanded = true;
            await Settle();
            a.Take(w, $"{tag}-todos");
            await Close(w, vm);
        }
        finally
        {
            MainViewModel.ApplyTheme("light");
        }
        if (Dir is not null) File.AppendAllText(Path.Combine(Dir, "pets-audit.tsv"), a.Report.ToString());
        Assert.True(a.Count == 0, $"{a.Count} layout findings:\n{a.Report}");
    }

    [AvaloniaTheory]
    [InlineData("light", 1180, 760)]
    [InlineData("dark", 1180, 760)]
    [InlineData("light", 640, 600)]
    [InlineData("dark", 640, 600)]
    public async Task The_pets_settings_page_is_aligned(string theme, double width, double height)
    {
        MainViewModel.ApplyTheme(theme);
        var a = new Audit();
        var tag = $"{theme}-{width}";
        try
        {
            var (w, vm) = await Open("normal", width, height);
            await vm.OpenSettingsAtAsync("pets");
            await Settle(400);
            a.Take(w, $"{tag}-settings-pets");
            var page = w.FindControl<ScrollViewer>("SettingsScroll")!;
            vm.Pets.ShowPreviewCommand.Execute(vm.Pets.PreviewOptions.First(o => o.Kind == "working"));
            page.ScrollToEnd();
            await Settle(900);
            a.Take(w, $"{tag}-settings-pets-preview");
            vm.Pets.SetSizeCommand.Execute("96");
            vm.Pets.ShowPreviewCommand.Execute(vm.Pets.PreviewOptions.First(o => o.Kind == "sleep"));
            await Settle(900);
            a.Take(w, $"{tag}-settings-pets-preview-96");
            page.ScrollToHome();
            vm.Pets.ShowPet = false;
            await Settle();
            a.Take(w, $"{tag}-settings-pets-off");
            await Close(w, vm);
        }
        finally
        {
            MainViewModel.ApplyTheme("light");
        }
        if (Dir is not null) File.AppendAllText(Path.Combine(Dir, "pets-audit.tsv"), a.Report.ToString());
        Assert.True(a.Count == 0, $"{a.Count} layout findings:\n{a.Report}");
    }
}
