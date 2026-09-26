using OmpGui.ClientCore;

namespace OmpGui.App.Pets;

/// <summary>
/// Turns what the agent does into the mood the pet shows, without flicker. The agent's state changes many times a
/// second (thinking, a tool, thinking again, a blink of "idle" between two tools); the pet shows a mood only once it
/// has held for a moment, keeps each mood on screen for at least <see cref="MinShow"/>, and treats thinking and working
/// as one busy family (a busy pet does not jump between them faster than <see cref="WithinBusy"/>). A question or an
/// error shows at once; so does waking up. Short reactions (done, a failed tool) are flashes that end by themselves.
/// After <see cref="SleepAfter"/> without anything happening the pet falls asleep. Pure: the time is passed in.
/// </summary>
public sealed class PetMoodMachine
{
    public static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan MinShow = TimeSpan.FromMilliseconds(700);
    public static readonly TimeSpan WithinBusy = TimeSpan.FromMilliseconds(600);
    public static readonly TimeSpan DoneFor = TimeSpan.FromSeconds(2.4);
    public static readonly TimeSpan OopsFor = TimeSpan.FromSeconds(2.4);

    /// <summary>How long a mood must be wanted before it shows.</summary>
    public static TimeSpan Settle(PetMood m) => m switch
    {
        PetMood.Waiting or PetMood.Error or PetMood.Done or PetMood.Sleeping => TimeSpan.Zero,
        PetMood.Idle => TimeSpan.FromMilliseconds(600),
        _ => TimeSpan.FromMilliseconds(350),
    };

    private PetMood _base = PetMood.Idle;
    private PetMood _candidate = PetMood.Idle;
    private DateTimeOffset _familySince, _exactSince, _shownSince, _lastActive;
    private (PetMood Mood, DateTimeOffset Until)? _flash;

    public PetMoodMachine(DateTimeOffset now) => _familySince = _exactSince = _shownSince = _lastActive = now;

    /// <summary>The mood on screen.</summary>
    public PetMood Shown { get; private set; } = PetMood.Idle;

    /// <summary>The flash being shown or pending (Done, or Error for a failed tool), if any.</summary>
    public PetMood? Flashing => _flash?.Mood;

    private static bool Busy(PetMood m) => m is PetMood.Thinking or PetMood.Working;

    private static int Family(PetMood m) => Busy(m) ? -1 : (int)m;

    /// <summary>What the agent does now: Idle, Thinking, Working, Waiting, or Error (omp stopped). True when the shown mood changed.</summary>
    public bool SetBase(PetMood mood, DateTimeOffset now)
    {
        _base = mood;
        return Advance(now);
    }

    /// <summary>A short reaction (a run finished, a tool failed) shown for <paramref name="duration"/>.</summary>
    public bool Flash(PetMood mood, TimeSpan duration, DateTimeOffset now)
    {
        _flash = (mood, now + duration);
        _lastActive = now;
        return Advance(now);
    }

    /// <summary>The user is here (typed, clicked the pet): not sleepy any more; a sleeping pet wakes up.</summary>
    public bool Poke(DateTimeOffset now)
    {
        _lastActive = now;
        return Advance(now);
    }

    private PetMood Target(DateTimeOffset now)
    {
        if (_flash is { } f && now >= f.Until) _flash = null;
        if (_base is PetMood.Error or PetMood.Waiting) return _base;
        if (_flash is { } active) return active.Mood;
        if (_base != PetMood.Idle) return _base;
        return now - _lastActive >= SleepAfter ? PetMood.Sleeping : PetMood.Idle;
    }

    /// <summary>Re-evaluates at <paramref name="now"/>; true when the shown mood changed.</summary>
    public bool Advance(DateTimeOffset now)
    {
        // A flash that ended since the last look: what comes after it is wanted since its end, not since now
        var changed = _flash is { } f && now > f.Until && Step(f.Until, now);
        return Step(now, now) | changed;
    }

    /// <summary>The target as of <paramref name="at"/>, switched to (on screen: at <paramref name="now"/>) once ready.</summary>
    private bool Step(DateTimeOffset at, DateTimeOffset now)
    {
        if (_base != PetMood.Idle) _lastActive = now;
        var target = Target(at);
        if (target != _candidate)
        {
            if (Family(target) != Family(_candidate)) _familySince = at;
            _exactSince = at;
            _candidate = target;
        }
        if (_candidate == Shown || now < ReadyAt()) return false;
        Shown = _candidate;
        _shownSince = now;
        return true;
    }

    /// <summary>When the candidate may replace the shown mood.</summary>
    private DateTimeOffset ReadyAt()
    {
        // Waking up (or falling asleep) needs no patience
        if (Shown == PetMood.Sleeping || _candidate == PetMood.Sleeping) return _exactSince;
        var settled = Busy(Shown) && Busy(_candidate) ? _exactSince + WithinBusy : _familySince + Settle(_candidate);
        var shownLongEnough = _shownSince + MinShow;
        return settled > shownLongEnough ? settled : shownLongEnough;
    }

    /// <summary>The next moment the shown mood could change without new input (for a one-shot timer), or null.</summary>
    public DateTimeOffset? NextChange(DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        void Consider(DateTimeOffset t) { if (t > now && (next is null || t < next)) next = t; }
        if (_candidate != Shown) Consider(ReadyAt());
        if (_flash is { } f) Consider(f.Until);
        if (_base == PetMood.Idle && _flash is null && Shown != PetMood.Sleeping) Consider(_lastActive + SleepAfter);
        return next;
    }

    /// <summary>
    /// The agent's state in a snapshot, as a base mood: omp stopped → Error; a question or approval open → Waiting;
    /// a tool running → Working; the model thinking or writing → Thinking; otherwise Idle.
    /// </summary>
    public static PetMood BaseMood(SessionSnapshot s, SessionPhase phase, bool hasDialog)
    {
        if (phase == SessionPhase.Faulted) return PetMood.Error;
        if (hasDialog || s.Dialogs.Count > 0) return PetMood.Waiting;
        if (phase is not (SessionPhase.Running or SessionPhase.Aborting)) return PetMood.Idle;
        for (var i = s.Items.Count - 1; i >= 0; i--)
        {
            switch (s.Items[i])
            {
                case ToolItem { Status: ToolStatus.Running }: return PetMood.Working;
                case AssistantItem { Streaming: true }: return PetMood.Thinking;
                case UserItem: return PetMood.Thinking;
            }
        }
        return PetMood.Working;
    }
}
