namespace Ombra.App.Pets.Oni;

/// <summary>
/// What ONI does, from what the app tells it (the reference events of runtime-contract.json): one <see cref="OniDirector"/>
/// on the app's clock. Every view of the pet steps the same director (<see cref="Sync"/>), so the perch, the desktop
/// window and the settings never disagree, and the semantic time (a wince's 2.4 s, falling asleep after 3 minutes)
/// runs on while no view draws. The agent's state changes many times a second (a blink of idle between two tools,
/// thinking and working in turns): <see cref="SetBase"/> passes a change on only once it has held for a moment, as the
/// earlier pets did; a question or omp stopping goes through at once, and so do the run's end, a failed tool, a cancel.
/// </summary>
public sealed class OniPet
{
    public static readonly TimeSpan MinShow = TimeSpan.FromMilliseconds(700);
    public static readonly TimeSpan WithinBusy = TimeSpan.FromMilliseconds(600);

    private PetMood _candidate = PetMood.Idle, _forwarded = PetMood.Idle;
    private DateTimeOffset _candidateSince, _familySince, _forwardedAt, _last;

    public OniPet(OniSpec spec, TimeProvider clock)
    {
        Spec = spec;
        Clock = clock;
        Director = new OniDirector(spec.States.ToDictionary(s => s.Key, s => s.Value.Clip));
        var now = clock.GetUtcNow();
        _last = now;
        _candidateSince = _familySince = _forwardedAt = now - MinShow;
    }

    public OniSpec Spec { get; }
    public TimeProvider Clock { get; }
    public OniDirector Director { get; }

    /// <summary>Something changed what the pet shows: its mood, a reaction, being carried.</summary>
    public event Action? Changed;

    /// <summary>Brings the director to now: the clock fully, the animation by at most 0.1 s.</summary>
    public void Sync()
    {
        var now = Clock.GetUtcNow();
        var dt = (now - _last).TotalSeconds;
        if (dt <= 0) return;
        _last = now;
        var cue = Director.Cue;
        var mood = Mood;
        Director.Step(dt);
        Forward(now);
        if (Mood != mood || Director.Cue != cue) Changed?.Invoke();
    }

    /// <summary>The pose being asked for as one of the app's moods (carried: what it will be when put down).</summary>
    public PetMood Mood => Director.Target(ignoreCarry: true) switch
    {
        "thinking" => PetMood.Thinking,
        "working" => PetMood.Working,
        "waiting" => PetMood.Waiting,
        "joy" => PetMood.Done,
        "error" => PetMood.Error,
        "sleeping" => PetMood.Sleeping,
        _ => PetMood.Idle,
    };

    private static bool Busy(PetMood m) => m is PetMood.Thinking or PetMood.Working;
    private static int Family(PetMood m) => Busy(m) ? -1 : (int)m;

    /// <summary>What the agent does: Idle, Thinking, Working, Waiting, or Error (omp stopped).</summary>
    public void SetBase(PetMood mood)
    {
        Sync();
        var now = Clock.GetUtcNow();
        if (mood != _candidate)
        {
            if (Family(mood) != Family(_candidate)) _familySince = now;
            _candidate = mood;
            _candidateSince = now;
        }
        if (Forward(now)) Changed?.Invoke();
    }

    /// <summary>When the wanted base may replace the one passed on.</summary>
    private DateTimeOffset ReadyAt()
    {
        if (_candidate is PetMood.Waiting or PetMood.Error) return _candidateSince;
        var settled = Busy(_forwarded) && Busy(_candidate) ? _candidateSince + WithinBusy
            : _familySince + (_candidate == PetMood.Idle ? TimeSpan.FromMilliseconds(600) : TimeSpan.FromMilliseconds(350));
        var shownLongEnough = _forwardedAt + MinShow;
        return settled > shownLongEnough ? settled : shownLongEnough;
    }

