using System.Buffers.Binary;
using System.Text;
using Ombra.ClientCore.Browser;

namespace Ombra.App.Nightly;

/// <summary>
/// Nightly's embed protocol, version 5 (desktop <c>src/zen/embed/ZenEmbedHost.mm</c>): plain Mach messages, a 24-byte
/// <c>mach_msg_header_t</c> then fixed little-endian fields. Nightly → Ombra messages arrive on Ombra's bootstrap service
/// port; Ombra → Nightly go to the receive right Nightly hands over in Hello. Script, ScriptResult and Popup carry their
/// text out of line (one OOL descriptor at 28, fields from 44). This class only lays out and reads bytes (no Mach calls),
/// so the layouts are unit tested against the offsets of the C++ structs.
/// </summary>
internal static class NightlyProtocol
{
    public const uint Version = 5;
    public const int HeaderSize = 24;
    /// <summary>URL and title bytes in Command and Location (kMaxText).</summary>
    public const int MaxText = 8192;
    /// <summary>Script source plus arguments, and a script's result, in bytes (kMaxScript).</summary>
    public const int MaxScript = 64 << 20;

    // Message ids (mach_msg_id_t).
    public const int Surface = 1, Frame = 2, Release = 3;
    public const int Mouse = 20, Key = 21, Focus = 22, Resize = 23, Wheel = 24, Text = 25, Command = 26, OpenWindow = 27, Script = 28,
        PopupChoice = 29, MarkedText = 30;
    public const int Hello = 100, HelloAck = 101, Window = 102, Location = 103, ScriptResult = 104, Popup = 105, PopupClosed = 106,
        Cursor = 107, TextRect = 108;
    // Protocol 4 (K1 cutover): Ombra → Nightly 40–49, Nightly → Ombra 120–129.
    public const int Quit = 40, DialogReply = 41, DownloadCancel = 42, OpenBrowserWindow = 43;
    public const int Dialog = 120, DialogClosed = 121, Download = 122;
    // Drags (protocol 4): Ombra → Nightly 44–45, Nightly → Ombra 123–124 (desktop ZenEmbedHost.mm, "Drags").
    public const int DragTarget = 44, DragSource = 45, DragStarted = 123, DragStatus = 124;
    // Protocol 5: whether a window's pane is on screen (Ombra → Nightly).
    public const int Visibility = 46;
    /// <summary>Bytes of a DialogReply's JSON, including archived native printer settings (kMaxDialogReply).</summary>
    public const int MaxReply = 1 << 20;
    /// <summary>UTF-16 units of text a Text or MarkedText message carries.</summary>
    public const int MaxInputText = 64;

    public enum MouseKind { Down = 1, Up = 2, Move = 3, Exit = 4 }

    /// <summary>An AppKit drag over a pane (<see cref="DragTargetMessage"/>).</summary>
    public enum DragPhase : uint { Enter = 1, Over = 2, Exit = 3, Drop = 4 }

    /// <summary>The window's own drag as Ombra carries it on (<see cref="DragSourceMessage"/>): Ombra's AppKit drag runs
    /// it now, it ended (with an operation), or the user cancelled it.</summary>
    public enum DragSourceState : uint { Carried = 1, Ended = 2, Cancelled = 3 }

    /// <summary>Device (protocol 4): JSON of the emulated device (<see cref="DeviceMessage"/>), empty to end the device mode.
    /// Theme (protocol 4): Ombra's appearance for the whole runtime (<see cref="ThemeMessage"/>). Speculate (protocol 5):
    /// an intent's JSON (<see cref="SpeculateJson"/>, <see cref="CancelSpeculationJson"/>).</summary>
    public enum CommandOp : uint { Navigate = 1, Reload = 2, Back = 3, Forward = 4, Stop = 5, Close = 6, Device = 7, Theme = 8, Speculate = 9 }

    /// <summary>
    /// A device the page in a window emulates (desktop <c>zen-embed.js</c> ZenEmbedDevice). <paramref name="Mobile"/>: a
    /// phone or tablet (meta viewport, screen = viewport, maxTouchPoints, orientation); <paramref name="PixelRatio"/> 0 keeps
    /// the display's; an empty <paramref name="UserAgent"/> keeps Nightly's. Ombra sizes the window to the viewport itself.
    /// </summary>
    public readonly record struct Device(bool Mobile, double PixelRatio, string UserAgent, bool Touch, bool Landscape);

