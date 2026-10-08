using System.Globalization;

namespace OmpGui.ClientCore.Browser;

/// <summary>
/// What a person does with a pointer and a keyboard, as <see cref="INativeInput"/> steps with short natural pauses:
/// the pointer travels to a target before it presses, buttons stay down for a moment, text arrives a character at a
/// time. The pauses are small fixed ranges for the page's sake (debounce, hover menus, autocomplete), not to look
/// human to anyone. One per web view: it remembers where the pointer is.
/// </summary>
public sealed class NativeInputActions(INativeInput input, Func<TimeSpan, CancellationToken, Task>? delay = null, Random? random = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Random _random = random ?? Random.Shared;

    public INativeInput Input { get; } = input;

    /// <summary>Where the pointer is (viewport CSS px); null before the first move.</summary>
    public (double X, double Y)? Pointer { get; private set; }

    /// <summary>⌘ (macOS) or Ctrl: the modifier of select-all and the other editing shortcuts.</summary>
    public NativeModifiers ShortcutModifier { get; init; } = OperatingSystem.IsMacOS() ? NativeModifiers.Meta : NativeModifiers.Control;

    private Task PauseAsync(int fromMs, int toMs, CancellationToken ct) =>
        _delay(TimeSpan.FromMilliseconds(_random.Next(fromMs, toMs + 1)), ct);

    private Task SendAsync(NativeInputStep step, CancellationToken ct) => Input.SendAsync([step], ct);

    /// <summary>Moves the pointer to (x, y) along a short eased path from where it was (no path before the first move).</summary>
    public async Task MoveAsync(double x, double y, CancellationToken ct, NativeMouseButton held = NativeMouseButton.Left, bool buttonDown = false)
    {
        if (!Input.CanHover && !buttonDown)
        {
            Pointer = (x, y); // the press itself tells the page where the pointer is
            return;
        }
        var steps = Pointer is null ? 1 : _random.Next(4, 8);
        var (fromX, fromY) = Pointer ?? (x, y);
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var eased = 1 - (1 - t) * (1 - t);
            var px = fromX + (x - fromX) * eased;
            var py = fromY + (y - fromY) * eased;
            await SendAsync(new NativeMouseStep(NativeMouseAction.Move, px, py, held, 0, NativeModifiers.None), ct).ConfigureAwait(false);
            Pointer = (px, py);
            if (i < steps) await PauseAsync(14, 28, ct).ConfigureAwait(false);
        }
        Pointer = (x, y);
    }

    /// <summary>Pointer to (x, y), then press and release (<paramref name="count"/> 2 = double click).</summary>
    public async Task ClickAsync(double x, double y, CancellationToken ct, int count = 1, NativeModifiers modifiers = NativeModifiers.None,
        NativeMouseButton button = NativeMouseButton.Left)
    {
        await MoveAsync(x, y, ct).ConfigureAwait(false);
        await PauseAsync(50, 120, ct).ConfigureAwait(false);
        for (var n = 1; n <= Math.Max(1, count); n++)
        {
            if (n > 1) await PauseAsync(60, 110, ct).ConfigureAwait(false);
            await SendAsync(new NativeMouseStep(NativeMouseAction.Down, x, y, button, n, modifiers), ct).ConfigureAwait(false);
            await PauseAsync(40, 90, ct).ConfigureAwait(false);
            await SendAsync(new NativeMouseStep(NativeMouseAction.Up, x, y, button, n, modifiers), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Pointer over (x, y) and resting there (hover menus, tooltips). Throws when the engine cannot move the pointer.</summary>
    public async Task HoverAsync(double x, double y, CancellationToken ct)
    {
        if (!Input.CanHover) throw new NotSupportedException("This web view cannot move the pointer over the page (hover).");
        await MoveAsync(x, y, ct).ConfigureAwait(false);
    }

    /// <summary>Press at (x1, y1), travel to (x2, y2) with the button held, release.</summary>
    public async Task DragAsync(double x1, double y1, double x2, double y2, CancellationToken ct)
    {
        await MoveAsync(x1, y1, ct).ConfigureAwait(false);
        await PauseAsync(50, 100, ct).ConfigureAwait(false);
        await SendAsync(new NativeMouseStep(NativeMouseAction.Down, x1, y1, NativeMouseButton.Left, 1, NativeModifiers.None), ct).ConfigureAwait(false);
        const int Steps = 10;
        for (var i = 1; i <= Steps; i++)
        {
            await PauseAsync(16, 32, ct).ConfigureAwait(false);
            var t = (double)i / Steps;
            await SendAsync(new NativeMouseStep(NativeMouseAction.Move, x1 + (x2 - x1) * t, y1 + (y2 - y1) * t, NativeMouseButton.Left, 0, NativeModifiers.None), ct)
                .ConfigureAwait(false);
        }
        Pointer = (x2, y2);
        await PauseAsync(40, 90, ct).ConfigureAwait(false);
        await SendAsync(new NativeMouseStep(NativeMouseAction.Up, x2, y2, NativeMouseButton.Left, 1, NativeModifiers.None), ct).ConfigureAwait(false);
    }

    /// <summary>Wheel at (x, y) by (dx, dy) CSS px (positive dy scrolls down), in notches of at most 120 px.</summary>
    public async Task ScrollAsync(double x, double y, double dx, double dy, CancellationToken ct)
    {
        if (dx == 0 && dy == 0) return;
        await MoveAsync(x, y, ct).ConfigureAwait(false);
        var steps = (int)Math.Clamp(Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)) / 120), 1, 24);
        double sentX = 0, sentY = 0;
        for (var i = 1; i <= steps; i++)
        {
            if (i > 1) await PauseAsync(16, 40, ct).ConfigureAwait(false);
            // whole pixels, the rounding carried so the total is exact
            var stepX = Math.Round(dx * i / steps) - sentX;
            var stepY = Math.Round(dy * i / steps) - sentY;
            sentX += stepX;
            sentY += stepY;
            await SendAsync(new NativeWheelStep(x, y, stepX, stepY, NativeModifiers.None), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Types <paramref name="text"/> into the focused element: each character as an input method commits it (no key
    /// events, so the user's keyboard layout and input method play no part); line breaks and tabs as Enter and Tab keys.
    /// Long text goes a few characters at a time, so a page never waits more than a few seconds.
    /// </summary>
    public async Task TypeAsync(string text, CancellationToken ct)
    {
        var parts = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) parts.Add(e.GetTextElement());
        var printable = parts.Count(p => p is not ("\n" or "\r\n" or "\r" or "\t"));
        var chunk = Math.Max(1, (int)Math.Ceiling(printable / 64.0));
        var pending = "";
        var count = 0;
        var first = true;
        async Task FlushAsync()
        {
            if (pending.Length == 0) return;
            if (!first) await PauseAsync(25, 60, ct).ConfigureAwait(false);
            first = false;
            await SendAsync(new NativeTextStep(pending), ct).ConfigureAwait(false);
            pending = "";
            count = 0;
        }
        foreach (var part in parts)
        {
            if (part is "\n" or "\r\n" or "\r" or "\t")
            {
                await FlushAsync().ConfigureAwait(false);
                if (!first) await PauseAsync(25, 60, ct).ConfigureAwait(false);
                first = false;
                await PressAsync(part == "\t" ? "Tab" : "Enter", ct).ConfigureAwait(false);
                continue;
            }
            pending += part;
            if (++count >= chunk) await FlushAsync().ConfigureAwait(false);
        }
        await FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Puts <paramref name="text"/> in place of the focused field's content: select all, delete, then the text at once (as a paste would).</summary>
    public async Task ReplaceAsync(string text, CancellationToken ct)
    {
        await PressAsync("a", ct, ShortcutModifier).ConfigureAwait(false);
        await PauseAsync(30, 60, ct).ConfigureAwait(false);
        await PressAsync("Backspace", ct).ConfigureAwait(false);
        if (text.Length == 0) return;
        await PauseAsync(30, 60, ct).ConfigureAwait(false);
        await SendAsync(new NativeTextStep(text), ct).ConfigureAwait(false);
    }

    /// <summary>A key or a combination ("Enter", "Shift+Tab", "Control+a", "Meta+Shift+z", "ControlOrMeta+a"): modifiers down, the key, all up.</summary>
    public Task PressAsync(string combo, CancellationToken ct) => PressAsync(combo, ct, NativeModifiers.None);

    private async Task PressAsync(string combo, CancellationToken ct, NativeModifiers extra)
    {
        var (key, modifiers) = ParseCombo(combo, ShortcutModifier);
        modifiers |= extra;
        if (modifiers.HasFlag(NativeModifiers.Shift) && key.Length == 1 && char.IsAsciiLetterLower(key[0])) key = key.ToUpperInvariant();
        var held = NativeModifiers.None;
        foreach (var (flag, name) in ModifierKeys)
        {
            if (!modifiers.HasFlag(flag)) continue;
            held |= flag;
            await SendAsync(new NativeKeyStep(true, name, null, held), ct).ConfigureAwait(false);
            await PauseAsync(15, 35, ct).ConfigureAwait(false);
        }
        await SendAsync(new NativeKeyStep(true, key, null, held), ct).ConfigureAwait(false);
        await PauseAsync(30, 70, ct).ConfigureAwait(false);
        await SendAsync(new NativeKeyStep(false, key, null, held), ct).ConfigureAwait(false);
        for (var i = ModifierKeys.Length - 1; i >= 0; i--)
        {
            var (flag, name) = ModifierKeys[i];
            if (!held.HasFlag(flag)) continue;
            await PauseAsync(10, 25, ct).ConfigureAwait(false);
            held &= ~flag;
            await SendAsync(new NativeKeyStep(false, name, null, held), ct).ConfigureAwait(false);
        }
    }

    private static readonly (NativeModifiers Flag, string Key)[] ModifierKeys =
        [(NativeModifiers.Control, "Control"), (NativeModifiers.Alt, "Alt"), (NativeModifiers.Shift, "Shift"), (NativeModifiers.Meta, "Meta")];

    /// <summary>"Control+Shift+a" → ("a", Control | Shift): modifier names as Playwright and omp write them, key names normalised to the DOM's.</summary>
    public static (string Key, NativeModifiers Modifiers) ParseCombo(string combo, NativeModifiers shortcutModifier)
    {
        string[] parts = combo == "+" ? ["+"] : combo.EndsWith("++", StringComparison.Ordinal) ? [.. combo[..^2].Split('+'), "+"] : combo.Split('+');
        var modifiers = NativeModifiers.None;
        foreach (var part in parts[..^1])
        {
            modifiers |= part.Trim().ToLowerInvariant() switch
            {
                "shift" => NativeModifiers.Shift,
                "control" or "ctrl" => NativeModifiers.Control,
                "alt" or "option" or "opt" => NativeModifiers.Alt,
                "meta" or "cmd" or "command" or "super" => NativeModifiers.Meta,
                "controlormeta" or "mod" => shortcutModifier,
                var other => throw new ArgumentException($"\"{other}\" is not a modifier key (Shift, Control, Alt, Meta).", nameof(combo)),
            };
        }
        var key = parts[^1] is { Length: > 1 } named ? named.Trim() : parts[^1];
        key = key switch
        {
            "Space" or "Spacebar" => " ",
            "Esc" => "Escape",
            "Return" => "Enter",
            "Up" => "ArrowUp",
            "Down" => "ArrowDown",
            "Left" => "ArrowLeft",
            "Right" => "ArrowRight",
            "Del" => "Delete",
            _ => key,
        };
        if (key.Length == 0) throw new ArgumentException("No key to press.", nameof(combo));
        return (key, modifiers);
    }
}
