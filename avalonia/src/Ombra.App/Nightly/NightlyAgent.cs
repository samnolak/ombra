using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ombra.ClientCore;
using Ombra.ClientCore.Browser;

namespace Ombra.App.Nightly;

/// <summary>
/// The agent's browser (<see cref="AgentBrowserBridge"/>) on a Nightly pane: scripts in the page's world and in
/// Ombra's isolated world, input Gecko treats like the user's, and screenshots of the pane's last frame. Everything
/// goes over the authenticated embed channel (<see cref="NightlyRuntime"/>): Script / ScriptResult messages for
/// scripts (run by Nightly's <c>ZenEmbedAgent</c> actor), the pane's own input messages for input, the IOSurfaces
/// Nightly already sends for screenshots. No remote debugging port, nothing another process can reach.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Page scripts answer as WKWebView's do: a string as it is, any other value as JSON, null for undefined/null; a
/// script exception is an <see cref="InvalidOperationException"/> with the page's text.</item>
/// <item>Isolated calls see the DOM through Xrays (Gecko's equivalent of a <c>WKContentWorld</c>): the page's globals
/// and patched built-ins stay out. Neither kind gives the page user activation; the page's CSP does not apply.</item>
/// <item>Input: coordinates are CSS px, which are window points in an embed window (no zoom). Mouse buttons and keys
/// become <c>NSEvent</c>s at Gecko's view in Nightly, moves without a button are hover; text is committed as an input
/// method commits it; modifiers alone are <c>flagsChanged:</c>. The user wins: a batch waits while the user's own
/// input reaches the pane (<see cref="Platform.Mac.NativeInput.UserQuiet"/>). A batch ends with a script round trip,
/// so the page has handled the input when it returns.</item>
/// <item>Screenshots: the pane's latest frame (the page's visible area at the backing scale), read from its IOSurface
/// while Nightly cannot reuse it (use count), as PNG.</item>
/// </list>
/// Any thread.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class NightlyAgent(NightlyAgent.ITarget target, NightlyFrames frames) : IIsolatedScripts, INativeInput, IAgentSpeculation
{
    /// <summary>The pane the agent drives (read from any thread).</summary>
    public interface ITarget
    {
        NightlyRuntime? Runtime { get; }
        /// <summary>The pane's Nightly window; 0 while it has none.</summary>
        uint WindowId { get; }
        /// <summary><see cref="Environment.TickCount64"/> of the user's last input to the pane; 0 for none.</summary>
        long LastUserInput { get; }
        /// <summary>The agent acts on the pane until the result is disposed: its page counts as seen meanwhile and for
        /// <see cref="Controls.NightlyPane.AgentSeen"/> after, wherever its tab or window is (not out of a window).
        /// Completes once the pane has applied that (Nightly told the page is seen), so what follows reaches a visible
        /// page. Any thread.</summary>
        Task<IDisposable> AgentActsAsync();
        /// <summary>Completes once the pane shows a frame Nightly painted since its page last came back on screen (at once when
        /// it does, or when it cannot be seen at all), or after <paramref name="timeout"/>. Any thread.</summary>
        Task ShownFrameAsync(TimeSpan timeout, CancellationToken ct);
    }

    private const string NoWindow = "Nightly has no page for this tab yet.";
    private const ulong ShiftFlag = 1UL << 17, ControlFlag = 1UL << 18, OptionFlag = 1UL << 19, CommandFlag = 1UL << 20;

    private readonly SemaphoreSlim _serial = new(1, 1);
    private ulong _heldModifiers;
    private int _heldButton = -1;

    public string? Unsupported => null;

    public bool CanHover => true;

    // ── scripts ──

    /// <summary>A script in the page's own world: its value as text (a string as it is, else JSON), null for undefined.</summary>
    public async Task<string?> RunScriptAsync(string script) => await RunPageScriptAsync(script, CancellationToken.None);

    public async Task<string?> RunPageScriptAsync(string script, CancellationToken ct)
    {
        using var acting = await target.AgentActsAsync().ConfigureAwait(false);
        var result = await RunAsync(NightlyProtocol.ScriptKind.Page, script, "", ct);
        return result.Status switch
        {
            NightlyProtocol.ScriptStatus.Value => result.Text,
            NightlyProtocol.ScriptStatus.Undefined => null,
            _ => throw new InvalidOperationException(result.Text.Length > 0 ? result.Text : NoWindow),
        };
    }

    public async Task<string?> CallAsync(string functionSource, string argsJson, CancellationToken ct)
    {
        using var acting = await target.AgentActsAsync().ConfigureAwait(false);
        NightlyProtocol.ScriptResultInfo result;
        try
        {
            result = await RunAsync(NightlyProtocol.ScriptKind.Isolated, functionSource, argsJson, ct);
        }
        catch (InvalidOperationException e)
        {
            throw new IsolatedScriptException(e.Message);
        }
        return result.Status switch
        {
            NightlyProtocol.ScriptStatus.Value => result.Text,
            NightlyProtocol.ScriptStatus.Undefined => null,
            _ => throw new IsolatedScriptException(result.Text.Length > 0 ? result.Text : NoWindow),
        };
    }

    private Task<NightlyProtocol.ScriptResultInfo> RunAsync(NightlyProtocol.ScriptKind kind, string source, string args, CancellationToken ct)
    {
        var window = target.WindowId;
        if (target.Runtime is not { } runtime || window == 0) return Task.FromException<NightlyProtocol.ScriptResultInfo>(new InvalidOperationException(NoWindow));
        return runtime.RunScriptAsync(window, kind, source, args, ct);
    }

    // ── screenshots ──

    public async Task<AgentScreenshot> CaptureAsync(CancellationToken ct)
    {
        // A page that was off screen paints again once it is seen: the screenshot is of that frame, not the one before.
        using var acting = await target.AgentActsAsync().ConfigureAwait(false);
        await target.ShownFrameAsync(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        return await frames.CaptureAsync(ct).ConfigureAwait(false);
    }

    // ── intent hints (Augur) ──

    /// <summary>Hints the page's window (0 while it has none) that the agent is about to load a URL: Nightly may warm a connection.</summary>
    public void Speculate(SpeculationHint hint) => target.Runtime?.Speculate(target.WindowId, hint);

    public void CancelSpeculation(string id) => target.Runtime?.CancelSpeculation(target.WindowId, id);

    // ── input ──

    public async Task SendAsync(IReadOnlyList<NativeInputStep> steps, CancellationToken ct)
    {
        using var acting = await target.AgentActsAsync().ConfigureAwait(false);
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitForUserAsync(ct).ConfigureAwait(false);
            // Keys and text go to the focused element of Gecko's active window: the agent's window becomes it (the
            // user's pane takes it back when they type or click there).
            if (steps.Any(s => s is NativeKeyStep or NativeTextStep or NativeMouseStep { Action: NativeMouseAction.Down })
                && target.Runtime is { } runtime && target.WindowId is var window and not 0)
                runtime.SetActive(window, true);
            foreach (var step in steps)
            {
                ct.ThrowIfCancellationRequested();
                Deliver(step);
            }
            // Nightly handles its messages in order and the page's process gets input before the script that follows:
            // when this answers, the page has seen every event of the batch.
            try { await RunAsync(NightlyProtocol.ScriptKind.Page, "0", "", ct).ConfigureAwait(false); }
            catch (InvalidOperationException) { } // between two documents: the input went out all the same
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task WaitForUserAsync(CancellationToken ct)
    {
        var started = Environment.TickCount64;
        var quietFor = Platform.Mac.NativeInput.UserQuiet;
        while (true)
        {
            var last = target.LastUserInput;
            var now = Environment.TickCount64;
            var quiet = TimeSpan.FromMilliseconds(now - last);
            if (last == 0 || quiet >= quietFor) return;
            if (TimeSpan.FromMilliseconds(now - started) > Platform.Mac.NativeInput.UserWaitLimit)
                throw new TimeoutException("The user is using this page in the preview: try again when they stop.");
            await Task.Delay(quietFor - quiet + TimeSpan.FromMilliseconds(20), ct).ConfigureAwait(false);
        }
    }

    private void Deliver(NativeInputStep step)
    {
        var window = target.WindowId;
        if (target.Runtime is not { } runtime || window == 0) throw new NotSupportedException(NoWindow);
        void Send(byte[] message)
        {
            if (!runtime.Send(message)) throw new NotSupportedException("Nightly did not take the input (not connected).");
        }
        switch (step)
        {
            case NativeMouseStep m:
            {
                var flags = Flags(m.Modifiers) | _heldModifiers;
                var button = m.Button switch { NativeMouseButton.Right => 1, NativeMouseButton.Middle => 2, _ => 0 };
                var clicks = Math.Max(1, m.Clicks);
                switch (m.Action)
                {
                    case NativeMouseAction.Move:
                        // A held button drags; without one it is hover.
                        Send(NightlyProtocol.MouseMessage(window, NightlyProtocol.MouseKind.Move, _heldButton, 0, m.X, m.Y, flags));
                        break;
                    case NativeMouseAction.Down:
                        _heldButton = button;
                        Send(NightlyProtocol.MouseMessage(window, NightlyProtocol.MouseKind.Down, button, clicks, m.X, m.Y, flags));
                        break;
                    case NativeMouseAction.Up:
                        if (_heldButton == button) _heldButton = -1;
                        Send(NightlyProtocol.MouseMessage(window, NightlyProtocol.MouseKind.Up, button, clicks, m.X, m.Y, flags));
                        break;
                }
                break;
            }
            case NativeWheelStep w:
                // DOM deltas (positive: the page scrolls down) as a pixel-precise, phaseless NSEvent (positive: content moves down).
                Send(NightlyProtocol.WheelMessage(window, w.X, w.Y, new WheelInput(NightlyProtocol.Phase.None, NightlyProtocol.Phase.None, true,
                    -w.DeltaX, -w.DeltaY, -w.DeltaX / 10, -w.DeltaY / 10, Flags(w.Modifiers) | _heldModifiers)));
                break;
            case NativeKeyStep k:
                Key(k, window, Send);
                break;
            case NativeTextStep t:
                for (var i = 0; i < t.Text.Length;)
                {
                    var n = Math.Min(64, t.Text.Length - i);
                    if (n < t.Text.Length - i && char.IsHighSurrogate(t.Text[i + n - 1])) n--; // never split a pair
                    Send(NightlyProtocol.TextMessage(window, t.Text.Substring(i, n)));
                    i += n;
                }
                break;
            default:
                throw new NotSupportedException("Unknown input step " + step.GetType().Name + ".");
        }
    }

    private void Key(NativeKeyStep k, uint window, Action<byte[]> send)
    {
        if (ModifierKey(k.Key, k.Code) is { } modifier)
        {
            _heldModifiers = k.Down ? _heldModifiers | modifier.Flag : _heldModifiers & ~modifier.Flag;
            send(NightlyProtocol.FlagsMessage(window, modifier.KeyCode, _heldModifiers | (Flags(k.Modifiers) & ~(k.Down ? 0UL : modifier.Flag))));
            return;
        }
        var flags = Flags(k.Modifiers) | _heldModifiers;
        var (keyCode, characters, unmodified, shifted) = Platform.Mac.NativeInput.Describe(k.Key);
        if (shifted) flags |= ShiftFlag;
        if ((flags & ControlFlag) != 0 && unmodified.Length == 1 && char.IsAsciiLetterLower(unmodified[0]))
            characters = ((char)(unmodified[0] - 'a' + 1)).ToString(); // what the keyboard gives for ⌃ + letter
        // A physical keyboard sends no key-up for ⌘ combinations (Nightly offers the down as a key equivalent first).
        if ((flags & CommandFlag) != 0 && !k.Down) return;
        send(NightlyProtocol.KeyMessage(window, k.Down, keyCode, characters, unmodified, flags, isRepeat: false));
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
}

/// <summary>
/// The IOSurfaces a Nightly pane got and which one holds its latest frame, for screenshots (<see cref="CaptureAsync"/>).
/// Holds its own reference to each surface. Any thread.
/// </summary>
/// <remarks>
/// Mirrors Nightly's sent-surface list (<c>WindowExporter::SendLocked</c>, <c>kMaxSentSurfaces</c> in
/// ZenEmbedHost.mm): Nightly announces a surface once, appends its id to a FIFO of <see cref="MaxSurfaces"/>, drops
/// the oldest id when full and announces a dropped surface again before it presents it again, and clears the list
/// when the size changes. Keeping the same FIFO here (in announcement order, a repeated id moved to the end, other
/// sizes dropped) holds exactly the ids Nightly counts as known, so a frame never names a surface this list let go,
/// and the pool's churn (resizes, new pools) no longer piles up surfaces. The pane's layer keeps its own reference to
/// what it shows (a CALayer's contents is retained), so dropping a surface here never frees a frame on screen.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class NightlyFrames : IDisposable
{
    /// <summary>Nightly's <c>kMaxSentSurfaces</c>; never fewer, or a frame could name a surface dropped here.</summary>
    internal const int MaxSurfaces = 16;

    private readonly Lock _gate = new();
    private readonly Dictionary<uint, (nint Surface, int Width, int Height)> _surfaces = [];
    private readonly List<uint> _order = []; // announcement order, oldest first
    private readonly Func<nint, nint> _retain;
    private readonly Action<nint> _release;
    private uint _latest;

    public NightlyFrames() : this(NightlyMach.CFRetain, NightlyMach.CFRelease) { }

    /// <summary>With these reference calls instead of CoreFoundation's (tests).</summary>
    internal NightlyFrames(Func<nint, nint> retain, Action<nint> release)
    {
        _retain = retain;
        _release = release;
    }

    /// <summary>The surfaces held now.</summary>
    internal int Count
    {
        get
        {
            lock (_gate) return _surfaces.Count;
        }
    }

    /// <summary>A surface of the window (the caller keeps its own reference); surfaces of another size, the one it
    /// replaces and the oldest past <see cref="MaxSurfaces"/> are done with.</summary>
    public void AddSurface(NightlyProtocol.SurfaceInfo info, nint surface)
    {
        var retained = _retain(surface);
        List<nint> done = [];
        lock (_gate)
        {
            if (_surfaces.Values.Any(s => s.Width != info.Width || s.Height != info.Height))
            {
                done.AddRange(_surfaces.Values.Select(s => s.Surface));
                _surfaces.Clear();
                _order.Clear();
            }
            else if (_surfaces.Remove(info.SurfaceId, out var old))
            {
                done.Add(old.Surface);
                _order.Remove(info.SurfaceId);
            }
            while (_order.Count >= MaxSurfaces)
            {
                _surfaces.Remove(_order[0], out var oldest);
                done.Add(oldest.Surface);
                _order.RemoveAt(0);
            }
            _surfaces[info.SurfaceId] = (retained, info.Width, info.Height);
            _order.Add(info.SurfaceId);
        }
        foreach (var s in done) _release(s);
    }

    public void SetLatest(NightlyProtocol.FrameInfo frame)
    {
        lock (_gate) _latest = frame.SurfaceId;
    }

    /// <summary>The surface with this id (the reference stays this list's), 0 when there is none.</summary>
    public nint Surface(uint surfaceId)
    {
        lock (_gate) return _surfaces.TryGetValue(surfaceId, out var s) ? s.Surface : 0;
    }

    public bool HasFrame
    {
        get
        {
            lock (_gate) return _surfaces.ContainsKey(_latest);
        }
    }

    /// <summary>The window is gone: its surfaces are void.</summary>
    public void Clear()
    {
        List<nint> done;
        lock (_gate)
        {
            done = [.. _surfaces.Values.Select(s => s.Surface)];
            _surfaces.Clear();
            _order.Clear();
            _latest = 0;
        }
        foreach (var s in done) _release(s);
    }

    public void Dispose() => Clear();

    /// <summary>The latest frame as PNG. "no_page" while there is none; "screenshot_failed" when it cannot be read.</summary>
    public async Task<AgentScreenshot> CaptureAsync(CancellationToken ct)
    {
        nint surface;
        lock (_gate)
        {
            if (!_surfaces.TryGetValue(_latest, out var entry))
                throw new AgentBrowserException("no_page", "The Nightly pane has not shown a frame yet.");
            surface = _retain(entry.Surface);
        }
        try
        {
            return await Task.Run(() => NightlySnapshot.Capture(surface), ct).ConfigureAwait(false);
        }
        finally
        {
            _release(surface);
        }
    }
}

/// <summary>An IOSurface (BGRA, as Gecko presents) read on the CPU and written as an opaque RGB PNG.</summary>
[SupportedOSPlatform("macos")]
internal static partial class NightlySnapshot
{
    private const string IOSurfaceLib = "/System/Library/Frameworks/IOSurface.framework/IOSurface";
    private const uint ReadOnly = 1; // kIOSurfaceLockReadOnly
    private const uint Bgra = 0x42475241; // 'BGRA'

    public static unsafe AgentScreenshot Capture(nint surface)
    {
        int width = (int)IOSurfaceGetWidth(surface), height = (int)IOSurfaceGetHeight(surface);
        var format = IOSurfaceGetPixelFormat(surface);
        if (width <= 0 || height <= 0) throw new AgentBrowserException("screenshot_failed", "The Nightly frame is empty.");
        if (format != Bgra) throw new AgentBrowserException("screenshot_failed", $"The Nightly frame has pixel format 0x{format:x8}, not BGRA.");
        var rgb = new byte[width * 3 * height];
        // While the use count is raised Nightly's surface pool does not draw into it (it is the shown frame anyway).
        IOSurfaceIncrementUseCount(surface);
        try
        {
            if (IOSurfaceLock(surface, ReadOnly, null) != 0) throw new AgentBrowserException("screenshot_failed", "The Nightly frame could not be read (IOSurfaceLock).");
            try
            {
                var bytes = (byte*)IOSurfaceGetBaseAddress(surface);
                var stride = (int)IOSurfaceGetBytesPerRow(surface);
                for (var y = 0; y < height; y++)
                {
                    var row = new ReadOnlySpan<byte>(bytes + (long)y * stride, width * 4);
                    var to = rgb.AsSpan(y * width * 3, width * 3);
                    for (int x = 0, i = 0, o = 0; x < width; x++, i += 4, o += 3)
                    {
                        to[o] = row[i + 2];
                        to[o + 1] = row[i + 1];
                        to[o + 2] = row[i];
                    }
                }
            }
            finally
            {
                IOSurfaceUnlock(surface, ReadOnly, null);
            }
        }
        finally
        {
            IOSurfaceDecrementUseCount(surface);
        }
        return new AgentScreenshot(Png(width, height, rgb), width, height);
    }

    /// <summary>An 8-bit RGB PNG of <paramref name="rgb"/> (rows top to bottom, no padding), each row Sub-filtered.</summary>
    public static byte[] Png(int width, int height, ReadOnlySpan<byte> rgb)
    {
        var rowBytes = width * 3;
        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
        {
            var line = new byte[rowBytes + 1];
            line[0] = 1; // Sub: each byte minus the one a pixel to its left
            for (var y = 0; y < height; y++)
            {
                var row = rgb.Slice(y * rowBytes, rowBytes);
                for (var i = 0; i < rowBytes; i++) line[i + 1] = (byte)(row[i] - (i >= 3 ? row[i - 3] : 0));
                z.Write(line);
            }
        }
        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; // bit depth
        header[9] = 2; // truecolour
        Chunk(png, "IHDR"u8, header);
        Chunk(png, "IDAT"u8, idat.GetBuffer().AsSpan(0, (int)idat.Length));
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        png.Write(word);
        png.Write(type);
        png.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~Crc(Crc(uint.MaxValue, type), data));
        png.Write(word);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    [LibraryImport(IOSurfaceLib)]
    private static partial nuint IOSurfaceGetWidth(nint surface);

    [LibraryImport(IOSurfaceLib)]
    private static partial nuint IOSurfaceGetHeight(nint surface);

    [LibraryImport(IOSurfaceLib)]
    private static partial nuint IOSurfaceGetBytesPerRow(nint surface);

    [LibraryImport(IOSurfaceLib)]
    private static partial uint IOSurfaceGetPixelFormat(nint surface);

    [LibraryImport(IOSurfaceLib)]
    private static partial nint IOSurfaceGetBaseAddress(nint surface);

    [LibraryImport(IOSurfaceLib)]
    private static unsafe partial int IOSurfaceLock(nint surface, uint options, uint* seed);

    [LibraryImport(IOSurfaceLib)]
    private static unsafe partial int IOSurfaceUnlock(nint surface, uint options, uint* seed);

    [LibraryImport(IOSurfaceLib)]
    private static partial void IOSurfaceIncrementUseCount(nint surface);

    [LibraryImport(IOSurfaceLib)]
    private static partial void IOSurfaceDecrementUseCount(nint surface);
}