    /// <summary>Where a script runs: the page's own world (evaluateJavaScript), or a function called in Ombra's isolated world.</summary>
    public enum ScriptKind : uint { Page = 1, Isolated = 2 }

    /// <summary>A script's answer: the value as text (a string as it is, else JSON), nothing, an exception, or no document.</summary>
    public enum ScriptStatus : uint { Value = 0, Undefined = 1, Error = 2, NoPage = 3 }

    [Flags]
    public enum LocationFlags : uint { None = 0, Loading = 1, CanGoBack = 2, CanGoForward = 4 }

    /// <summary>NSEventPhase values (scroll phases).</summary>
    [Flags]
    public enum Phase : uint { None = 0, Began = 1, Stationary = 2, Changed = 4, Ended = 8, Cancelled = 16, MayBegin = 32 }

    private const uint CopySend = 19; // MACH_MSG_TYPE_COPY_SEND in msgh_bits (remote), no local right
    private const uint Complex = 0x80000000; // MACH_MSGH_BITS_COMPLEX
    private const byte OolDescriptor = 1, VirtualCopy = 1; // MACH_MSG_OOL_DESCRIPTOR, MACH_MSG_VIRTUAL_COPY
    private const uint ResizeHasOrigin = 1, WheelPrecise = 1;

    // ── Ombra → Nightly ──

    public static byte[] HelloAckMessage()
    {
        var m = New(HelloAck, 32);
        U32(m, 24, Version);
        return m;
    }

    public static byte[] ReleaseMessage(uint windowId, ulong seq)
    {
        var m = New(Release, 40);
        U32(m, 24, windowId);
        U64(m, 32, seq);
        return m;
    }

    /// <param name="button">0 left, 1 right, 2 middle, -1 none (a move with no button down).</param>
    /// <param name="modifiers">NSEventModifierFlags.</param>
    public static byte[] MouseMessage(uint windowId, MouseKind kind, int button, int clickCount, double x, double y, ulong modifiers)
    {
        var m = New(Mouse, 64);
        U32(m, 24, windowId);
        I32(m, 28, (int)kind);
        I32(m, 32, button);
        I32(m, 36, clickCount);
        F64(m, 40, x);
        F64(m, 48, y);
        U64(m, 56, modifiers);
        return m;
    }

    /// <param name="chars">NSEvent characters (at most 16 UTF-16 units are sent).</param>
    /// <param name="unmodified">charactersIgnoringModifiers.</param>
    public static byte[] KeyMessage(uint windowId, bool down, int keyCode, string chars, string unmodified, ulong modifiers, bool isRepeat) =>
        KeyLike(windowId, down ? 1 : 0, keyCode, chars, unmodified, modifiers, isRepeat);

    /// <summary>A modifier key alone (<c>flagsChanged:</c>): its key code and the modifier flags after it.</summary>
    public static byte[] FlagsMessage(uint windowId, int keyCode, ulong modifiers) => KeyLike(windowId, 2, keyCode, "", "", modifiers, false);

    private static byte[] KeyLike(uint windowId, int action, int keyCode, string chars, string unmodified, ulong modifiers, bool isRepeat)
    {
        var m = New(Key, 120);
        U32(m, 24, windowId);
        I32(m, 28, action);
        I32(m, 32, keyCode);
        I32(m, 36, Utf16(m, 48, chars, 16));
        U64(m, 40, modifiers);
        I32(m, 112, Utf16(m, 80, unmodified, 16));
        I32(m, 116, isRepeat ? 1 : 0);
        return m;
    }

    public static byte[] FocusMessage(uint windowId, bool focused)
    {
        var m = New(Focus, 32);
        U32(m, 24, windowId);
        U32(m, 28, focused ? 1u : 0u);
        return m;
    }

    /// <summary>
    /// Whether the window's pane is on screen (protocol 5, id 46): hidden, Nightly makes its browser inactive
    /// (<c>document.hidden</c>, rAF and timers throttled as a background tab's), exports no frames and gives back those
    /// in flight; visible, it paints a fresh frame. <paramref name="generation"/> grows with every message for a window;
    /// Nightly ignores one that is not above the last it applied.
    /// </summary>
    public static byte[] VisibilityMessage(uint windowId, bool visible, uint generation)
    {
        var m = New(Visibility, 40);
        U32(m, 24, windowId);
        U32(m, 28, visible ? 1u : 0u);
        U32(m, 32, generation);
        return m;
    }

