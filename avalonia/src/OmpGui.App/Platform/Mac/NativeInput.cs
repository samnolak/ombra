using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OmpGui.ClientCore.Browser;
using static OmpGui.App.Platform.Mac.MacRuntime;

namespace OmpGui.App.Platform.Mac;

/// <summary>
/// Input to one <c>WKWebView</c> that WebKit treats like the user's own (BROWSER_PLAN B4): <c>NSEvent</c>s handed to
/// the view's own responder methods (<c>mouseDown:</c>, <c>keyDown:</c>, <c>scrollWheel:</c>…), the path hardware
/// input takes after AppKit (<c>WebEventInputSource::UserDriven</c>), so the page gets trusted events and user
/// activation. Nothing goes through the window server, <c>-[NSWindow sendEvent:]</c> or <c>CGEventPost</c>: no
/// permission, the app's flyouts and menus never see the events, and the window is never activated or ordered front.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Coordinates: CSS px × <c>pageZoom</c> × <c>magnification</c> in the view, converted to the window
/// (<c>convertPoint:toView:nil</c>); a view without a window (pane hidden) takes its own coordinates.</item>
/// <item>Text: <c>insertText:replacementRange:</c>, as an input method commits it (any script; the user's layout and
/// input method play no part). Keys are for non-text keys and shortcuts; ⌘ shortcuts go to
/// <c>performKeyEquivalent:</c> while the view is first responder for that one call, then the previous first
/// responder (the composer) gets it back.</item>
/// <item>A key the page does not handle is re-sent by WebKit through <c>-[NSApp sendEvent:]</c> (so ⌘W would close
/// our window): every event we make carries an associated mark and a guarded <c>sendEvent:</c> drops marked key
/// events (<see cref="SuppressedKeyEvents"/>).</item>
/// <item>Hover: <c>_simulateMouseMove:</c> (WebKit SPI, macOS 13+, what Safari's WebDriver uses), only when the view
/// answers it (<see cref="CanHover"/>). <c>+[NSEvent pressedMouseButtons]</c> answers the hardware state, so while an
/// agent button is down it also reports that button (drags see <c>buttons == 1</c>), as Safari's WebDriver does.</item>
/// <item>The user wins: a step waits while the user's own clicks, keys or wheel reach this view (local event monitor),
/// until they pause for <see cref="UserQuiet"/>; after <see cref="UserWaitLimit"/> it fails.</item>
/// <item>Right-click is refused (WebKit would open its context menu, a modal menu over the app). Timestamps are
/// <c>NSProcessInfo.systemUptime</c>, the clock of real events. <c>_controlledByAutomation</c> is never set.</item>
/// </list>
/// No Avalonia types: the windowless harness compiles this folder.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class NativeInput : INativeInput, IDisposable
{
    // NSEventType
    private const ulong LeftMouseDown = 1, LeftMouseUp = 2, MouseMoved = 5, LeftMouseDragged = 6,
        KeyDown = 10, KeyUp = 11, FlagsChanged = 12, OtherMouseDown = 25, OtherMouseUp = 26, OtherMouseDragged = 27;

    // NSEventModifierFlags (the same bits as CGEventFlags)
    private const ulong ShiftFlag = 1 << 17, ControlFlag = 1 << 18, OptionFlag = 1 << 19, CommandFlag = 1 << 20;

    private const int MouseEventButtonNumber = 3; // kCGMouseEventButtonNumber
    private const int WindowUnderPointer = 91, WindowUnderPointerThatCanHandle = 92; // kCGMouseEventWindowUnderMousePointer(ThatCanHandleThisEvent)

    /// <summary>How long the user's own input to the view must have paused before the agent's next step.</summary>
    public static TimeSpan UserQuiet { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a step waits for the user to pause before it fails.</summary>
    public static TimeSpan UserWaitLimit { get; set; } = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _serial = new(1, 1);
    private nint _view;
    private ulong _heldModifiers;
    private NativeMouseButton? _held;

    /// <param name="wkWebView">The <c>WKWebView*</c>; retained until <see cref="Dispose"/>. Main thread.</param>
    public NativeInput(nint wkWebView)
    {
        Unsupported = wkWebView == 0 ? "No web view."
            : !MacObjC.RespondsTo(wkWebView, "mouseDown:") || !MacObjC.RespondsTo(wkWebView, "insertText:replacementRange:")
                ? "This web view takes no native input."
                : null;
        CanHover = wkWebView != 0 && MacObjC.RespondsTo(wkWebView, "_simulateMouseMove:");
        _view = MacObjC.Retain(wkWebView);
        if (Unsupported is null) Guard.Install(this);
    }

    public string? Unsupported { get; }

    public bool CanHover { get; }

    /// <summary>The <c>WKWebView*</c> (0 after <see cref="Dispose"/>).</summary>
    public nint View => _view;

    /// <summary>Key events of ours that WebKit re-sent to the app (the page did not handle them) and the guard dropped.</summary>
    public static int SuppressedKeyEvents => Guard.Suppressed;

    /// <summary>When the user's own input last reached this view (ticks of <see cref="Environment.TickCount64"/>; 0 = never).</summary>
    internal long LastUserInput;

    public async Task SendAsync(IReadOnlyList<NativeInputStep> steps, CancellationToken ct)
    {
        if (Unsupported is { } why) throw new NotSupportedException(why);
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        var generation = Interlocked.Increment(ref _generation);
        try
        {
            foreach (var step in steps)
            {
                ct.ThrowIfCancellationRequested();
                await WaitForUserAsync(ct).ConfigureAwait(false);
                await MacRuntime.OnMainAsync(() =>
                {
                    KeepFirstResponder(true);
                    Deliver(step);
                }).ConfigureAwait(false);
            }
            // Done when the page has handled them, so a script run next sees their effects (a dialog a click opened)
            if (steps.Any(s => s is NativeWheelStep or NativeMouseStep { Action: not NativeMouseAction.Move }))
                await ProcessedAsync("_doAfterProcessingAllPendingMouseEvents:", ct).ConfigureAwait(false);
            if (steps.Any(s => s is NativeKeyStep))
                await ProcessedAsync("_doAfterProcessingAllPendingKeyEvents:", ct).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
            _ = ReleaseFirstResponderLaterAsync(generation);
        }
    }

    /// <summary>How long a batch waits for WebKit to say the page handled its events (a page showing a dialog answers late).</summary>
    private static readonly TimeSpan ProcessedWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Waits until WebKit has handled the events it queued (<paramref name="selector"/>, SPI from WebKit's testing
    /// interface, guarded), at most <see cref="ProcessedWait"/>.
    /// </summary>
    private async Task ProcessedAsync(string selector, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = await MacRuntime.OnMainAsync(() =>
        {
            var view = _view;
            if (view == 0 || !MacObjC.RespondsTo(view, selector)) return false;
            var block = MacRuntime.VoidBlock(() => done.TrySetResult());
            try
            {
                MacObjC.SendVoid(view, selector, block);
            }
            finally
            {
                MacBlocks.Release(block);
            }
            return true;
        }).ConfigureAwait(false);
        if (asked) await Task.WhenAny(done.Task, Task.Delay(ProcessedWait, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>How long after the agent's last step WebKit still may not take first responder (its request comes over IPC, late).</summary>
    private static readonly TimeSpan FirstResponderTail = TimeSpan.FromSeconds(1.5);

    private long _generation;

    /// <summary>
    /// While the agent's input is in flight WebKit may not make the web view first responder (it asks to when a click
    /// focuses a text field: <c>WebChromeClient::makeFirstResponder</c>), so the composer keeps the keyboard. The user's
    /// own clicks still focus the page: AppKit does that itself. SPI, guarded; without it the page may take the keyboard.
    /// </summary>
    private unsafe void KeepFirstResponder(bool keep)
    {
        var view = _view;
        if (view != 0 && MacObjC.RespondsTo(view, "_setShouldSuppressFirstResponderChanges:"))
            ((delegate* unmanaged<nint, nint, byte, void>)MacObjC.MsgSend)(view, MacObjC.Sel("_setShouldSuppressFirstResponderChanges:"), keep ? (byte)1 : (byte)0);
    }

    private async Task ReleaseFirstResponderLaterAsync(long generation)
    {
        await Task.Delay(FirstResponderTail).ConfigureAwait(false);
        if (Interlocked.Read(ref _generation) == generation) MacObjC.OnMainThread(() => KeepFirstResponder(false));
    }

    private async Task WaitForUserAsync(CancellationToken ct)
    {
        var started = Environment.TickCount64;
        while (true)
        {
            var last = Interlocked.Read(ref LastUserInput);
            var now = Environment.TickCount64;
            var quiet = TimeSpan.FromMilliseconds(now - last);
            if (last == 0 || quiet >= UserQuiet) return;
            if (TimeSpan.FromMilliseconds(now - started) > UserWaitLimit)
                throw new TimeoutException("The user is using this page in the preview: try again when they stop.");
            await Task.Delay(UserQuiet - quiet + TimeSpan.FromMilliseconds(20), ct).ConfigureAwait(false);
        }
    }

    private void Deliver(NativeInputStep step)
    {
        var view = _view;
        if (view == 0) throw new NotSupportedException("The page is gone.");
        var pool = MacObjC.PoolPush();
        try
        {
            switch (step)
            {
                case NativeMouseStep m:
                    Mouse(view, m);
                    break;
                case NativeWheelStep w:
                    Wheel(view, w);
                    break;
                case NativeKeyStep k:
                    Key(view, k);
                    break;
                case NativeTextStep t:
                    if (t.Text.Length > 0) InsertText(view, t.Text);
                    break;
                default:
                    throw new NotSupportedException("Unknown input step " + step.GetType().Name + ".");
            }
        }
        finally
        {
            MacObjC.PoolPop(pool);
        }
    }

    // ── mouse ──

    private void Mouse(nint view, NativeMouseStep m)
    {
        if (m.Button == NativeMouseButton.Right && m.Action != NativeMouseAction.Move)
            throw new NotSupportedException("A right click would open the web view's context menu over the app; the OMP GUI preview does not send it.");
        var point = WindowPoint(view, m.X, m.Y);
        var flags = Flags(m.Modifiers) | _heldModifiers;
        var clicks = Math.Max(1, m.Clicks);
        switch (m.Action)
        {
            case NativeMouseAction.Move when _held is { } held:
            {
                var ev = MouseEvent(view, held == NativeMouseButton.Left ? LeftMouseDragged : OtherMouseDragged, point, flags, 0, 1, held);
                MacObjC.SendVoid(view, held == NativeMouseButton.Left ? "mouseDragged:" : "otherMouseDragged:", ev);
                break;
            }
            case NativeMouseAction.Move:
            {
                if (!CanHover) throw new NotSupportedException("This WebKit has no way to move the pointer over the page (hover).");
                MacObjC.SendVoid(view, "_simulateMouseMove:", MouseEvent(view, MouseMoved, point, flags, 0, 0, NativeMouseButton.Left));
                break;
            }
            case NativeMouseAction.Down:
            {
                _held = m.Button;
                Guard.Press(ButtonMask(m.Button));
                var ev = MouseEvent(view, m.Button == NativeMouseButton.Left ? LeftMouseDown : OtherMouseDown, point, flags, clicks, 1, m.Button);
                MacObjC.SendVoid(view, m.Button == NativeMouseButton.Left ? "mouseDown:" : "otherMouseDown:", ev);
                break;
            }
            case NativeMouseAction.Up:
            {
                var ev = MouseEvent(view, m.Button == NativeMouseButton.Left ? LeftMouseUp : OtherMouseUp, point, flags, clicks, 0, m.Button);
                try
                {
                    MacObjC.SendVoid(view, m.Button == NativeMouseButton.Left ? "mouseUp:" : "otherMouseUp:", ev);
                }
                finally
                {
                    if (_held == m.Button) _held = null;
                    Guard.Lift(ButtonMask(m.Button));
                }
                break;
            }
        }
    }

    private static ulong ButtonMask(NativeMouseButton button) => button switch
    {
        NativeMouseButton.Left => 1,
        NativeMouseButton.Right => 2,
        _ => 4,
    };

    private unsafe static nint MouseEvent(nint view, ulong type, CGPoint p, ulong flags, long clickCount, float pressure, NativeMouseButton button)
    {
        var window = MacObjC.Send(view, "window");
        var windowNumber = window == 0 ? 0 : MacObjC.SendLong(window, "windowNumber");
        var ev = ((delegate* unmanaged<nint, nint, ulong, CGPoint, ulong, double, long, nint, long, long, float, nint>)MacObjC.MsgSend)(
            MacObjC.Cls("NSEvent"), MacObjC.Sel("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
            type, p, flags, Uptime(), windowNumber, 0, 0, clickCount, pressure);
        if (ev == 0) throw new InvalidOperationException("AppKit made no mouse event.");
        if (button == NativeMouseButton.Middle && type is OtherMouseDown or OtherMouseUp or OtherMouseDragged)
        {
            // NSEvent's factory has no button number: set it on the CGEvent (as Safari's WebDriver does)
            var cg = MacObjC.Send(ev, "CGEvent"); // owned by the event
            CGEventSetIntegerValueField(cg, MouseEventButtonNumber, 2);
            ev = MacObjC.Send(MacObjC.Cls("NSEvent"), "eventWithCGEvent:", cg);
        }
        Guard.Mark(ev);
        return ev;
    }

    // ── wheel ──

    private unsafe void Wheel(nint view, NativeWheelStep w)
    {
        var point = WindowPoint(view, w.X, w.Y);
        var window = MacObjC.Send(view, "window");
        // WebKit wheel deltas: positive is up/left; the DOM's positive deltaY scrolls down
        var cg = CGEventCreateScrollWheelEvent2(0, 0 /* kCGScrollEventUnitPixel */, 2, Clamp(-w.DeltaY), Clamp(-w.DeltaX), 0);
        if (cg == 0) throw new InvalidOperationException("Core Graphics made no wheel event.");
        try
        {
            var screenHeight = PrimaryScreenHeight();
            CGPoint location;
            if (window != 0)
            {
                var screen = ((delegate* unmanaged<nint, nint, CGPoint, CGPoint>)MacObjC.MsgSend)(window, MacObjC.Sel("convertPointToScreen:"), point);
                location = new CGPoint(screen.X, screenHeight - screen.Y);
                var number = MacObjC.SendLong(window, "windowNumber");
                CGEventSetIntegerValueField(cg, WindowUnderPointer, number);
                CGEventSetIntegerValueField(cg, WindowUnderPointerThatCanHandle, number);
            }
            else location = new CGPoint(point.X, screenHeight - point.Y); // no window: the event's location is the view's root coordinates
            CGEventSetLocation(cg, location);
            CGEventSetFlags(cg, Flags(w.Modifiers) | _heldModifiers);
            var ev = MacObjC.Send(MacObjC.Cls("NSEvent"), "eventWithCGEvent:", cg);
            if (ev == 0) throw new InvalidOperationException("AppKit made no wheel event.");
            // Without it AppKit takes the location as on screen (rdar 17180591, as Safari's WebDriver notes)
            if (window != 0 && MacObjC.RespondsTo(ev, "_eventRelativeToWindow:"))
                ev = MacObjC.Send(ev, "_eventRelativeToWindow:", window) is var relative and not 0 ? relative : ev;
            Guard.Mark(ev);
            MacObjC.SendVoid(view, "scrollWheel:", ev);
        }
        finally
        {
            CFRelease(cg);
        }
    }

    private static int Clamp(double v) => (int)Math.Clamp(Math.Round(v), -100_000, 100_000);

    private unsafe static double PrimaryScreenHeight()
    {
        var screens = MacObjC.Send(MacObjC.Cls("NSScreen"), "screens");
        var first = screens == 0 ? 0 : MacObjC.Send(screens, "firstObject");
        if (first == 0) return 0;
        return ((delegate* unmanaged<nint, nint, CGRect>)MacObjC.MsgSend)(first, MacObjC.Sel("frame")).Height;
    }

    // ── keys and text ──

    private void Key(nint view, NativeKeyStep k)
    {
        var flags = Flags(k.Modifiers) | _heldModifiers;
        if (ModifierKey(k.Key, k.Code) is { } modifier)
        {
            _heldModifiers = k.Down ? _heldModifiers | modifier.Flag : _heldModifiers & ~modifier.Flag;
            var ev = KeyEvent(view, FlagsChanged, _heldModifiers | Flags(k.Modifiers) & (k.Down ? ulong.MaxValue : ~modifier.Flag), "", "", modifier.KeyCode);
            MacObjC.SendVoid(view, "flagsChanged:", ev);
            return;
        }
        var (keyCode, characters, unmodified, shifted) = Describe(k.Key);
        if (shifted) flags |= ShiftFlag;
        if ((flags & ControlFlag) != 0 && unmodified.Length == 1 && char.IsAsciiLetterLower(unmodified[0]))
            characters = ((char)(unmodified[0] - 'a' + 1)).ToString(); // what the keyboard gives for ⌃ + letter
        if ((flags & CommandFlag) != 0)
        {
            // A physical keyboard sends no key-up for ⌘ combinations
            if (!k.Down) return;
            // keyDown:, not performKeyEquivalent: (that one only reaches the page while the web view is first
            // responder, and taking first responder from the composer and giving it back clears the page's selection).
            // A combination the page leaves alone comes back from WebKit to the app: the guard does what the app's
            // Edit menu would do with it, on this view only.
            var ev = KeyEvent(view, KeyDown, flags, characters, unmodified, keyCode);
            Guard.ExpectEquivalent(ev, this, EditCommand(unmodified, flags));
            MacObjC.SendVoid(view, "keyDown:", ev);
            return;
        }
        var e = KeyEvent(view, k.Down ? KeyDown : KeyUp, flags, characters, unmodified, keyCode);
        MacObjC.SendVoid(view, k.Down ? "keyDown:" : "keyUp:", e);
    }

    private unsafe static nint KeyEvent(nint view, ulong type, ulong flags, string characters, string unmodified, ushort keyCode)
    {
        var window = MacObjC.Send(view, "window");
        var windowNumber = window == 0 ? 0 : MacObjC.SendLong(window, "windowNumber");
        using var chars = new MacRuntime.NSStr(characters);
        using var plain = new MacRuntime.NSStr(unmodified);
        var ev = ((delegate* unmanaged<nint, nint, ulong, CGPoint, ulong, double, long, nint, nint, nint, byte, ushort, nint>)MacObjC.MsgSend)(
            MacObjC.Cls("NSEvent"), MacObjC.Sel("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:"),
            type, default, flags, Uptime(), windowNumber, 0, chars.Handle, plain.Handle, 0, keyCode);
        if (ev == 0) throw new InvalidOperationException("AppKit made no key event.");
        Guard.Mark(ev);
        return ev;
    }

    private unsafe static void InsertText(nint view, string text)
    {
        using var s = new MacRuntime.NSStr(text);
        ((delegate* unmanaged<nint, nint, nint, NSRange, void>)MacObjC.MsgSend)(
            view, MacObjC.Sel("insertText:replacementRange:"), s.Handle, new NSRange(unchecked((nuint)long.MaxValue), 0)); // NSNotFound: at the selection
    }

    /// <summary>
    /// The Edit-menu action of a ⌘ combination (select all, copy, cut, paste, undo, redo); null for the others, which
    /// only the page may act on.
    /// </summary>
    internal static string? EditCommand(string unmodified, ulong flags)
    {
        if ((flags & (ControlFlag | OptionFlag)) != 0 || unmodified.Length != 1) return null;
        var shift = (flags & ShiftFlag) != 0;
        return char.ToLowerInvariant(unmodified[0]) switch
        {
            'a' when !shift => "selectAll:",
            'c' when !shift => "copy:",
            'x' when !shift => "cut:",
            'v' when !shift => "paste:",
            'z' => shift ? "redo" : "undo",
            _ => null,
        };
    }

    private static ulong Flags(NativeModifiers m) =>
        (m.HasFlag(NativeModifiers.Shift) ? ShiftFlag : 0) | (m.HasFlag(NativeModifiers.Control) ? ControlFlag : 0)
        | (m.HasFlag(NativeModifiers.Alt) ? OptionFlag : 0) | (m.HasFlag(NativeModifiers.Meta) ? CommandFlag : 0);

    private static (ulong Flag, ushort KeyCode)? ModifierKey(string key, string? code) => key switch
    {
        "Shift" => (ShiftFlag, code == "ShiftRight" ? (ushort)60 : (ushort)56),
        "Control" => (ControlFlag, code == "ControlRight" ? (ushort)62 : (ushort)59),
        "Alt" => (OptionFlag, code == "AltRight" ? (ushort)61 : (ushort)58),
        "Meta" => (CommandFlag, code == "MetaRight" ? (ushort)54 : (ushort)55),
        _ => null,
    };

    /// <summary>
    /// The Mac virtual key code and characters of a DOM key: named keys, and the printable keys of the US layout (the
    /// layout WebDriver assumes; WebKit derives the DOM <c>code</c> from the key code). Other characters keep key code 0.
    /// </summary>
    internal static (ushort KeyCode, string Characters, string Unmodified, bool Shifted) Describe(string key)
    {
        switch (key)
        {
            case "Enter": return (36, "\r", "\r", false);
            case "Tab": return (48, "\t", "\t", false);
            case "Backspace": return (51, "\u007f", "\u007f", false);
            case "Delete": return (117, "\uF728", "\uF728", false);
            case "Escape" or "Esc": return (53, "\u001b", "\u001b", false);
            case "ArrowUp": return (126, "\uF700", "\uF700", false);
            case "ArrowDown": return (125, "\uF701", "\uF701", false);
            case "ArrowLeft": return (123, "\uF702", "\uF702", false);
            case "ArrowRight": return (124, "\uF703", "\uF703", false);
            case "Home": return (115, "\uF729", "\uF729", false);
            case "End": return (119, "\uF72B", "\uF72B", false);
            case "PageUp": return (116, "\uF72C", "\uF72C", false);
            case "PageDown": return (121, "\uF72D", "\uF72D", false);
            case "Insert" or "Help": return (114, "\uF727", "\uF727", false);
            case " " or "Space" or "Spacebar": return (49, " ", " ", false);
        }
        if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 12)
        {
            ReadOnlySpan<ushort> codes = [122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111];
            var c = ((char)(0xF704 + f - 1)).ToString();
            return (codes[f - 1], c, c, false);
        }
        if (key.Length == 1)
        {
            var ch = key[0];
            const string Shifted = "~!@#$%^&*()_+{}|:\"<>?";
            const string Base = "`1234567890-=[]\\;',./";
            var isUpper = char.IsAsciiLetterUpper(ch);
            var shiftIndex = Shifted.IndexOf(ch);
            var plain = isUpper ? char.ToLowerInvariant(ch) : shiftIndex >= 0 ? Base[shiftIndex] : ch;
            var code = UsKeyCode(plain);
            if (code is { } kc) return (kc, key, isUpper || shiftIndex >= 0 ? key : key, isUpper || shiftIndex >= 0);
        }
        return (0, key, key, false);
    }

    private static ushort? UsKeyCode(char c) => c switch
    {
        'a' => 0, 's' => 1, 'd' => 2, 'f' => 3, 'h' => 4, 'g' => 5, 'z' => 6, 'x' => 7, 'c' => 8, 'v' => 9, 'b' => 11,
        'q' => 12, 'w' => 13, 'e' => 14, 'r' => 15, 'y' => 16, 't' => 17, '1' => 18, '2' => 19, '3' => 20, '4' => 21,
        '6' => 22, '5' => 23, '=' => 24, '9' => 25, '7' => 26, '-' => 27, '8' => 28, '0' => 29, ']' => 30, 'o' => 31,
        'u' => 32, '[' => 33, 'i' => 34, 'p' => 35, 'l' => 37, 'j' => 38, '\'' => 39, 'k' => 40, ';' => 41, '\\' => 42,
        ',' => 43, '/' => 44, 'n' => 45, 'm' => 46, '.' => 47, '`' => 50,
        _ => null,
    };

    // ── coordinates and time ──

    /// <summary>A main-frame viewport point (CSS px) in the view's window (the view's root when it has no window).</summary>
    private unsafe static CGPoint WindowPoint(nint view, double x, double y)
    {
        var zoom = MacObjC.RespondsTo(view, "pageZoom") ? MacRuntime.SendDouble(view, "pageZoom") : 1;
        var magnification = MacRuntime.SendDouble(view, "magnification");
        var scale = (zoom > 0 ? zoom : 1) * (magnification > 0 ? magnification : 1);
        var bounds = ((delegate* unmanaged<nint, nint, CGRect>)MacObjC.MsgSend)(view, MacObjC.Sel("bounds"));
        var local = MacObjC.SendBool(view, "isFlipped") ? new CGPoint(x * scale, y * scale) : new CGPoint(x * scale, bounds.Height - y * scale);
        return ((delegate* unmanaged<nint, nint, CGPoint, nint, CGPoint>)MacObjC.MsgSend)(view, MacObjC.Sel("convertPoint:toView:"), local, 0);
    }

    private static double Uptime() => MacRuntime.SendDouble(MacObjC.Send(MacObjC.Cls("NSProcessInfo"), "processInfo"), "systemUptime");

    public void Dispose()
    {
        var view = Interlocked.Exchange(ref _view, 0);
        if (view == 0) return;
        Guard.Remove(this);
        if (_held is { } held) Guard.Lift(ButtonMask(held));
        _held = null;
        MacObjC.OnMainThread(() => MacObjC.Release(view));
    }

    // ── process-wide guard: event marks, the sendEvent: filter, pressed buttons, the user's own input ──

    /// <summary>
    /// What all <see cref="NativeInput"/>s share, installed once on the main thread: the mark on our events, the
    /// <c>-[NSApplication sendEvent:]</c> filter that drops our re-sent key events, <c>+[NSEvent pressedMouseButtons]</c>
    /// with the agent's held buttons, and a local event monitor noting the user's own input per view.
    /// </summary>
    private static unsafe class Guard
    {
        private static readonly Lock Gate = new();
        private static readonly List<WeakReference<NativeInput>> Inputs = [];
        private static nint _markKey;
        private static nint _markValue;
        private static delegate* unmanaged<nint, nint, nint, void> _sendEvent;
        private static delegate* unmanaged<nint, nint, nuint> _pressedButtons;
        private static long _agentButtons;
        private static nint _monitor;
        private static int _suppressed;

        public static int Suppressed => Volatile.Read(ref _suppressed);

        public static void Install(NativeInput input)
        {
            lock (Gate)
            {
                Inputs.RemoveAll(w => !w.TryGetTarget(out _));
                Inputs.Add(new WeakReference<NativeInput>(input));
                if (_markKey != 0) return;
                _markKey = (nint)NativeMemory.Alloc(1); // a unique address for objc_setAssociatedObject
                _markValue = MacRuntime.NewString("omp-gui-agent-input"); // kept for the process
                HookSendEvent();
                HookPressedButtons();
                AddMonitor();
            }
        }

        public static void Remove(NativeInput input)
        {
            lock (Gate) Inputs.RemoveAll(w => !w.TryGetTarget(out var i) || i == input);
        }

        public static void Mark(nint ev) => MacRuntime.SetAssociated(ev, _markKey, _markValue);

        private static bool IsOurs(nint ev) => _markKey != 0 && ev != 0 && MacRuntime.GetAssociated(ev, _markKey) != 0;

        public static void Press(ulong mask) => Interlocked.Or(ref _agentButtons, (long)mask);

        public static void Lift(ulong mask) => Interlocked.And(ref _agentButtons, ~(long)mask);

        /// <summary>⌘ key-downs of ours waiting for WebKit's verdict: the view and the Edit-menu action to run if the page leaves it.</summary>
        private static readonly Dictionary<nint, (WeakReference<NativeInput> Input, string? Command)> Equivalents = [];

        /// <summary>Remembers <paramref name="ev"/> (retained for a few seconds) until WebKit re-sends it or the time is up.</summary>
        public static void ExpectEquivalent(nint ev, NativeInput input, string? command)
        {
            lock (Gate) Equivalents[MacObjC.Retain(ev)] = (new WeakReference<NativeInput>(input), command);
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => MacObjC.OnMainThread(() => Forget(ev)), TaskScheduler.Default);
        }

        private static (WeakReference<NativeInput> Input, string? Command)? Forget(nint ev)
        {
            (WeakReference<NativeInput>, string?) entry;
            lock (Gate)
            {
                if (!Equivalents.Remove(ev, out entry)) return null;
            }
            MacObjC.Release(ev);
            return entry;
        }

        /// <summary>The Edit-menu action for a ⌘ combination the page did not handle, on the agent's web view only.</summary>
        private static void RunEditCommand(nint ev)
        {
            if (Forget(ev) is not { } entry || entry.Command is not { } command || !entry.Input.TryGetTarget(out var input)) return;
            var view = input._view;
            if (view == 0) return;
            if (command is "undo" or "redo")
            {
                var undo = MacObjC.Send(view, "undoManager");
                if (undo != 0 && MacObjC.SendBool(undo, command == "undo" ? "canUndo" : "canRedo")) MacObjC.SendVoid(undo, command);
            }
            else if (MacObjC.RespondsTo(view, command)) MacObjC.SendVoid(view, command, 0);
        }

        private static void HookSendEvent()
        {
            var app = MacObjC.Send(MacObjC.Cls("NSApplication"), "sharedApplication");
            if (app == 0) return;
            var cls = MacObjC.ClassOf(app);
            var method = MacRuntime.InstanceMethod(cls, "sendEvent:");
            if (method == 0) return;
            var ours = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&SendEvent;
            var original = MacRuntime.Implementation(method);
            if (original == ours) return;
            _sendEvent = (delegate* unmanaged<nint, nint, nint, void>)original;
            // The app's class gets its own method when it only inherits sendEvent: (NSApplication's stays as it is)
            if (!MacRuntime.AddOwnMethod(cls, "sendEvent:", ours, "v@:@")) MacRuntime.SetImplementation(method, ours);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void SendEvent(nint self, nint cmd, nint ev)
        {
            try
            {
                if (IsOurs(ev))
                {
                    var type = (ulong)MacObjC.SendLong(ev, "type");
                    if (type is KeyDown or KeyUp or FlagsChanged)
                    {
                        // A key the page did not handle, re-sent by WebKit: never the app's menus or shortcuts
                        Interlocked.Increment(ref _suppressed);
                        if (type == KeyDown) RunEditCommand(ev);
                        return;
                    }
                }
                _sendEvent(self, cmd, ev);
            }
            catch
            {
                // Nothing may unwind into AppKit
            }
        }

        private static void HookPressedButtons()
        {
            var method = MacRuntime.ClassMethod(MacObjC.Cls("NSEvent"), "pressedMouseButtons");
            if (method == 0) return;
            var ours = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nuint>)&PressedMouseButtons;
            var original = MacRuntime.Implementation(method);
            if (original == ours) return;
            _pressedButtons = (delegate* unmanaged<nint, nint, nuint>)original;
            MacRuntime.SetImplementation(method, ours);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static nuint PressedMouseButtons(nint self, nint cmd)
        {
            try
            {
                return _pressedButtons(self, cmd) | (nuint)Interlocked.Read(ref _agentButtons);
            }
            catch
            {
                return 0;
            }
        }

        // The user's clicks, keys and wheel (not plain pointer moves) on any of our views
        private const ulong MonitorMask = (1UL << 1) | (1UL << 2) | (1UL << 3) | (1UL << 4) | (1UL << 6) | (1UL << 7)
            | (1UL << 10) | (1UL << 11) | (1UL << 22) | (1UL << 25) | (1UL << 26) | (1UL << 27);

        private static void AddMonitor()
        {
            var block = MacBlocks.Create((nint)(delegate* unmanaged<nint, nint, nint>)&OnLocalEvent, Inputs, "@16@?0@8");
            try
            {
                _monitor = MacObjC.Retain(((delegate* unmanaged<nint, nint, ulong, nint, nint>)MacObjC.MsgSend)(
                    MacObjC.Cls("NSEvent"), MacObjC.Sel("addLocalMonitorForEventsMatchingMask:handler:"), MonitorMask, block));
            }
            finally
            {
                MacBlocks.Release(block); // the monitor copied it; it lives as long as the process
            }
        }

        [UnmanagedCallersOnly]
        private static nint OnLocalEvent(nint block, nint ev)
        {
            try
            {
                if (ev != 0 && !IsOurs(ev)) NoteUserInput(ev);
            }
            catch
            {
                // Nothing may unwind into AppKit
            }
            return ev;
        }

        private static void NoteUserInput(nint ev)
        {
            NativeInput[] inputs;
            lock (Gate) inputs = [.. Inputs.Select(w => w.TryGetTarget(out var i) ? i : null).OfType<NativeInput>()];
            if (inputs.Length == 0) return;
            var window = MacObjC.Send(ev, "window");
            if (window == 0) return;
            var type = (ulong)MacObjC.SendLong(ev, "type");
            foreach (var input in inputs)
            {
                var view = input._view;
                if (view == 0 || MacObjC.Send(view, "window") != window || MacObjC.SendBool(view, "isHiddenOrHasHiddenAncestor")) continue;
                bool mine;
                if (type is KeyDown or KeyUp)
                {
                    var responder = MacObjC.Send(window, "firstResponder");
                    mine = responder == view || (responder != 0 && MacObjC.IsKindOf(responder, "NSView") && MacObjC.SendBool(responder, "isDescendantOf:", view));
                }
                else
                {
                    var p = ((delegate* unmanaged<nint, nint, CGPoint>)MacObjC.MsgSend)(ev, MacObjC.Sel("locationInWindow"));
                    var local = ((delegate* unmanaged<nint, nint, CGPoint, nint, CGPoint>)MacObjC.MsgSend)(view, MacObjC.Sel("convertPoint:fromView:"), p, 0);
                    var bounds = ((delegate* unmanaged<nint, nint, CGRect>)MacObjC.MsgSend)(view, MacObjC.Sel("bounds"));
                    mine = local.X >= bounds.X && local.Y >= bounds.Y && local.X < bounds.X + bounds.Width && local.Y < bounds.Y + bounds.Height;
                }
                if (mine) Interlocked.Exchange(ref input.LastUserInput, Environment.TickCount64);
            }
        }
    }

    // ── Core Graphics / Core Foundation (creating events needs no permission; only posting them would) ──

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreGraphics)]
    private static extern nint CGEventCreateScrollWheelEvent2(nint source, uint units, uint wheelCount, int wheel1, int wheel2, int wheel3);

    [DllImport(CoreGraphics)]
    private static extern void CGEventSetLocation(nint ev, CGPoint location);

    [DllImport(CoreGraphics)]
    private static extern void CGEventSetFlags(nint ev, ulong flags);

    [DllImport(CoreGraphics)]
    private static extern void CGEventSetIntegerValueField(nint ev, int field, long value);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint obj);
}
