using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.Controls;
using OmpGui.App.Pets;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// Pets: the pixel data, the mood machine (what the agent does → what the pet shows, without flicker), the control
/// (crisp pixels, a timer only while shown), the pet on the message box (bubble, focus, where it hides), the settings
/// round trip, and the layout of every screen with a pet.
/// </summary>
public sealed class PetsTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("OMPGUI_REVIEW_DIR");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // ───────────────────────────── Sprite data ─────────────────────────────

    private static readonly MouthStyle[] Mouths = [PetArt.Smile, PetArt.SmallMouth, PetArt.Grin, PetArt.Oh, PetArt.Wavy];

    [Fact]
    public void Every_body_is_a_24_by_24_grid_of_its_palette_roles_with_a_face_on_it()
    {
        Assert.Equal(9, PetArt.Bodies.Count);
        Assert.Equal(PetArt.Bodies.Count, PetArt.Bodies.Select(b => b.Id).Distinct().Count());
        foreach (var b in PetArt.Bodies)
        {
            Assert.Equal(PetArt.BodySize, b.Rows.Length);
            foreach (var row in b.Rows)
            {
                Assert.Equal(PetArt.BodySize, row.Length);
                foreach (var ch in row) Assert.True(ch is '.' or 'h' or 'd' || b.Palette.ContainsKey(ch), $"{b.Id}: '{ch}' is not in its palette");
            }
            foreach (var role in "o123ew") Assert.True(b.Palette.ContainsKey(role), $"{b.Id} lacks role {role}");
            Assert.All(b.Palette.Values, c => Assert.Equal(0xFFu, c >> 24)); // opaque colours only
            Assert.All(b.Rows[^1], ch => Assert.Equal('.', ch)); // the bottom row is the contact shadow's
            Assert.Equal(2, b.Eyes.Length);
            foreach (var (x, y) in b.Eyes)
                for (var dy = 0; dy < 3; dy++)
                    for (var dx = 0; dx < 2; dx++)
                        Assert.True(b.Rows[y + dy][x + dx] is not ('.' or 'o'), $"{b.Id}: eye at {x},{y} is off the face");
            // Happy carets are a pixel wider each side: never touching each other
            Assert.True(b.Eyes[0].X + 4 + PetArt.Happy.LeftDx < b.Eyes[1].X + PetArt.Happy.RightDx, $"{b.Id}: happy eyes collide");
            if (b.Mouth is { } m)
                foreach (var mouth in Mouths)
                {
                    var w = mouth.Rows.Max(r => r.Length);
                    for (var y = 0; y < mouth.Rows.Length; y++)
                        for (var x = 0; x < mouth.Rows[y].Length; x++)
                            if (mouth.Rows[y][x] != '.')
                                Assert.True(b.Rows[m.Y + y][m.X - w / 2 + x] is not ('.' or 'o'), $"{b.Id}: {mouth.Id} mouth is off the face");
                }
            foreach (var p in b.Twitch)
                Assert.True(p.X >= 0 && p.Y >= 0 && p.X + p.Pixels.Length <= PetArt.BodySize && p.Y < PetArt.BodySize, $"{b.Id}: twitch off the grid");
            Assert.NotEmpty(b.Twitch);
            Assert.InRange(b.Hat.Y, 1, 12);
            Assert.InRange(b.Neck, 1, PetArt.BodySize - 5);
            Assert.NotEmpty(b.Quips);
            Assert.False(string.IsNullOrWhiteSpace(b.DefaultName));
        }
        Assert.Equal(PetArt.Coats.Count, PetArt.Coats.Select(c => c.Id).Distinct().Count());
        Assert.Equal(PetArt.Accessories.Count, PetArt.Accessories.Select(a => a.Id).Distinct().Count());
        Assert.Equal(PetArt.Sprites.Count(), PetArt.Sprites.Select(s => s.Id).Distinct().Count());
        foreach (var s in PetArt.Sprites)
            foreach (var ch in s.Rows.SelectMany(r => r)) Assert.True(ch == '.' || s.Palette.ContainsKey(ch), $"{s.Id}: role '{ch}' has no colour");
    }

    /// <summary>
    /// Good pixel art: the silhouette has a one-pixel outline. Every visible pixel on the edge of the shape (next to
    /// transparency) is the outline or an accent (a flower, an antenna, the robot's ears); only the feet row, which
    /// stands on the message box, may show toes.
    /// </summary>
    [Fact]
    public void Every_body_has_a_one_pixel_outline()
    {
        foreach (var b in PetArt.Bodies)
            for (var y = 0; y < PetArt.BodySize - 2; y++)
                for (var x = 0; x < PetArt.BodySize; x++)
                {
                    var ch = b.Rows[y][x];
                    if (ch == '.') continue;
                    bool Clear(int xx, int yy) => xx < 0 || yy < 0 || xx >= PetArt.BodySize || yy >= PetArt.BodySize || b.Rows[yy][xx] == '.';
                    if (Clear(x - 1, y) || Clear(x + 1, y) || Clear(x, y - 1) || Clear(x, y + 1))
                        Assert.True(ch is 'o' or '6' or '7', $"{b.Id}: '{ch}' at {x},{y} is on the edge without an outline");
                }
    }

    [Fact]
    public void Every_look_renders_every_mood_inside_the_canvas()
    {
        var coats = new PetCoat?[] { null, PetArt.Coats[0], PetArt.Coats[^1] };
        foreach (var body in PetArt.Bodies)
            foreach (var coat in coats)
                foreach (var accessory in PetArt.Accessories)
                    foreach (var mood in Enum.GetValues<PetMood>())
                    {
                        var f = PetFrames.Still(new PetLook(body, coat, accessory), mood);
                        Assert.Equal((PetFrames.Width, PetFrames.Height), (f.Width, f.Height));
                        Assert.Equal(PetFrames.Width * PetFrames.Height, f.Pixels.Length);
                        var ink = f.Pixels.Count(p => p >> 24 == 0xFF);
                        Assert.True(ink > 200, $"{body.Id}/{mood}: only {ink} pixels");
                        // The contact shadow: see-through, only under the pet
                        Assert.Contains(f.Pixels, p => p >> 24 is > 0 and < 0x80);
                    }
        // Every scale is whole output pixels: 2× is each pet pixel as a 2×2 square
        var look = new PetLook(PetArt.Cat);
        var one = PetFrames.Still(look, PetMood.Idle);
        var two = PetFrames.Still(look, PetMood.Idle, 2);
        Assert.Equal((72, 48), (two.Width, two.Height));
        for (var y = 0; y < two.Height; y++)
            for (var x = 0; x < two.Width; x++)
                Assert.Equal(one.Pixels[y / 2 * one.Width + x / 2], two.Pixels[y * two.Width + x]);
        // Small: 4/3 of a screen pixel each, 48×32
        Assert.Equal((48, 32), (PetFrames.Still(look, PetMood.Idle, 4 / 3.0).Width, PetFrames.Still(look, PetMood.Idle, 4 / 3.0).Height));
        // A coat changes the coat and the outline, nothing else
        var cat = PetFrames.Body(look, new PetFace(PetArt.Open));
        var mint = PetFrames.Body(new PetLook(PetArt.Cat, PetArt.Coat("mint"), PetArt.None), new PetFace(PetArt.Open));
        Assert.Equal(PetArt.Coat("mint")!.Mid, mint[10, 9]);
        Assert.Equal(PetArt.Coat("mint")!.Outline, mint[1, 9]);
        Assert.Equal(cat[10, 18], mint[10, 18]); // the belly keeps its colour
        Assert.NotEqual(cat[10, 9], mint[10, 9]);
    }

    [Fact]
    public void A_hop_stays_inside_the_canvas_and_the_shadow_stays_on_the_ground()
    {
        foreach (var body in PetArt.Bodies)
        {
            var look = new PetLook(body);
            var standing = PetFrames.Render(look, new PetPose(new PetFace(PetArt.Open)));
            var up = PetFrames.Render(look, new PetPose(new PetFace(PetArt.Open), Dy: -3, Shadow: 0.5));
            // Up in the air: the feet row is empty but for the (smaller) shadow on the bottom rows
            var feetRow = PetFrames.Height - 2;
            Assert.True(up.Pixels.Skip(feetRow * PetFrames.Width).Take(PetFrames.Width).Count(p => p >> 24 == 0xFF)
                < standing.Pixels.Skip(feetRow * PetFrames.Width).Take(PetFrames.Width).Count(p => p >> 24 == 0xFF), body.Id);
            Assert.Contains(up.Pixels.Skip((PetFrames.Height - 1) * PetFrames.Width), p => p >> 24 is > 0 and < 0x80);
            Assert.True(up.Pixels.Count(p => p >> 24 == 0xFF) > 200, body.Id);
        }
    }

    // ───────────────────────────── Life ─────────────────────────────

    private static DateTimeOffset At(double seconds) => T0 + TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Blinks_come_every_two_to_six_seconds_and_sometimes_twice()
    {
        var life = new PetLife(T0, seed: 3);
        life.Begin(PetMood.Idle, T0);
        var blinks = new List<(double At, bool Double)>();
        for (var t = 0.0; t < 300; t += 0.02)
        {
            life.Plan(At(t));
            if (blinks.Count == 0 || blinks[^1].At != life.NextBlink) blinks.Add((life.NextBlink, life.NextBlinkIsDouble));
        }
        Assert.InRange(blinks.Count, 300 / 6.5, 300 / 2.0 + 1);
        for (var i = 1; i < blinks.Count; i++)
            Assert.InRange(blinks[i].At - blinks[i - 1].At, 2.0, 6.0 + 0.45);
        Assert.Contains(blinks, b => b.Double);
        Assert.Contains(blinks, b => !b.Double);
        // A blink shuts the eyes for a moment: half, closed, half, open
        var fresh = new PetLife(T0, seed: 3);
        fresh.Begin(PetMood.Idle, T0);
        fresh.Plan(T0);
        var b0 = fresh.NextBlink;
        Assert.Equal(PetArt.Open, fresh.Sample(PetArt.Cat, At(b0 - 0.05)).Face.Eyes);
        Assert.Equal(PetArt.Half, fresh.Sample(PetArt.Cat, At(b0 + 0.02)).Face.Eyes);
        Assert.Equal(PetArt.Closed, fresh.Sample(PetArt.Cat, At(b0 + 0.08)).Face.Eyes);
        Assert.Equal(PetArt.Open, fresh.Sample(PetArt.Cat, At(b0 + 0.2)).Face.Eyes);
    }

    [Fact]
    public void Idle_actions_come_every_eight_to_twenty_seconds_and_vary()
    {
        var life = new PetLife(T0, seed: 5);
        life.Begin(PetMood.Idle, T0);
        var actions = new List<(double At, PetLife.IdleAction Kind)>();
        for (var t = 0.0; t < 600; t += 0.05)
        {
            life.Plan(At(t));
            if (actions.Count == 0 || actions[^1].At != life.NextAction) actions.Add((life.NextAction, life.NextActionKind));
        }
        Assert.InRange(actions[0].At, 0, 20);
        for (var i = 1; i < actions.Count; i++) Assert.InRange(actions[i].At - actions[i - 1].At, 8, 20 + 2);
        Assert.True(actions.Select(a => a.Kind).Distinct().Count() >= 4, "idle actions all alike");
        // A hop leaves the ground, with a smaller shadow, and lands
        var hop = new PetLife(T0, seed: 1);
        hop.Begin(PetMood.Idle, T0);
        hop.Act(PetLife.IdleAction.Hop, At(1));
        var mid = hop.Sample(PetArt.Cat, At(1.3));
        Assert.True(mid.Dy < -1, $"hop dy {mid.Dy}");
        Assert.True(mid.Shadow < 1);
        Assert.Equal(0, hop.Sample(PetArt.Cat, At(1.7)).Dy, 3);
        // A twitch changes the body's pixels (an ear, a foot)
        foreach (var b in PetArt.Bodies)
            Assert.False(PetFrames.Render(new PetLook(b), new PetPose(new PetFace(PetArt.Open))).Pixels.SequenceEqual(
                PetFrames.Render(new PetLook(b), new PetPose(new PetFace(PetArt.Open, Twitch: true))).Pixels), $"{b.Id}: a twitch changes nothing");
    }

    [Fact]
    public void Every_mood_moves_and_the_picture_changes_at_least_every_few_seconds()
    {
        foreach (var body in new[] { PetArt.Cat, PetArt.Ghost, PetArt.Robot })
            foreach (var mood in Enum.GetValues<PetMood>())
            {
                var life = new PetLife(T0, seed: 9);
                life.Begin(mood, T0);
                var keys = new HashSet<string>();
                var now = T0;
                var wakes = 0;
                while (now < At(8))
                {
                    life.Plan(now);
                    keys.Add(PetFrames.Key(life.Sample(body, now), 2));
                    var next = life.NextChange(body, now, 2);
                    Assert.True(next > now);
                    Assert.True(next - now <= TimeSpan.FromSeconds(3));
                    now = next;
                    wakes++;
                }
                Assert.True(keys.Count >= 3, $"{body.Id}/{mood}: {keys.Count} pictures in 8 s");
                // Wakes only for a change: at most 30 a second, far fewer at rest
                Assert.InRange(wakes, 3, 8 * 31);
                if (mood == PetMood.Idle) Assert.True(wakes < 8 * 12, $"idle woke {wakes} times in 8 s");
            }
    }

    [Fact]
    public void Moods_ease_into_each_other_and_long_ones_come_to_rest()
    {
        var life = new PetLife(T0, seed: 2);
        life.Begin(PetMood.Idle, T0);
        life.SetMood(PetMood.Thinking, At(1));
        // The thought cloud pops in (small first), then is whole
        PetParticle Cloud(double t) => life.Sample(PetArt.Cat, At(t)).Effects.Single(p => p.Sprite == PetArt.ThoughtCloud);
        Assert.InRange(Cloud(1.05).Scale, 0.05, 0.9);
        Assert.Equal(1, Cloud(1.6).Scale, 3);
        Assert.Equal(PetArt.LookUp, life.Sample(PetArt.Cat, At(1.6)).Face.Eyes);
        // Working: the cloud shrinks away while the gear comes in
        life.SetMood(PetMood.Working, At(3));
        var mid = life.Sample(PetArt.Cat, At(3.1));
        Assert.InRange(mid.Effects.Single(p => p.Sprite == PetArt.ThoughtCloud).Scale, 0.01, 0.99);
        Assert.DoesNotContain(life.Sample(PetArt.Cat, At(3.6)).Effects, p => p.Sprite == PetArt.ThoughtCloud);
        // Done: a happy face; Error: a wobble
        life.SetMood(PetMood.Done, At(5));
        Assert.Equal(PetArt.Happy, life.Sample(PetArt.Cat, At(5.6)).Face.Eyes);
        life.SetMood(PetMood.Error, At(7));
        Assert.Contains(Enumerable.Range(0, 10), i => Math.Abs(life.Sample(PetArt.Cat, At(7.4 + i * 0.02)).Dx) > 0.4);
        Assert.False(life.IsResting(At(20)));
        Assert.True(life.IsResting(At(7) + PetLife.RestAfter(PetMood.Error)));
        // Asleep: rests after a while (no timer), a click wakes it for a moment
        life.SetMood(PetMood.Sleeping, At(100));
        Assert.False(life.IsResting(At(110)));
        Assert.True(life.IsResting(At(100) + PetLife.RestAfter(PetMood.Sleeping)));
        Assert.Equal(TimeSpan.MaxValue, PetLife.RestAfter(PetMood.Idle));
    }

    [Fact]
    public void The_pet_looks_at_the_pointer_cheers_when_clicked_and_watches_you_type()
    {
        var life = new PetLife(T0, seed: 4);
        life.Begin(PetMood.Idle, T0);
        life.Hover(true, -10, 0, At(1));
        Assert.Equal(-1, life.Sample(PetArt.Fox, At(1.6)).Face.LookX);
        life.Hover(true, 10, -8, At(1.7));
        Assert.Equal((1, -1), (life.Sample(PetArt.Fox, At(1.8)).Face.LookX, life.Sample(PetArt.Fox, At(1.8)).Face.LookY));
        life.Hover(false, 0, 0, At(2));
        life.Pat(At(3));
        var pat = life.Sample(PetArt.Fox, At(3.3));
        Assert.Equal(PetArt.Happy, pat.Face.Eyes);
        Assert.True(pat.Dy < -1);
        Assert.Contains(pat.Effects, p => p.Sprite == PetArt.Heart);
        life.Attend(At(5));
        var typing = life.Sample(PetArt.Fox, At(5.5));
        Assert.Equal((-1, 1), (typing.Face.LookX, typing.Face.LookY));
        Assert.Equal(0, life.Sample(PetArt.Fox, At(7.5)).Face.LookY);
    }

    // ───────────────────────────── Mood machine ─────────────────────────────

    [Fact]
    public void Thinking_and_working_in_quick_turns_do_not_flicker()
    {
        var m = new PetMoodMachine(T0);
        var changes = new List<(DateTimeOffset At, PetMood Mood)>();
        for (var i = 0; i < 60; i++)
        {
            var at = T0 + TimeSpan.FromMilliseconds(100 * i);
            if (m.SetBase(i % 2 == 0 ? PetMood.Thinking : PetMood.Working, at)) changes.Add((at, m.Shown));
        }
        // Idle → busy once, then it stays with what it showed first
        Assert.Single(changes);
        Assert.True(changes[0].Mood is PetMood.Thinking or PetMood.Working);
        Assert.True(changes[0].At - T0 >= PetMoodMachine.Settle(PetMood.Thinking));
    }

    [Fact]
    public void A_short_idle_between_tools_is_not_shown_but_a_real_change_is()
    {
        var m = new PetMoodMachine(T0);
        m.SetBase(PetMood.Working, T0);
        m.Advance(T0 + TimeSpan.FromSeconds(1));
        Assert.Equal(PetMood.Working, m.Shown);
        m.SetBase(PetMood.Idle, T0 + TimeSpan.FromMilliseconds(2000));
        m.Advance(T0 + TimeSpan.FromMilliseconds(2300));
        m.SetBase(PetMood.Working, T0 + TimeSpan.FromMilliseconds(2400));
        m.Advance(T0 + TimeSpan.FromSeconds(4));
        Assert.Equal(PetMood.Working, m.Shown);
        // Thinking that lasts: shown once it held for a moment
        m.SetBase(PetMood.Thinking, T0 + TimeSpan.FromSeconds(5));
        Assert.Equal(PetMood.Working, m.Shown);
        Assert.Equal(T0 + TimeSpan.FromSeconds(5) + PetMoodMachine.WithinBusy, m.NextChange(T0 + TimeSpan.FromSeconds(5)));
        m.Advance(T0 + TimeSpan.FromSeconds(6));
        Assert.Equal(PetMood.Thinking, m.Shown);
    }

    [Fact]
    public void A_question_shows_at_once_and_a_finished_run_cheers_then_rests()
    {
        var m = new PetMoodMachine(T0);
        m.SetBase(PetMood.Thinking, T0);
        m.Advance(T0 + TimeSpan.FromSeconds(1));
        Assert.Equal(PetMood.Thinking, m.Shown);
        Assert.True(m.SetBase(PetMood.Waiting, T0 + TimeSpan.FromSeconds(2)));
        Assert.Equal(PetMood.Waiting, m.Shown);
        m.SetBase(PetMood.Thinking, T0 + TimeSpan.FromSeconds(5));
        m.Advance(T0 + TimeSpan.FromSeconds(6));
        // The run ends: done at once (for a while), then idle
        var end = T0 + TimeSpan.FromSeconds(8);
        m.SetBase(PetMood.Idle, end);
        m.Flash(PetMood.Done, PetMoodMachine.DoneFor, end);
        Assert.Equal(PetMood.Done, m.Shown);
        Assert.Equal(end + PetMoodMachine.DoneFor, m.NextChange(end));
        m.Advance(end + PetMoodMachine.DoneFor);
        Assert.Equal(PetMood.Done, m.Shown); // idle settles before it shows
        var next = m.NextChange(end + PetMoodMachine.DoneFor)!.Value;
        m.Advance(next);
        Assert.Equal(PetMood.Idle, m.Shown);
    }

    [Fact]
    public void A_failed_tool_winces_briefly_and_omp_stopping_stays_an_error()
    {
        var m = new PetMoodMachine(T0);
        m.SetBase(PetMood.Working, T0);
        m.Advance(T0 + TimeSpan.FromSeconds(1));
        var at = T0 + TimeSpan.FromSeconds(2);
        m.Flash(PetMood.Error, PetMoodMachine.OopsFor, at);
        Assert.Equal(PetMood.Error, m.Shown);
        m.Advance(at + PetMoodMachine.OopsFor + TimeSpan.FromSeconds(1));
        Assert.Equal(PetMood.Working, m.Shown);
        m.SetBase(PetMood.Error, T0 + TimeSpan.FromSeconds(10));
        m.Advance(T0 + TimeSpan.FromMinutes(30));
        Assert.Equal(PetMood.Error, m.Shown); // no sleeping through a crash
    }

    [Fact]
    public void Nothing_happening_puts_it_to_sleep_and_the_user_wakes_it()
    {
        var m = new PetMoodMachine(T0);
        Assert.Equal(T0 + PetMoodMachine.SleepAfter, m.NextChange(T0));
        m.Advance(T0 + PetMoodMachine.SleepAfter - TimeSpan.FromSeconds(1));
        Assert.Equal(PetMood.Idle, m.Shown);
        m.Advance(T0 + PetMoodMachine.SleepAfter);
        Assert.Equal(PetMood.Sleeping, m.Shown);
        Assert.Null(m.NextChange(T0 + PetMoodMachine.SleepAfter)); // asleep: no timer at all
        Assert.True(m.Poke(T0 + TimeSpan.FromMinutes(10)));
        Assert.Equal(PetMood.Idle, m.Shown);
        // Work wakes it too, at once
        m.Advance(T0 + TimeSpan.FromMinutes(20));
        Assert.Equal(PetMood.Sleeping, m.Shown);
        Assert.True(m.SetBase(PetMood.Working, T0 + TimeSpan.FromMinutes(21)));
        Assert.Equal(PetMood.Working, m.Shown);
    }

    [Fact]
    public void Every_mood_stays_on_screen_for_the_minimum_under_random_input()
    {
        var rnd = new Random(7);
        var m = new PetMoodMachine(T0);
        var now = T0;
        var shownAt = T0;
        var shown = m.Shown;
        var moods = new[] { PetMood.Idle, PetMood.Thinking, PetMood.Working, PetMood.Waiting };
        for (var i = 0; i < 3000; i++)
        {
            now += TimeSpan.FromMilliseconds(rnd.Next(20, 400));
            var r = rnd.Next(20);
            if (r == 0) m.Flash(PetMood.Done, PetMoodMachine.DoneFor, now);
            else if (r == 1) m.Flash(PetMood.Error, PetMoodMachine.OopsFor, now);
            else m.SetBase(moods[rnd.Next(moods.Length)], now);
            if (m.Shown == shown) continue;
            Assert.True(now - shownAt >= PetMoodMachine.MinShow, $"{shown} lasted {(now - shownAt).TotalMilliseconds} ms");
            (shown, shownAt) = (m.Shown, now);
        }
    }

    private static SessionSnapshot Snap(params TranscriptItem[] items) =>
        new(1, SessionPhase.Running, items, "m", 1, "s", 0, 0, null, null, null, []);

    [Fact]
    public void The_agent_state_maps_to_a_base_mood()
    {
        var user = new UserItem(1, "hi", true);
        Assert.Equal(PetMood.Idle, PetMoodMachine.BaseMood(Snap(user), SessionPhase.Ready, false));
        Assert.Equal(PetMood.Thinking, PetMoodMachine.BaseMood(Snap(user), SessionPhase.Running, false));
        Assert.Equal(PetMood.Thinking, PetMoodMachine.BaseMood(Snap(user, new AssistantItem(2, "", "hmm", true, null, null)), SessionPhase.Running, false));
        Assert.Equal(PetMood.Thinking, PetMoodMachine.BaseMood(Snap(user, new AssistantItem(2, "Writing", "", true, null, null)), SessionPhase.Running, false));
        var tool = new ToolItem(3, "t1", "bash", "{}", ToolStatus.Running, null);
        Assert.Equal(PetMood.Working, PetMoodMachine.BaseMood(Snap(user, new AssistantItem(2, "", "", false, "toolUse", null), tool), SessionPhase.Running, false));
        Assert.Equal(PetMood.Waiting, PetMoodMachine.BaseMood(Snap(user, tool), SessionPhase.Running, true));
        Assert.Equal(PetMood.Error, PetMoodMachine.BaseMood(Snap(user), SessionPhase.Faulted, false));
        Assert.Equal(PetMood.Idle, PetMoodMachine.BaseMood(Snap(user), SessionPhase.Starting, false));
    }

    // ───────────────────────────── The control ─────────────────────────────

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
    public async Task The_pet_is_drawn_in_crisp_square_pixels_inside_its_box()
    {
        var pet = new PetView { Look = new PetLook(PetArt.Cat), Mood = PetMood.Idle, PixelScale = 3, Animate = false, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Margin = new Thickness(30, 20, 0, 0) };
        var w = new Window { Width = 200, Height = 120, Background = Avalonia.Media.Brushes.White, Content = pet };
        w.Show();
        await Settle();
        Assert.Equal(new Size(PetFrames.Width * 3, PetFrames.Height * 3), pet.Bounds.Size);
        using var frame = w.CaptureRenderedFrame()!;
        var size = frame.PixelSize;
        var buf = new byte[size.Width * size.Height * 4];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(buf, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect(size), pin.AddrOfPinnedObject(), buf.Length, size.Width * 4); }
        finally { pin.Free(); }
        var expected = PetFrames.Still(new PetLook(PetArt.Cat), PetMood.Idle, 3);
        var box = new PixelRect(30, 20, expected.Width, expected.Height);
        int ink = 0, outside = 0;
        for (var y = 0; y < size.Height; y++)
            for (var x = 0; x < size.Width; x++)
            {
                var p = BitConverter.ToUInt32(buf, (y * size.Width + x) * 4) | 0xFF000000;
                if (frame.Format == Avalonia.Platform.PixelFormat.Rgba8888) p = (p & 0xFF00FF00) | ((p & 0xFF) << 16) | ((p >> 16) & 0xFF);
                var inBox = x >= box.X && x < box.Right && y >= box.Y && y < box.Bottom;
                if (!inBox) { if (p != 0xFFFFFFFF) outside++; continue; }
                // Every screen pixel is exactly its pet pixel's colour (the shadow blended on white): no smoothing
                var s = expected.Pixels[(y - box.Y) * expected.Width + (x - box.X)];
                var a = (s >> 24) / 255.0;
                for (var sh = 0; sh <= 16; sh += 8)
                    Assert.InRange((double)((p >> sh) & 0xFF), ((s >> sh) & 0xFF) * a + 255 * (1 - a) - 2, ((s >> sh) & 0xFF) * a + 255 * (1 - a) + 2);
                if (s >> 24 == 0xFF) ink++;
            }
        Assert.Equal(0, outside);
        Assert.Equal(expected.Pixels.Count(p => p >> 24 == 0xFF), ink);
        w.Close();
    }

    [AvaloniaFact]
    public async Task It_lives_only_while_shown_and_a_long_mood_comes_to_rest()
    {
        var pet = new PetView { Look = new PetLook(PetArt.Owl), Mood = PetMood.Idle };
        var host = new Border { Child = pet };
        var w = new Window { Width = 200, Height = 120, Content = host };
        w.Show();
        await Settle(100);
        Assert.True(pet.IsPlaying);
        host.IsVisible = false; // an ancestor hides it (settings page, pet switched off)
        await Settle(100);
        Assert.False(pet.IsPlaying);
        host.IsVisible = true;
        await Settle(100);
        Assert.True(pet.IsPlaying);
        w.WindowState = WindowState.Minimized; // minimised: no timer
        await Settle(100);
        Assert.False(pet.IsPlaying);
        w.WindowState = WindowState.Normal;
        await Settle(100);
        Assert.True(pet.IsPlaying);
        pet.Animate = false;
        Assert.False(pet.IsPlaying);
        pet.Animate = true;
        Assert.True(pet.IsPlaying);

        // On its own clock: idle life changes the picture, and the timer wakes only for a change
        var now = T0;
        pet.UseClock(() => now, seed: 1);
        var keys = new HashSet<string?>();
        for (var i = 0; i < 200; i++)
        {
            now += TimeSpan.FromMilliseconds(50);
            pet.Step();
            keys.Add(pet.FrameKey);
        }
        Assert.True(keys.Count >= 4, $"{keys.Count} pictures in 10 s of idle");
        // Asleep: Zzz for a while, then it rests on a still with no timer
        pet.Mood = PetMood.Sleeping;
        Assert.True(pet.IsPlaying);
        now += PetLife.RestAfter(PetMood.Sleeping) + TimeSpan.FromSeconds(1);
        pet.Step();
        Assert.False(pet.IsPlaying);
        Assert.NotNull(pet.FrameKey);
        // A new mood plays again; so does a click
        pet.Mood = PetMood.Thinking;
        Assert.True(pet.IsPlaying);
        pet.Cheer();
        Assert.True(pet.IsPlaying);
        w.Close();
        await Settle(50);
        Assert.False(pet.IsPlaying); // detached
    }

    [AvaloniaFact]
    public async Task Hovering_the_pet_turns_its_eyes_to_the_pointer()
    {
        var pet = new PetView { Look = new PetLook(PetArt.Cat), Mood = PetMood.Idle, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var w = new Window { Width = 300, Height = 120, Content = pet };
        w.Show();
        await Settle(100);
        var now = T0;
        pet.UseClock(() => now, seed: 7);
        var box = pet.Bounds;
        w.MouseMove(new Point(box.X + 2, box.Y + 20)); // far left of its face
        now += TimeSpan.FromSeconds(0.6);
        pet.Step();
        Assert.Equal(-1, pet.Life.Sample(PetArt.Cat, now).Face.LookX);
        w.MouseMove(new Point(box.Right - 2, box.Y + 20)); // and right
        Assert.Equal(1, pet.Life.Sample(PetArt.Cat, now).Face.LookX);
        w.Close();
    }

    // ───────────────────────────── On the message box ─────────────────────────────

    private static async Task<(MainWindow w, MainViewModel vm)> Open(string scenario, double width = 1180, double height = 760, ClientSettingsStore? store = null, string? pace = null)
    {
        var sessions = TestProcesses.TempDir("pets");
        var factory = TestProcesses.FakeFactory(scenario, sessions);
        Func<LaunchRequest, OmpGui.Rpc.OmpLaunchSpec> launch = pace is null ? factory : req =>
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
        var own = perch.GetVisualDescendants().OfType<Visual>().Where(v => v is PetView || v.Name == "PetBubble")
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

    [AvaloniaFact]
    public async Task Clicking_the_pet_opens_its_message_box_and_Enter_sends_to_the_conversation()
    {
        var (w, vm) = await Open("normal");
        var composer = w.FindControl<TextBox>("Composer")!;
        composer.Focus();
        await Settle(100);
        Assert.True(composer.IsFocused);
        var perch = w.FindControl<PetPerch>("PetPerch")!;
        var pet = perch.FindControl<PetView>("Pet")!;
        var chat = perch.FindControl<TextBox>("PetChatBox")!;
        Assert.True(pet.IsEffectivelyVisible);
        Assert.False(chat.IsEffectivelyVisible);
        // The pet sits on the message box's top edge, at its right end
        var petBox = Box(pet, w);
        var boxBox = Box(w.FindControl<Border>("ComposerBox")!, w);
        Assert.Equal(boxBox.Top + 1, petBox.Bottom, 0.5);
        Assert.InRange(boxBox.Right - petBox.Right, 16, 24);
        var center = petBox.Center;
        w.MouseDown(center, MouseButton.Left);
        w.MouseUp(center, MouseButton.Left);
        await Settle(100);
        // A click: the message box opens beside the pet with the keyboard in it, and the pet says something
        Assert.True(vm.Pets.IsChatOpen);
        Assert.True(chat.IsEffectivelyVisible);
        Assert.True(chat.IsFocused, "the pet's message box did not take the keyboard");
        Assert.True(vm.Pets.IsBubbleOpen);
        Assert.False(string.IsNullOrWhiteSpace(vm.Pets.BubbleText));
        var bubble = perch.FindControl<StackPanel>("PetBubble")!;
        Assert.True(bubble.IsEffectivelyVisible);
        Assert.True(Box(bubble, w).Right <= petBox.Left + 1, "the bubble covers the pet");
        var chatBox = Box(perch.FindControl<Border>("PetChat")!, w);
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
    public async Task The_pet_can_be_dragged_anywhere_in_the_window_and_stays_inside_it()
    {
        var path = Path.Combine(TestProcesses.TempDir("pets-place"), "omp-gui.local.json");
        var (w, vm) = await Open("normal", store: new ClientSettingsStore(path));
        var perch = w.FindControl<PetPerch>("PetPerch")!;
        var pet = perch.FindControl<PetView>("Pet")!;
        var composer = w.FindControl<Border>("ComposerBox")!;
        var from = Box(pet, w).Center;
        // A press that moves a little is still a click
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(from + new Vector(2, 1));
        w.MouseUp(from + new Vector(2, 1), MouseButton.Left);
        await Settle(100);
        Assert.True(vm.Pets.IsChatOpen);
        Assert.False(vm.Pets.IsRoaming);
        vm.Pets.CloseChat();
        // Dragged to the window's top left: it goes there, the message box gives the pet's band back
        var to = new Point(200, 150);
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(from + new Vector(-10, -10));
        w.MouseMove(to);
        w.MouseUp(to, MouseButton.Left);
        await Settle(100);
        Assert.False(vm.Pets.IsChatOpen);
        Assert.True(vm.Pets.IsRoaming);
        Assert.Equal(to.X, Box(pet, w).Center.X, 1);
        Assert.Equal(to.Y, Box(pet, w).Center.Y, 1);
        Assert.Equal(6, composer.Margin.Top);
        // Past the window's edge: it stops at the edge
        from = Box(pet, w).Center;
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(from + new Vector(5000, 5000));
        w.MouseUp(from + new Vector(5000, 5000), MouseButton.Left);
        await Settle(100);
        Assert.Equal(w.Bounds.Width, Box(pet, w).Right, 1);
        Assert.Equal(w.Bounds.Height, Box(pet, w).Bottom, 1);
        // A smaller window keeps it inside, in the same corner
        w.Width = 800;
        w.Height = 600;
        await Settle(150);
        Assert.Equal(w.Bounds.Width, Box(pet, w).Right, 1);
        Assert.Equal(w.Bounds.Height, Box(pet, w).Bottom, 1);
        // The bubble follows it (on its left, room permitting)
        vm.Pets.TalkCommand.Execute(null);
        await Settle(100);
        var bubble = Box(perch.FindControl<StackPanel>("PetBubble")!, w);
        Assert.True(bubble.Right <= Box(pet, w).Left + 1 && bubble.Right >= Box(pet, w).Left - 4, "the bubble is not beside the pet");
        await Close(w, vm);

        // The place survives a restart
        (w, vm) = await Open("normal", width: 800, height: 600, store: new ClientSettingsStore(path));
        pet = w.FindControl<PetPerch>("PetPerch")!.FindControl<PetView>("Pet")!;
        Assert.True(vm.Pets.IsRoaming);
        Assert.Equal(w.Bounds.Width, Box(pet, w).Right, 1);
        Assert.Equal(w.Bounds.Height, Box(pet, w).Bottom, 1);
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
    public async Task A_pet_dropped_close_to_its_perch_sits_on_it_again()
    {
        var (w, vm) = await Open("normal");
        var pet = w.FindControl<PetPerch>("PetPerch")!.FindControl<PetView>("Pet")!;
        vm.Pets.MoveTo(new Point(0.5, 0.5));
        await Settle(150);
        Assert.True(vm.Pets.IsRoaming);
        // Where the perch is now (the band is gone while the pet is elsewhere): its feet on the message box
        var box = Box(w.FindControl<Border>("ComposerBox")!, w);
        var size = Box(pet, w).Size;
        var perchCenter = new Point(box.Right - PetsViewModel.PetInset - size.Width / 2, box.Top + 1 - size.Height / 2);
        var from = Box(pet, w).Center;
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(from + new Vector(20, 20));
        w.MouseMove(perchCenter + new Vector(6, -5));
        w.MouseUp(perchCenter + new Vector(6, -5), MouseButton.Left);
        await Settle(150);
        Assert.False(vm.Pets.IsRoaming);
        box = Box(w.FindControl<Border>("ComposerBox")!, w);
        Assert.Equal(box.Top + 1, Box(pet, w).Bottom, 0.5);
        Assert.Equal(vm.Pets.PerchHeight - 1, w.FindControl<Border>("ComposerBox")!.Margin.Top);
        await Close(w, vm);
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
        await Until(() => vm.Pets.Mood == PetMood.Idle, "idle again", 10);
        var moods = seen.Select(s => s.Mood).ToList();
        Assert.Contains(PetMood.Error, moods); // the scenario's ruff call fails
        Assert.Contains(moods, m => m is PetMood.Working);
        Assert.Equal(PetMood.Done, moods[^2]);
        for (var i = 2; i < seen.Count; i++)
            Assert.True(seen[i].At - seen[i - 1].At >= TimeSpan.FromMilliseconds(650), $"{seen[i - 1].Mood} → {seen[i].Mood} after {(seen[i].At - seen[i - 1].At).TotalMilliseconds:0} ms");
        // While busy the bubble says what the activity line says
        Assert.Matches("^(Thinking|Writing|Working|Running )", said[0]);
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
        var pet = perch.FindControl<PetView>("Pet")!;
        Assert.True(perch.IsEffectivelyVisible);
        Assert.True(pet.IsPlaying);
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
        vm.Pets.Size = "small";
        await Settle();
        Assert.True(perch.IsEffectivelyVisible);
        Assert.Equal(32, pet.Bounds.Height);
        Assert.Equal(31, composer.Margin.Top);
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
        var pet = perch.FindControl<PetView>("Pet")!;
        Assert.False(vm.Pets.HasRoom);
        Assert.False(perch.IsEffectivelyVisible);
        Assert.False(pet.IsPlaying);
        Assert.Equal(6, composer.Margin.Top); // as without a pet
        w.Height = PetsViewModel.MinWindowHeight + 80;
        await Until(() => perch.IsEffectivelyVisible, "the pet back in a taller window");
        await Settle(100);
        Assert.Equal(vm.Pets.PerchHeight - 1, composer.Margin.Top);
        Assert.True(pet.IsPlaying);
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
        var path = Path.Combine(TestProcesses.TempDir("pets-settings"), "omp-gui.local.json");
        File.WriteAllText(path, "{ \"theme\": \"dark\", \"somethingElse\": 42 }");
        var store = new ClientSettingsStore(path);
        var (w, vm) = await Open("normal", store: store);
        var p = vm.Pets;
        Assert.True(p.ShowPet); // on by default
        Assert.Equal("pi", p.Chosen!.Id);
        p.Size = "small";
        p.StartCreateCommand.Execute(null);
        Assert.Equal(PetArt.Cat, p.DraftBody);
        Assert.Equal("Natural", p.CoatOptions[0].Name);
        Assert.DoesNotContain(p.CoatOptions, o => o.Name == "Ginger"); // the cat's own colour: no second swatch for it
        p.ChooseBodyCommand.Execute(p.BodyOptions.First(o => o.Body == PetArt.Owl));
        p.ChooseCoatCommand.Execute(p.CoatOptions.First(o => o.Coat?.Id == "mint"));
        p.ChooseAccessoryCommand.Execute(p.AccessoryOptions.First(o => o.Accessory.Id == "crown"));
        p.DraftName = "  Professor  ";
        p.SaveEditorCommand.Execute(null);
        Assert.Equal("Professor", p.Chosen!.Name);
        var created = p.Chosen.Id;
        // Customize a built-in pet: renamed and recoloured
        p.ChoosePetCommand.Execute(p.Gallery.First(c => c.Id == "cat"));
        p.StartCustomizeCommand.Execute(null);
        Assert.False(p.CanChangeBody);
        p.DraftName = "Tiger";
        p.ChooseCoatCommand.Execute(p.CoatOptions.First(o => o.Coat?.Id == "cocoa"));
        p.SaveEditorCommand.Execute(null);
        p.ShowPet = false;
        await Close(w, vm);

        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("dark", (string?)json["theme"]);
        Assert.Equal(42, (int?)json["somethingElse"]);
        Assert.NotNull(json["pet"]);

        (w, vm) = await Open("normal", store: new ClientSettingsStore(path));
        p = vm.Pets;
        Assert.False(p.ShowPet);
        Assert.Equal("small", p.Size);
        Assert.Equal("cat", p.Chosen!.Id);
        Assert.Equal("Tiger", p.Chosen.Name);
        Assert.Equal("cocoa", p.Chosen.Look.Coat?.Id);
        Assert.True(p.Chosen.IsChanged);
        var mine = p.Gallery.Single(c => c.Id == created);
        Assert.True(mine.IsCustom);
        Assert.Equal(("Professor", "owl", "mint", "crown"), (mine.Name, mine.Look.Body.Id, mine.Look.Coat?.Id, mine.Look.Accessory.Id));
        // Reset the cat, delete the owl
        p.StartCustomizeCommand.Execute(null);
        Assert.True(p.CanReset);
        p.ResetChosenCommand.Execute(null);
        Assert.Equal("Mochi", p.Chosen!.Name);
        p.ChoosePetCommand.Execute(mine);
        p.StartCustomizeCommand.Execute(null);
        Assert.True(p.CanDelete);
        p.DeleteChosenCommand.Execute(null);
        Assert.Equal("pi", p.Chosen!.Id);
        await Close(w, vm);

        (w, vm) = await Open("normal", store: new ClientSettingsStore(path));
        Assert.Equal(PetArt.Bodies.Count, vm.Pets.Gallery.Count);
        Assert.All(vm.Pets.Gallery, c => Assert.False(c.IsChanged || c.IsCustom));
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
            // The run's "Done" flash can start just after the run reads as over: let it pass, or it takes the Idle shot
            await Settle((int)PetMoodMachine.DoneFor.TotalMilliseconds + 300);
            await Until(() => vm.Pets.Mood == PetMood.Idle, "settled", 10);
            foreach (var mood in Enum.GetValues<PetMood>())
            {
                vm.Pets.Mood = mood;
                await Settle(150);
                a.Take(w, $"{tag}-mood-{mood}");
            }
            vm.Pets.Mood = PetMood.Idle;
            vm.Pets.TalkCommand.Execute(null);
            await Settle(150);
            a.Take(w, $"{tag}-bubble");
            vm.Pets.Size = "small";
            await Settle(150);
            a.Take(w, $"{tag}-bubble-small");
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
            vm.Pets.StartCreateCommand.Execute(null);
            vm.Pets.ChooseAccessoryCommand.Execute(vm.Pets.AccessoryOptions.First(o => o.Accessory.Id == "party-hat"));
            vm.Pets.ChoosePreviewMoodCommand.Execute(vm.Pets.MoodOptions.First(o => o.Mood == PetMood.Done));
            await Settle(400);
            a.Take(w, $"{tag}-settings-pets-create");
            page.ScrollToEnd();
            await Settle();
            a.Take(w, $"{tag}-settings-pets-create-end");
            vm.Pets.DraftName = "Sparky";
            vm.Pets.SaveEditorCommand.Execute(null);
            vm.Pets.StartCustomizeCommand.Execute(null);
            await Settle(400);
            page.ScrollToEnd();
            await Settle();
            a.Take(w, $"{tag}-settings-pets-customize-end");
            page.ScrollToHome();
            vm.Pets.ShowPet = false;
            vm.Pets.CancelEditorCommand.Execute(null);
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