    /// <param name="width">Content size in points (DIP).</param>
    /// <param name="screenOrigin">Where the pane's bottom-left corner is on screen, in Cocoa coordinates (the window moves
    /// there and takes that screen's backing scale); null keeps it where it is.</param>
    public static byte[] ResizeMessage(uint windowId, double width, double height, (double X, double Y)? screenOrigin)
    {
        var m = New(Resize, 64);
        U32(m, 24, windowId);
        U32(m, 28, screenOrigin is null ? 0 : ResizeHasOrigin);
        F64(m, 32, width);
        F64(m, 40, height);
        F64(m, 48, screenOrigin?.X ?? 0);
        F64(m, 56, screenOrigin?.Y ?? 0);
        return m;
    }

    /// <summary>One scroll event as macOS reported it (<see cref="WheelInput"/>), at a window point.</summary>
    public static byte[] WheelMessage(uint windowId, double x, double y, in WheelInput input)
    {
        var m = New(Wheel, 96);
        U32(m, 24, windowId);
        U32(m, 28, (uint)input.Phase);
        U32(m, 32, (uint)input.MomentumPhase);
        U32(m, 36, input.Precise ? WheelPrecise : 0);
        F64(m, 40, x);
        F64(m, 48, y);
        F64(m, 56, input.DeltaX);
        F64(m, 64, input.DeltaY);
        F64(m, 72, input.LineDeltaX);
        F64(m, 80, input.LineDeltaY);
        U64(m, 88, input.Modifiers);
        return m;
    }

    /// <summary>Committed text that came without a forwarded key (an input method's commit); at most 64 UTF-16 units.</summary>
    public static byte[] TextMessage(uint windowId, string text)
    {
        var m = New(Text, 160);
        U32(m, 24, windowId);
        I32(m, 28, Utf16(m, 32, text, MaxInputText));
        return m;
    }

    /// <summary>
    /// The input method's composition (<c>setMarkedText:</c>): <paramref name="text"/> (at most <see cref="MaxInputText"/>
    /// UTF-16 units) with the caret at <paramref name="selectionStart"/> for <paramref name="selectionLength"/> units; an
    /// empty text ends the composition with nothing.
    /// </summary>
    public static byte[] MarkedTextMessage(uint windowId, string text, int selectionStart, int selectionLength)
    {
        var m = New(MarkedText, 168);
        U32(m, 24, windowId);
        var count = Utf16(m, 40, text, MaxInputText);
        var start = Math.Clamp(selectionStart, 0, count);
        I32(m, 28, start);
        I32(m, 32, Math.Clamp(selectionLength, 0, count - start));
        I32(m, 36, count);
        return m;
    }

    /// <summary>The user's answer to popup <paramref name="popupId"/>: the index of the chosen item, -1 when dismissed.</summary>
    public static byte[] PopupChoiceMessage(uint windowId, uint popupId, int index)
    {
        var m = New(PopupChoice, 40);
        U32(m, 24, windowId);
        U32(m, 28, popupId);
        I32(m, 32, index);
        return m;
    }

    /// <summary>Asks Nightly to quit as a user would: it writes its session and closes its profile, then exits.</summary>
    public static byte[] QuitMessage() => New(Quit, 32);

    /// <summary>Stops the download that dialog <paramref name="dialogId"/> chose a path for.</summary>
    public static byte[] DownloadCancelMessage(uint windowId, uint dialogId)
    {
        var m = New(DownloadCancel, 32);
        U32(m, 24, windowId);
        U32(m, 28, dialogId);
        return m;
    }

    /// <summary>
    /// The answer (<paramref name="length"/> bytes of UTF-8 JSON at <paramref name="data"/>, copied by the kernel while
    /// sending) to dialog <paramref name="dialogId"/>; laid out as ScriptResult.
    /// </summary>
    public static byte[] DialogReplyMessage(uint windowId, uint dialogId, nint data, int length)
    {
        var m = New(DialogReply, 64);
        U32(m, 0, CopySend | Complex);
        U32(m, 24, 1);
        U64(m, 28, (ulong)data);
        m[36] = 0;
        m[37] = VirtualCopy;
        m[39] = OolDescriptor;
        U32(m, 40, (uint)length);
        U32(m, 44, windowId);
        U32(m, 48, dialogId);
        U32(m, 56, (uint)length);
        return m;
    }

