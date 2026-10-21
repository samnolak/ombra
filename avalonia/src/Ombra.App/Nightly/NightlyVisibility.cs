namespace Ombra.App.Nightly;

/// <summary>
/// Whether Nightly renders a pane's window (protocol 5 Visibility), and the frames the pane holds from it. The pane says
/// whether it can be seen after anything that may change that (<see cref="Update"/>); Nightly is told only when it
/// changes (and once when a window attaches), each message with a generation above every earlier one. On screen the
/// pane holds the frame it shows until a newer one replaces it, and the newest one it cannot show yet (no layer). Off
/// screen every frame it holds goes back to Nightly at once and one still in flight goes back as it comes, so a hidden
/// pane keeps none of Nightly's frame slots or surfaces; shown again, it waits for the fresh frame Nightly paints then.
/// Frames of a window that is gone are void: nothing goes back for them. A state that could not be sent (the queue full)
/// is sent again after <see cref="RetryDelay"/>, until Nightly has it or the window goes. UI thread.
/// </summary>
/// <param name="send">Sends a message to Nightly; false when it could not (not connected, queue full).</param>
/// <param name="release">Gives frame (window, seq) back to Nightly (Release).</param>
/// <param name="hide">Takes the pane's frame off the screen (the layer's contents), before Nightly is told and has its
/// frames back: Nightly may reuse a surface as soon as it is released.</param>
/// <param name="later">Runs an action on this thread after a delay.</param>
internal sealed class NightlyVisibility(Func<byte[], bool> send, Action<uint, ulong> release, Action hide, Action<TimeSpan, Action> later)
{
    /// <summary>How long a state that could not be sent waits before it is sent again.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    // The state Nightly last got for the window; null: none yet (or that message could not be sent).
    private bool? _told;
    // A retry is scheduled: it sends the state as it is then, unless Nightly has it (nothing while no window is attached).
    private bool _retrying;

    /// <summary>The pane's window; 0 while it has none.</summary>
    public uint WindowId { get; private set; }

    /// <summary>The pane can be seen, as last updated.</summary>
    public bool Visible { get; private set; }

    /// <summary>The generation of the last Visibility message (0: none yet); never repeats, for any window of the pane.</summary>
    public uint Generation { get; private set; }

    /// <summary>The frame the pane shows, held from Nightly.</summary>
    public NightlyProtocol.FrameInfo? Shown { get; private set; }

    /// <summary>The newest frame the pane could not show when it came (no layer yet), held from Nightly until it can.</summary>
    public NightlyProtocol.FrameInfo? Held { get; private set; }

    /// <summary>The pane has window <paramref name="windowId"/> now: Nightly gets whether it can be seen at once.</summary>
    public void Attach(uint windowId, bool visible)
    {
        WindowId = windowId;
        Visible = visible;
        _told = null;
        Tell();
    }

    /// <summary>The window is gone (the runtime stopped or restarted, the pane closed): its frames are void, and nothing
    /// more is said for it.</summary>
    public void Detach()
    {
        WindowId = 0;
        _told = null;
        Shown = Held = null;
    }

    /// <summary>
    /// Whether the pane can be seen now; true when that changed. Off screen, the pane's frame leaves the screen first,
    /// then Nightly is told and every frame the pane holds goes back. A message that could not be sent before is sent
    /// again.
    /// </summary>
    public bool Update(bool visible)
    {
        var changed = visible != Visible;
        Visible = visible;
        if (changed && !visible) hide();
        Tell();
        if (changed && !visible) ReleaseAll();
        return changed;
    }

    /// <summary>
    /// A frame came: true when the pane shows it now (it calls <see cref="Showing"/>, or <see cref="Release"/> when it
    /// cannot after all). A frame of another window, or one in flight while the pane is off screen, goes back at once
    /// (and a state that could not be sent before is sent now); with <paramref name="canShow"/> false it is held instead
    /// of the older held one.
    /// </summary>
    public bool Arrived(NightlyProtocol.FrameInfo frame, bool canShow)
    {
        if (Held is { } older)
        {
            Held = null;
            Release(older);
        }
        if (!Visible || frame.WindowId != WindowId || WindowId == 0)
        {
            Release(frame);
            Tell();
            return false;
        }
        if (canShow) return true;
        Held = frame;
        return false;
    }

    /// <summary>The held frame, to show now; null when there is none or the pane is off screen.</summary>
    public NightlyProtocol.FrameInfo? TakeHeld()
    {
        if (!Visible || Held is not { } frame) return null;
        Held = null;
        return frame;
    }

    /// <summary>The pane shows <paramref name="frame"/> from now on; the one it showed goes back.</summary>
    public void Showing(NightlyProtocol.FrameInfo frame)
    {
        if (Shown is { } replaced && replaced != frame) Release(replaced);
        Shown = frame;
    }

    /// <summary>Gives <paramref name="frame"/> back to Nightly (a frame the pane cannot show).</summary>
    public void Release(NightlyProtocol.FrameInfo frame) => release(frame.WindowId, frame.Seq);

    private void ReleaseAll()
    {
        if (Shown is { } shown) Release(shown);
        if (Held is { } held) Release(held);
        Shown = Held = null;
    }

    /// <summary>Tells Nightly the pane's state when it does not have it yet; when that fails, again after
    /// <see cref="RetryDelay"/> (the state then, which a later change may have replaced).</summary>
    private void Tell()
    {
        if (WindowId == 0 || _told == Visible) return;
        // A generation is used up even when the send fails: one Nightly may have seen is never sent again.
        var generation = ++Generation;
        _told = send(NightlyProtocol.VisibilityMessage(WindowId, Visible, generation)) ? Visible : null;
        if (_told is not null || _retrying) return;
        _retrying = true;
        later(RetryDelay, () =>
        {
            _retrying = false;
            Tell();
        });
    }
}
