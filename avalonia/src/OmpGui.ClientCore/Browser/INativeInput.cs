namespace OmpGui.ClientCore.Browser;

public enum NativeMouseButton
{
    Left,
    Right,
    Middle,
}

[Flags]
public enum NativeModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    Meta = 8,
}

public enum NativeMouseAction
{
    Move,
    Down,
    Up,
}

/// <summary>One step of user-equivalent input (omp's Tern <c>input</c> step). Coordinates are main-frame viewport CSS pixels.</summary>
public abstract record NativeInputStep;

/// <param name="Clicks">0 for moves; n for the n-th down/up of a multi-click. While a button is held, moves carry it.</param>
public sealed record NativeMouseStep(NativeMouseAction Action, double X, double Y, NativeMouseButton Button, int Clicks, NativeModifiers Modifiers)
    : NativeInputStep;

public sealed record NativeWheelStep(double X, double Y, double DeltaX, double DeltaY, NativeModifiers Modifiers) : NativeInputStep;

/// <param name="Key">The DOM <c>key</c> ("Enter", "a", "A", "ArrowLeft", "Shift").</param>
/// <param name="Code">The DOM <c>code</c> ("KeyA", "ShiftLeft"); derived from <paramref name="Key"/> when null.</param>
public sealed record NativeKeyStep(bool Down, string Key, string? Code, NativeModifiers Modifiers) : NativeInputStep;

/// <summary>Text as an input method commits it (any script, the user's keyboard layout plays no part): no key events.</summary>
public sealed record NativeTextStep(string Text) : NativeInputStep;

/// <summary>
/// Input to one web view that its engine treats like the user's own: trusted DOM events that grant user activation,
/// delivered inside the app (no OS permission, nothing posted to the window server). macOS: <c>NSEvent</c>s to the
/// <c>WKWebView</c>'s responder methods; text through <c>insertText:replacementRange:</c>.
/// </summary>
/// <remarks>
/// Steps run in order with no added delay (pacing is the caller's: <see cref="NativeInputActions"/>). The app's own
/// focus stays where the user left it, and real input from the user to the same view wins: a step waits while the user
/// is using the view (up to a second), then goes on.
/// </remarks>
public interface INativeInput
{
    /// <summary>Why this view cannot take native input (engine, missing API); null when it can.</summary>
    string? Unsupported { get; }

    /// <summary>Whether pointer moves without a button reach the page (hover); false where only clicks do.</summary>
    bool CanHover { get; }

    /// <summary>
    /// Sends <paramref name="steps"/>. Throws <see cref="NotSupportedException"/> (with the reason) when
    /// <see cref="Unsupported"/> is set or a step cannot be delivered natively (then nothing of that step happened).
    /// </summary>
    Task SendAsync(IReadOnlyList<NativeInputStep> steps, CancellationToken ct);
}

/// <summary>Input for an engine without a native path: every call reports why.</summary>
public sealed class UnsupportedNativeInput(string reason) : INativeInput
{
    public string? Unsupported => reason;

    public bool CanHover => false;

    public Task SendAsync(IReadOnlyList<NativeInputStep> steps, CancellationToken ct) =>
        Task.FromException(new NotSupportedException(reason));
}
