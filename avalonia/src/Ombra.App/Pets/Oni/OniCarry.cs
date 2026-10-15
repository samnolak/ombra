namespace Ombra.App.Pets.Oni;

/// <summary>
/// Carrying ONI with the pointer and putting it down (interaction-preview.js's stage, in the host's units: logical
/// pixels of the window, or of the desktop at the grab's scale). <see cref="Body"/> is the point between the feet. Held,
/// the body hangs a little above where it is held (<see cref="Lift"/>) and follows on the reference's stiff spring,
/// swinging from the nape; let go, it keeps its speed and lean, falls that lift onto the spot under it and lands with
/// dust, sliding to a stop. The director is told when it is picked up and when it touches down. Reduced motion: it
/// follows the pointer exactly and is put down at once, upright.
/// </summary>
public sealed class OniCarry
{
    /// <summary>Above this landing speed (logical px/s) the landing raises dust (interaction-preview.js).</summary>
    public const double DustSpeed = 160;
    /// <summary>How long the dust shows (seconds).</summary>
    public const double DustFor = .85;

    private readonly OniPet _pet;
    private readonly OniDragBody _body;
    private readonly bool _reduced;
    private readonly double _lift;
    private double _floor;
    private int _impacts;
    private double _landX, _dustAge = double.PositiveInfinity;
    private bool _settled;

    /// <param name="pet">Told when it is picked up and put down.</param>
    /// <param name="body">Where the body is now.</param>
    /// <param name="size">The pet's height.</param>
    /// <param name="bounds">Where the body may go (left, top, right, floor).</param>
    /// <param name="reduced">No swing, no fall: it follows and is put down at once.</param>
    public OniCarry(OniPet pet, Avalonia.Point body, double size, Avalonia.Rect bounds, bool reduced)
    {
        _pet = pet;
        _reduced = reduced;
        _lift = reduced ? 0 : size * .22;
        _body = new OniDragBody(body.X, body.Y, bounds.Left, bounds.Right, bounds.Top, bounds.Bottom) { Length = size * .4 };
        _floor = bounds.Bottom;
        Size = size;
    }

    public double Size { get; }

    /// <summary>The point between the feet.</summary>
    public Avalonia.Point Body => new(_body.X, _body.Y);

    /// <summary>The lean from the nape (radians; +: feet to the left).</summary>
    public double Angle => _reduced ? 0 : _body.Angle;

    public bool Held => _body.Held;

    /// <summary>Held, or still in the air.</summary>
    public bool Carried => _body.Held || _body.Air;

    /// <summary>Seconds since a landing that raised dust (infinity: none), and where it was from the body now.</summary>
    public double DustAge => _dustAge;
    public double DustDx => _landX - _body.X;

    /// <summary>How far the ground (where it will land: under the pointer while held, the spot it was let go over after) is below the body.</summary>
    public double GroundDy => Math.Max(0, (_body.Held ? Math.Min(_body.TargetY + _lift, _floor) : _body.Floor) - _body.Y);

    /// <summary>Picked up, swung, falling or sliding, or the dust still settling: frames are needed.</summary>
    public bool Moving => !_settled;

    /// <summary>It came to rest after being put down: where it is now is where it stays.</summary>
    public event Action? Settled;

    /// <summary>The body moved: the host puts the pet there.</summary>
    public event Action? Moved;

    /// <summary>The pointer at <paramref name="pointer"/> takes the body (the drag began).</summary>
    public void Grab(Avalonia.Point pointer)
    {
        _settled = false;
        _body.Grab(pointer.X, pointer.Y);
        _body.Floor = _floor;
        Move(pointer);
        _pet.Carry(true);
    }

    public void Move(Avalonia.Point pointer)
    {
        if (!_body.Held) return;
        _body.Move(pointer.X, pointer.Y - _lift);
        if (!_reduced) return;
        (_body.X, _body.Y) = (_body.TargetX, _body.TargetY);
        Moved?.Invoke();
    }

    /// <summary>Let go (released, cancelled, or the pointer taken away): it comes down onto the spot under it.</summary>
    public void Release()
    {
        if (!_body.Held) return;
        _body.Release();
        _body.Floor = Math.Clamp(_body.TargetY + _lift, _body.Top, _floor);
        if (!_reduced) return;
        _body.Y = _body.Floor;
        _body.Air = false;
        Moved?.Invoke();
        Land();
    }

    /// <summary>Advances by <paramref name="dt"/> seconds of wall time.</summary>
    public void Step(double dt)
    {
        if (_settled || dt <= 0) return;
        if (!_reduced)
        {
            _body.Step(dt);
            Moved?.Invoke();
        }
        if (_body.Impacts != _impacts)
        {
            _impacts = _body.Impacts;
            if (_body.ImpactSpeed > DustSpeed)
            {
                _dustAge = 0;
                _landX = _body.X;
            }
        }
        else if (double.IsFinite(_dustAge)) _dustAge += Math.Min(dt, .1);
        if (!_body.Held && !_body.Air && _pet.Director.Carried) Land();
        if (!_settled && !_body.Held && !_body.Air && Math.Abs(_body.Vx) < 1 && Math.Abs(_body.Angle) < .002 && Math.Abs(_body.Av) < .02
            && !(_dustAge < DustFor))
        {
            _settled = true;
            _dustAge = double.PositiveInfinity;
            Settled?.Invoke();
        }
    }

    private void Land()
    {
        _pet.Carry(false);
        if (!_reduced) return;
        _settled = true;
        Settled?.Invoke();
    }
}