    private bool Forward(DateTimeOffset now)
    {
        if (_candidate == _forwarded || now < ReadyAt()) return false;
        _forwarded = _candidate;
        _forwardedAt = now;
        Director.Event(_candidate switch
        {
            PetMood.Thinking => "thinking",
            PetMood.Working => "working",
            PetMood.Waiting => "waiting",
            PetMood.Error => "error",
            _ => "idle",
        });
        return true;
    }

    /// <summary>A run finished (not stopped): joy, then what comes next.</summary>
    public void Done() => Reaction("done");

    /// <summary>A tool failed: a 2.4 s wince that a question or omp stopping still wins over.</summary>
    public void ToolFailed() => Reaction("tool-error");

    /// <summary>The run was stopped: back to idle, no joy.</summary>
    public void Aborted() => Reaction("abort");

    /// <summary>The user typed (the message box, the pet's own): awake and attentive.</summary>
    public void Typed() => Reaction("type");

    /// <summary>The pet was clicked.</summary>
    public void Clicked() => Reaction("click");

    /// <summary>Asleep at once (the settings' preview).</summary>
    public void Sleep() => Reaction("sleep");

    /// <summary>Picked up, or put down: carried is shown over whatever the pet is doing, which resumes on landing.</summary>
    public void Carry(bool carried)
    {
        if (carried == Director.Carried) return;
        Reaction(carried ? "drag-start" : "land");
    }

    /// <summary>
    /// The settings' preview: <paramref name="kind"/> (a reference event: idle, thinking, working, waiting, error,
    /// done, tool-error, abort, sleep) at once, without waiting for a mood to hold.
    /// </summary>
    public void Show(string kind)
    {
        Sync();
        // A finished run and a failed tool show over work; a question or omp stopping would hide them
        if (kind is "done" or "tool-error" && Director.Base is "waiting" or "error") Director.Event("idle");
        Director.Event(kind);
        _candidate = _forwarded = Director.Base switch
        {
            "thinking" => PetMood.Thinking,
            "working" => PetMood.Working,
            "waiting" => PetMood.Waiting,
            "error" => PetMood.Error,
            _ => PetMood.Idle,
        };
        _forwardedAt = _candidateSince = _familySince = Clock.GetUtcNow();
        Changed?.Invoke();
    }

    /// <summary>The pointer is over the pet: it notices (and wakes).</summary>
    public void Hover(bool over)
    {
        Sync();
        Director.Hover = over;
        if (!over) return;
        var cue = Director.Cue;
        Director.Notice();
        Director.Step(0);
        if (Director.Cue != cue) Changed?.Invoke();
    }

    private void Reaction(string kind)
    {
        Sync();
        var now = Clock.GetUtcNow();
        Director.Event(kind);
        if (Director.Base == "idle" && _forwarded != PetMood.Idle)
        {
            // The reaction put the director's base back to idle (a finished or stopped run): the gate has passed that on
            _forwarded = PetMood.Idle;
            _forwardedAt = now;
        }
        // A base that waited for the gate may follow once its time comes
        Forward(now);
        Changed?.Invoke();
    }

    /// <summary>The next moment the mood could change without new input, for a one-shot timer (null: none).</summary>
    public DateTimeOffset? NextChange()
    {
        var now = Clock.GetUtcNow();
        DateTimeOffset? next = null;
        void Consider(DateTimeOffset t)
        {
            if (t > now && (next is null || t < next)) next = t;
        }
        if (_candidate != _forwarded) Consider(ReadyAt());
        var d = Director;
        if (d.FlashUntil is { } until) Consider(now + TimeSpan.FromSeconds(Math.Max(0, until - d.Clock)));
        if (d.Base == "idle" && d.FlashUntil is null && d.Target(ignoreCarry: true) != "sleeping")
            Consider(now + TimeSpan.FromSeconds(Math.Max(0, d.LastActive + OniDirector.SleepAfter - d.Clock)));
        return next;
    }
}