    /// <summary>A command for the window's chrome script; null when <paramref name="text"/> is longer than <see cref="MaxText"/> bytes.</summary>
    public static byte[]? CommandMessage(uint windowId, CommandOp op, string text = "")
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxText) return null;
        var m = New(Command, (40 + bytes.Length + 3) & ~3);
        U32(m, 24, windowId);
        U32(m, 28, (uint)op);
        U32(m, 32, (uint)bytes.Length);
        bytes.CopyTo(m, 40);
        return m;
    }

    /// <summary>The device a window emulates from now on (null: none); null when the user agent does not fit.</summary>
    public static byte[]? DeviceMessage(uint windowId, Device? device) =>
        CommandMessage(windowId, CommandOp.Device, device is { } d ? DeviceJson(d) : "");

    /// <summary>Ombra's appearance, which the whole runtime follows (Nightly's <c>ui.systemUsesDarkTheme</c>: Zen's chrome
    /// and the pages' <c>prefers-color-scheme</c>); not tied to a window.</summary>
    public static byte[] ThemeMessage(bool dark) => CommandMessage(0, CommandOp.Theme, dark ? "dark" : "light")!;

    internal static string DeviceJson(Device d) => Json(json =>
    {
        json.WriteStartObject();
        json.WriteBoolean("mobile", d.Mobile);
        json.WriteNumber("dpr", d.PixelRatio > 0 ? d.PixelRatio : 0);
        json.WriteString("userAgent", d.UserAgent);
        json.WriteBoolean("touch", d.Touch);
        json.WriteBoolean("landscape", d.Landscape);
        json.WriteEndObject();
    });

    /// <summary>
    /// An intent for window <paramref name="windowId"/> (protocol 5, Command op 9; 0: a pane whose window is still being
    /// opened, which Nightly gives a new chrome-less window's origin attributes): <paramref name="json"/> from
    /// <see cref="SpeculateJson"/> or <see cref="CancelSpeculationJson"/>. Null when it is longer than <see cref="MaxText"/> bytes.
    /// </summary>
    public static byte[]? SpeculateMessage(uint windowId, string json) => CommandMessage(windowId, CommandOp.Speculate, json);

    /// <summary>
    /// A hint as Speculate's JSON, <c>{"v":1,"id","source","action":"hint","url","p","leadMs","ttlMs"}</c>: Nightly opens
    /// a connection to the URL's origin (never an HTTP request) and validates the same bounds. Null when the hint breaks
    /// them: an id other than 16 lowercase hex digits, an unknown source, a URL that is not absolute http(s) or carries
    /// user info, p outside 0–1, a lead outside 0–5000 ms, a TTL outside 100–30000 ms.
    /// </summary>
    internal static string? SpeculateJson(SpeculationHint hint)
    {
        if (!SpeculationHint.IsId(hint.Id) || !SpeculationHint.Eligible(hint.Url) || !(hint.P is >= 0.0 and <= 1.0)
            || hint.Source is not (SpeculationHint.AgentOpen or SpeculationHint.AgentClick or SpeculationHint.AgentGoto or SpeculationHint.AddressCommit)
            || hint.LeadMs is < 0 or > SpeculationHint.MaxLeadMs || hint.TtlMs is < SpeculationHint.MinTtlMs or > SpeculationHint.MaxTtlMs)
            return null;
        return Json(json =>
        {
            json.WriteStartObject();
            json.WriteNumber("v", 1);
            json.WriteString("id", hint.Id);
            json.WriteString("source", hint.Source);
            json.WriteString("action", "hint");
            json.WriteString("url", hint.Url.AbsoluteUri);
            json.WriteNumber("p", hint.P);
            json.WriteNumber("leadMs", hint.LeadMs);
            json.WriteNumber("ttlMs", hint.TtlMs);
            json.WriteEndObject();
        });
    }

    /// <summary>Withdraws hint <paramref name="id"/> (its action was abandoned): <c>{"v":1,"id","action":"cancel"}</c>;
    /// null when <paramref name="id"/> is not a hint id.</summary>
    internal static string? CancelSpeculationJson(string id) => SpeculationHint.IsId(id)
        ? Json(json =>
        {
            json.WriteStartObject();
            json.WriteNumber("v", 1);
            json.WriteString("id", id);
            json.WriteString("action", "cancel");
            json.WriteEndObject();
        })
        : null;

    private static string Json(Action<System.Text.Json.Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var json = new System.Text.Json.Utf8JsonWriter(buffer)) write(json);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public static byte[] OpenWindowMessage(uint pane)
    {
        var m = New(OpenWindow, 32);
        U32(m, 24, pane);
        return m;
    }

    /// <summary>
    /// A full browser window for <paramref name="pane"/> (protocol 4, id 43): <c>browser.xhtml</c>, Nightly's whole UI,
    /// embedded as the chrome-less windows are, with its menus and panels as Nightly's own windows over the pane.
    /// Laid out as OpenWindow.
    /// </summary>
    public static byte[] OpenBrowserWindowMessage(uint pane)
    {
        var m = New(OpenBrowserWindow, 32);
        U32(m, 24, pane);
        return m;
    }

    /// <summary>
    /// A script for the window's top-level document. Its text (<paramref name="sourceLength"/> bytes of UTF-8 source,
    /// then <paramref name="argsLength"/> bytes of UTF-8 JSON arguments) is at <paramref name="data"/>, which the kernel
    /// copies when the message is sent (the caller frees it afterwards).
    /// </summary>
    public static byte[] ScriptMessage(uint windowId, uint requestId, ScriptKind kind, nint data, int sourceLength, int argsLength)
    {
        var m = New(Script, 64);
        U32(m, 0, CopySend | Complex);
        U32(m, 24, 1); // one descriptor
        U64(m, 28, (ulong)data);
        m[36] = 0; // deallocate: no, the caller's memory
        m[37] = VirtualCopy;
        m[39] = OolDescriptor;
        U32(m, 40, (uint)(sourceLength + argsLength));
        U32(m, 44, windowId);
        U32(m, 48, requestId);
        U32(m, 52, (uint)kind);
        U32(m, 56, (uint)sourceLength);
        U32(m, 60, (uint)argsLength);
        return m;
    }

    /// <summary>
    /// An AppKit drag over the pane at a window point: entered, moved, left or dropped. <paramref name="operations"/>: the
    /// NSDragOperation mask its source allows; its data is on the system's drag pasteboard. Nightly answers DragStatus.
    /// </summary>
    public static byte[] DragTargetMessage(uint windowId, DragPhase phase, double x, double y, ulong operations, ulong modifiers)
    {
        var m = New(DragTarget, 64);
        U32(m, 24, windowId);
        U32(m, 28, (uint)phase);
        F64(m, 32, x);
        F64(m, 40, y);
        U64(m, 48, operations);
        U64(m, 56, modifiers);
        return m;
    }

    /// <summary>The window's own drag (DragStarted) left the pane and Ombra's AppKit drag carries it on, or that drag ended
    /// with <paramref name="operation"/> (NSDragOperation) at a window point (outside the pane too).</summary>
    public static byte[] DragSourceMessage(uint windowId, DragSourceState state, double x, double y, ulong operation, ulong modifiers)
    {
        var m = New(DragSource, 64);
        U32(m, 24, windowId);
        U32(m, 28, (uint)state);
        F64(m, 32, x);
        F64(m, 40, y);
        U64(m, 48, operation);
        U64(m, 56, modifiers);
        return m;
    }

    // ── Nightly → Ombra (offsets in the received message; a complex message's port descriptor is at 28) ──

    public readonly record struct HelloInfo(uint Version, int Pid);
    public readonly record struct SurfaceInfo(uint WindowId, uint SurfaceId, int Width, int Height);
    public readonly record struct FrameInfo(uint WindowId, uint SurfaceId, ulong Seq, ulong PresentedAt, int Width, int Height);
    /// <param name="Opener">The window whose page opened this one (<c>window.open</c>, a link in a new tab); 0 for none.</param>
    public readonly record struct WindowInfo(uint WindowId, bool Visible, double Width, double Height, double Scale, uint Pane, uint Opener = 0);
    public readonly record struct LocationInfo(uint WindowId, LocationFlags Flags, string Url, string Title);
    public readonly record struct ScriptResultInfo(uint WindowId, uint RequestId, ScriptStatus Status, string Text);
    /// <param name="Json">What to show (desktop <c>zen-embed.js</c>, <see cref="NightlyPopup"/>).</param>
    public readonly record struct PopupInfo(uint WindowId, uint PopupId, string Json);
    /// <summary>A rectangle of the window's content in points, top-left origin.</summary>
    public readonly record struct TextRectInfo(uint WindowId, double X, double Y, double Width, double Height);
    /// <summary>
    /// A drag began in the window: its image (PNG at the window's backing scale; empty when there is none) drawn at
    /// <paramref name="Image"/> (window points, top-left origin) while the pointer is at (<paramref name="PointerX"/>,
    /// <paramref name="PointerY"/>); its data is on the named pasteboard <paramref name="Pasteboard"/>.
    /// </summary>
    public readonly record struct DragStartedInfo(uint WindowId, byte[] Png, (double X, double Y, double Width, double Height) Image,
        double PointerX, double PointerY, ulong Operations, string Pasteboard);

    public static int Id(ReadOnlySpan<byte> m) => BinaryPrimitives.ReadInt32LittleEndian(m[20..]);
    public static int Size(ReadOnlySpan<byte> m) => (int)BinaryPrimitives.ReadUInt32LittleEndian(m[4..]);
    /// <summary>The port name a complex message carries (Hello: Nightly's receive right; Surface: the IOSurface).</summary>
    public static uint PortName(ReadOnlySpan<byte> m) => R32(m, 28);

    public static HelloInfo ReadHello(ReadOnlySpan<byte> m) => new(R32(m, 40), (int)R32(m, 44));
    public static SurfaceInfo ReadSurface(ReadOnlySpan<byte> m) => new(R32(m, 40), R32(m, 44), (int)R32(m, 48), (int)R32(m, 52));
    public static FrameInfo ReadFrame(ReadOnlySpan<byte> m) =>
        new(R32(m, 24), R32(m, 28), R64(m, 32), R64(m, 40), (int)R32(m, 48), (int)R32(m, 52));
    public static WindowInfo ReadWindow(ReadOnlySpan<byte> m) =>
        new(R32(m, 24), R32(m, 28) != 0, RF64(m, 32), RF64(m, 40), RF64(m, 48), R32(m, 56), R32(m, 60));

    /// <summary>(window, popup) of a PopupClosed: the page closed the popup itself.</summary>
    public static (uint WindowId, uint PopupId) ReadPopupClosed(ReadOnlySpan<byte> m) => (R32(m, 24), R32(m, 28));

    /// <summary>(window, dialog) of a DialogClosed: Nightly withdrew the question (navigation, page closed, crash).</summary>
    public static (uint WindowId, uint DialogId) ReadDialogClosed(ReadOnlySpan<byte> m) => (R32(m, 24), R32(m, 28));

    /// <summary>A Dialog or Download: (window, dialog id, JSON) whose out-of-line text the channel copied into
    /// <paramref name="payload"/>; null when it does not fit. Window 0 for a download whose window is gone.</summary>
    public static (uint WindowId, uint DialogId, string Json)? ReadDialog(ReadOnlySpan<byte> m, byte[]? payload)
    {
        if (m.Length < 60) return null;
        var length = R32(m, 56);
        if (length == 0 || length > (payload?.Length ?? 0)) return null;
        return (R32(m, 44), R32(m, 48), Encoding.UTF8.GetString(payload.AsSpan(0, (int)length)));
    }

    /// <summary>(window, nsCursor) of a Cursor: the pointer shape the page wants over the window.</summary>
    public static (uint WindowId, int Cursor) ReadCursor(ReadOnlySpan<byte> m) => (R32(m, 24), (int)R32(m, 28));

    /// <summary>Where the composition's caret is (the input method's candidate window goes there).</summary>
    public static TextRectInfo ReadTextRect(ReadOnlySpan<byte> m) => new(R32(m, 24), RF64(m, 32), RF64(m, 40), RF64(m, 48), RF64(m, 56));

    /// <summary>A DragStarted whose image the channel copied into <paramref name="payload"/>; null when it does not fit.</summary>
    public static DragStartedInfo? ReadDragStarted(ReadOnlySpan<byte> m, byte[]? payload)
    {
        if (m.Length < 240) return null;
        var imageLength = R32(m, 48);
        var nameLength = R32(m, 52);
        if (imageLength > (payload?.Length ?? 0) || nameLength > 128) return null;
        return new DragStartedInfo(R32(m, 44), imageLength == 0 ? [] : payload.AsSpan(0, (int)imageLength).ToArray(),
            (RF64(m, 56), RF64(m, 64), RF64(m, 72), RF64(m, 80)), RF64(m, 88), RF64(m, 96), R64(m, 104),
            Encoding.UTF8.GetString(m.Slice(112, (int)nameLength)));
    }

    /// <summary>(window, NSDragOperation, ended) of a DragStatus: what a drop there would do; ended once the window's own
    /// drag is over.</summary>
    public static (uint WindowId, ulong Operation, bool Ended) ReadDragStatus(ReadOnlySpan<byte> m) => (R32(m, 24), R64(m, 32), (R32(m, 28) & 1) != 0);

    /// <summary>Null when the lengths do not fit the message.</summary>
    public static LocationInfo? ReadLocation(ReadOnlySpan<byte> m)
    {
        if (m.Length < 40) return null;
        var urlLength = R32(m, 32);
        var titleLength = R32(m, 36);
        if (urlLength > MaxText || titleLength > MaxText || 40 + urlLength + titleLength > m.Length) return null;
        var url = Encoding.UTF8.GetString(m.Slice(40, (int)urlLength));
        var title = Encoding.UTF8.GetString(m.Slice(40 + (int)urlLength, (int)titleLength));
        return new LocationInfo(R32(m, 24), (LocationFlags)R32(m, 28), url, title);
    }

    /// <summary>A ScriptResult whose out-of-line text the channel copied into <paramref name="payload"/>; null when it does not fit.</summary>
    public static ScriptResultInfo? ReadScriptResult(ReadOnlySpan<byte> m, byte[]? payload)
    {
        if (m.Length < 60) return null;
        var length = R32(m, 56);
        if (length > (payload?.Length ?? 0)) return null;
        var text = length == 0 ? "" : Encoding.UTF8.GetString(payload.AsSpan(0, (int)length));
        return new ScriptResultInfo(R32(m, 44), R32(m, 48), (ScriptStatus)R32(m, 52), text);
    }

    /// <summary>A Popup whose out-of-line JSON the channel copied into <paramref name="payload"/>; null when it does not fit.</summary>
    public static PopupInfo? ReadPopup(ReadOnlySpan<byte> m, byte[]? payload)
    {
        if (m.Length < 60) return null;
        var length = R32(m, 56);
        if (length == 0 || length > (payload?.Length ?? 0)) return null;
        return new PopupInfo(R32(m, 44), R32(m, 48), Encoding.UTF8.GetString(payload.AsSpan(0, (int)length)));
    }

    // ── layout ──

    private static byte[] New(int id, int size)
    {
        var m = new byte[size];
        U32(m, 0, CopySend);
        U32(m, 4, (uint)size);
        I32(m, 20, id);
        return m;
    }

    private static int Utf16(byte[] m, int offset, string s, int max)
    {
        var n = Math.Min(max, s.Length);
        // Never split a surrogate pair at the cut.
        if (n < s.Length && n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
        for (var i = 0; i < n; i++) BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(offset + 2 * i), s[i]);
        return n;
    }

    private static void U32(byte[] m, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(o), v);
    private static void I32(byte[] m, int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(o), v);
    private static void U64(byte[] m, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(m.AsSpan(o), v);
    private static void F64(byte[] m, int o, double v) => BinaryPrimitives.WriteDoubleLittleEndian(m.AsSpan(o), v);
    private static uint R32(ReadOnlySpan<byte> m, int o) => BinaryPrimitives.ReadUInt32LittleEndian(m[o..]);
    private static ulong R64(ReadOnlySpan<byte> m, int o) => BinaryPrimitives.ReadUInt64LittleEndian(m[o..]);
    private static double RF64(ReadOnlySpan<byte> m, int o) => BinaryPrimitives.ReadDoubleLittleEndian(m[o..]);
}

/// <summary>A macOS scroll event's fields (<c>NSEvent</c> scrollWheel): phases, precise (pixel) and line deltas.</summary>
/// <param name="DeltaX">scrollingDeltaX: points when <paramref name="Precise"/>, else lines.</param>
/// <param name="LineDeltaX">deltaX.</param>
/// <param name="Modifiers">NSEventModifierFlags.</param>
internal readonly record struct WheelInput(
    NightlyProtocol.Phase Phase, NightlyProtocol.Phase MomentumPhase, bool Precise,
    double DeltaX, double DeltaY, double LineDeltaX, double LineDeltaY, ulong Modifiers);
