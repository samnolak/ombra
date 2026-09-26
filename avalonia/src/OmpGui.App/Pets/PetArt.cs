namespace OmpGui.App.Pets;

/// <summary>A few pixels written over a body (<see cref="PetBody.Twitch"/>): <c>.</c> clears one, other characters are roles.</summary>
public sealed record PetPatch(int X, int Y, string Pixels);

/// <summary>
/// A pet's body: a 24×24 pixel grid whose characters are palette roles, plus where its face, hat and scarf go.
/// Roles: <c>.</c> transparent, <c>o</c> outline, <c>h</c> highlight and <c>1 2 3</c> coat light / mid / shadow and
/// <c>d</c> deep shadow (the part a coat colour changes; <c>h</c> and <c>d</c> are mixed from the ramp), <c>4 5</c>
/// secondary light / shadow (belly, muzzle, shell, screen), <c>6 7</c> accent, <c>p</c> blush. The face is not in the
/// grid: <see cref="PetFrames"/> draws the eyes (<c>e</c> eye, <c>w</c> glint) per moment at <see cref="Eyes"/> and the
/// mouth (<c>m</c> line, <c>n</c> inside, <c>t</c> tongue) at <see cref="Mouth"/>. The feet stand on row 22; row 23 is
/// left for the contact shadow.
/// </summary>
/// <param name="Eyes">Top-left pixel of each 2×3 eye.</param>
/// <param name="Mouth">Centre column and top row of the mouth (null: no mouth, e.g. a beak).</param>
/// <param name="IdleMouth">The mouth at rest.</param>
/// <param name="Hat">Centre column of the head and the row of its top outline (a hat is worn over it).</param>
/// <param name="Neck">Top row of the two rows a scarf covers.</param>
/// <param name="Twitch">Pixels changed for a twitch (an ear flick, a lifted foot, a blinking antenna).</param>
/// <param name="Floats">Hovers instead of standing (a ghost): bobs in the air over a fainter shadow.</param>
public sealed record PetBody(
    string Id,
    string Species,
    string DefaultName,
    string[] Rows,
    IReadOnlyDictionary<char, uint> Palette,
    (int X, int Y)[] Eyes,
    (int X, int Y)? Mouth,
    MouthStyle IdleMouth,
    (int X, int Y) Hat,
    int Neck,
    PetPatch[] Twitch,
    string[] Quips,
    bool Floats = false);

/// <summary>A coat colour: replaces the body's coat ramp (roles 1–3, and the highlight and deep shadow mixed from it) and its outline.</summary>
public sealed record PetCoat(string Id, string Name, uint Light, uint Mid, uint Shadow, uint Outline)
{
    public uint Swatch => Mid;
}

/// <summary>Something a pet wears, drawn over the body after the face.</summary>
public sealed record PetAccessory(string Id, string Name);

/// <summary>How a pet's eyes look in one frame. Patterns: <c>e</c> eye, <c>w</c> glint, <c>_</c> leave the body pixel.</summary>
public sealed record EyeStyle(string Id, string[] Left, string[] Right, int LeftDx = 0, int RightDx = 0, int Dy = 0)
{
    /// <summary>Open eyes that can look around (glances, the pointer) and blink.</summary>
    public bool CanLook => Id is "open" or "wide" or "half" or "up";
}

/// <summary>A mouth, centred on <see cref="PetBody.Mouth"/>. Patterns: <c>m</c> line, <c>n</c> inside, <c>t</c> tongue.</summary>
public sealed record MouthStyle(string Id, string[] Rows);

/// <summary>
/// All pixel data of the pets: the bodies, eyes, mouths, coats, accessories and the little effects (Zzz, thought
/// cloud, "!", sparkles, gear, sweat drop, heart). Pure data: <see cref="PetFrames"/> composes it, <see cref="PetLife"/>
/// animates it.
/// </summary>
public static class PetArt
{
    public const int BodySize = 24;

    static uint C(uint rgb) => 0xFF000000u | rgb;

    static Dictionary<char, uint> Pal(params (char Role, uint Rgb)[] roles) => roles.ToDictionary(r => r.Role, r => C(r.Rgb));

    // ───────────────────────────── Eyes ─────────────────────────────

    public static readonly EyeStyle Open = new("open", ["we", "ee", "ee"], ["we", "ee", "ee"]);
    /// <summary>Half shut: the middle of a blink, and a focused look while working.</summary>
    public static readonly EyeStyle Half = new("half", ["__", "ee", "ee"], ["__", "ee", "ee"]);
    public static readonly EyeStyle Closed = new("closed", ["__", "__", "ee"], ["__", "__", "ee"]);
    /// <summary>Content, asleep: ‿ ‿ (one pixel wider each side than an eye).</summary>
    public static readonly EyeStyle Sleep = new("sleep", ["e__e", "_ee_"], ["e__e", "_ee_"], -1, -1, 1);
    /// <summary>Delighted: ^ ^.</summary>
    public static readonly EyeStyle Happy = new("happy", ["_ee_", "e__e"], ["_ee_", "e__e"], -1, -1, 1);
    public static readonly EyeStyle LookUp = new("up", ["we", "ee", "__"], ["we", "ee", "__"], Dy: -1);
    public static readonly EyeStyle Wide = new("wide", ["wee", "eee", "eew"], ["wee", "eee", "eew"], -1, 0);
    public static readonly EyeStyle Squint = new("squint", ["e__", "_ee", "e__"], ["__e", "ee_", "__e"], -1, 0);

    // ───────────────────────────── Mouths ─────────────────────────────

    public static readonly MouthStyle Smile = new("smile", ["m..m", ".mm."]);
    public static readonly MouthStyle SmallMouth = new("small", [".mm."]);
    public static readonly MouthStyle Grin = new("grin", ["mmmm", "mnnm", ".tt."]);
    public static readonly MouthStyle Oh = new("oh", [".mm.", "mnnm", ".mm."]);
    public static readonly MouthStyle Wavy = new("wavy", [".m.m", "m.m."]);

    // ───────────────────────────── Bodies ─────────────────────────────

    public static readonly PetBody Pi = new("pi", "omp's own", "Pi",
    [
        "........................",
        "........................",
        "........................",
        "..oooooooooooooooooooo..",
        ".o1hh22222222222222223o.",
        ".o1h222222222222222223o.",
        ".o12222222222222222223o.",
        ".o12222222222222222223o.",
        ".o12222222222222222223o.",
        ".o12pp222222222222pp33o.",
        ".o12222222222222222223o.",
        ".o33333333333333333333o.",
        "..ooo1223oooooo3223ooo..",
        "....o1223o....o3223o....",
        "....o1223o....o3223o....",
        "....o1223o....o3223o....",
        "....o1223o....o3223o....",
        "....o1223o....o3223o....",
        "....o1223o....o3223o....",
        "....o1223o....o3223o....",
        "..oo112223o..o322233oo..",
        "..o3333333o..o3333333o..",
        "..oooooooo....oooooooo..",
        "........................",
    ],
    Pal(('o', 0x14285C), ('1', 0x86B6FF), ('2', 0x3D7EF5), ('3', 0x2459C9), ('4', 0xFFFFFF), ('5', 0xD6E4FF),
        ('6', 0xFFD23F), ('7', 0xE0A21A), ('p', 0xFF9DC0), ('e', 0x0E1D45), ('w', 0xFFFFFF)),
    [(5, 6), (17, 6)], (12, 9), Smile, (12, 3), 12,
    [new(2, 21, "oooooooo"), new(2, 22, "........")],
    ["Ready!", "Oh my π!", "Ready when you are.", "Pi, at your service."]);

    public static readonly PetBody Cat = new("cat", "Cat", "Mochi",
    [
        "........................",
        "........................",
        "...oo..............oo...",
        "...o1o............o3o...",
        "...o16o..........o63o...",
        "..o1166o........o6633o..",
        "..o1h662oooooooo26633o..",
        "..o1hh222222222222223o..",
        ".o1h222222222222222233o.",
        ".o12222222222222222233o.",
        ".o12222222222222222233o.",
        ".o12222222222222222233o.",
        ".o12222222222222222233o.",
        ".o12pp22244774422pp233o.",
        "..o222224444444422333o..",
        "...o3322244444423333o...",
        "....o1dddddddddddd3o.oo.",
        "....o12244444444233oo12o",
        "....o12444444444423oo23o",
        "....o12444444444433oo23o",
        "....o22444444444433o223o",
        "....o2o445o33o445o3d23o.",
        "....ooooooooooooooooooo.",
        "........................",
    ],
    Pal(('o', 0x4A2A1A), ('1', 0xFFC98A), ('2', 0xF5A25D), ('3', 0xD27A3E), ('4', 0xFFF3E2), ('5', 0xEBCFB2),
        ('6', 0xFF9FB2), ('7', 0xFF7F96), ('p', 0xFF8FA3), ('e', 0x2A1A12), ('w', 0xFFFFFF)),
    [(6, 10), (16, 10)], (12, 14), Smile, (12, 6), 16,
    [new(3, 2, ".."), new(3, 3, "ooo")],
    ["Purr…", "Mrrp? Ready when you are.", "*stretches*", "I was not asleep."]);

    public static readonly PetBody Owl = new("owl", "Owl", "Quill",
    [
        "........................",
        "........................",
        "...oo..............oo...",
        "...o1o............o3o...",
        "...o11oooooooooooo33o...",
        "..o1h1222222222222323o..",
        ".o1h222222222222222223o.",
        ".o12244442222224444223o.",
        ".o12444444222244444423o.",
        ".o12444444222244444423o.",
        ".o12444444266244444423o.",
        ".o12244442266224444223o.",
        ".o12222222277222222223o.",
        "o12d1222222222222223d23o",
        "o12d1224444444444223d23o",
        "o22d1245454444545423d22o",
        "o22d1244444444444423d22o",
        "o23d1254545445454523d32o",
        "o33d1244444444444423d33o",
        ".o3d2245454444545422d3o.",
        "..od2224444444444222do..",
        "...oo33333333333333oo...",
        ".....oo676oooo676oo.....",
        "........................",
    ],
    Pal(('o', 0x33264A), ('1', 0xB58BD8), ('2', 0x8E63B8), ('3', 0x6A4591), ('4', 0xFFF8F0), ('5', 0xD9CCE8),
        ('6', 0xFFB23E), ('7', 0xE08A12), ('p', 0xFF9DB6), ('e', 0x2A1A2E), ('w', 0xFFFFFF)),
    [(6, 8), (16, 8)], null, Smile, (12, 4), 13,
    [new(3, 2, ".."), new(3, 3, "ooo")],
    ["Hoo? Ask me anything.", "Wise choice.", "*ruffles feathers*", "I read the docs so you don't have to."]);

    public static readonly PetBody Robot = new("robot", "Robot", "Servo",
    [
        "...........oo...........",
        "..........o66o..........",
        "...........oo...........",
        "..oooooooooooooooooooo..",
        ".o1hh11111111111111123o.",
        ".o1h222222222222222223o.",
        ".o12oooooooooooooooo23o.",
        ".o12o44555555555555o23o.",
        ".o12o45555555555555o23o.",
        "7o12o55555555555555o23o7",
        "7o12o55555555555555o23o7",
        "7o12o55555555555555o23o7",
        ".o12o5pp55555555pp5o23o.",
        ".o12o55555555555555o23o.",
        ".o12oooooooooooooooo23o.",
        ".o32222222222222222223o.",
        "..o333333333333333333o..",
        "...oooooooooooooooooo...",
        "....ooo1222222223ooo....",
        "....o2o1222662223o2o....",
        "....o3o1222772223o3o....",
        ".....oo1333333333oo.....",
        "......oooooooooooo......",
        "........................",
    ],
    Pal(('o', 0x1F2733), ('1', 0xE6EDF3), ('2', 0xB8C6D2), ('3', 0x8395A6), ('4', 0x3A4E63), ('5', 0x22303F),
        ('6', 0xFF6B6B), ('7', 0xC23838), ('p', 0xFF7FA0), ('e', 0x6CF0C2), ('w', 0xE6FFF6), ('m', 0x6CF0C2), ('n', 0x2E8F74), ('t', 0xFF7FA0)),
    [(7, 8), (15, 8)], (12, 11), Smile, (12, 3), 18,
    [new(11, 1, "ww")],
    ["Beep boop. Ready.", "All systems nominal.", "Awaiting input.", "01101000 01101001"]);

    public static readonly PetBody Slime = new("slime", "Slime", "Gloop",
    [
        "........................",
        "........................",
        "........................",
        "........................",
        "........................",
        "........................",
        "........................",
        "........oooooooo........",
        "......oo1hh22223oo......",
        ".....o1hhh22222223o.....",
        "....o1hh22222222223o....",
        "....o1h222222222223o....",
        "...o1222222222222223o...",
        "...o1222222222222223o...",
        "...o1222222222222223o...",
        "..o122222222222222223o..",
        "..o12pp2222222222pp23o..",
        "..o122222222222222223o..",
        ".o12222222222222222223o.",
        ".o12222222222222222223o.",
        ".o22222222222222222222o.",
        ".o33333333333333333333o.",
        ".oooooooooooooooooooooo.",
        "........................",
    ],
    Pal(('o', 0x1D4A2E), ('1', 0xB6F5A5), ('2', 0x6FD86A), ('3', 0x44B252), ('4', 0xF2FFEE), ('5', 0x2F8A3E),
        ('6', 0xFF8FA3), ('7', 0xD9667D), ('p', 0xFF9DAE), ('e', 0x15291D), ('w', 0xFFFFFF)),
    [(7, 13), (15, 13)], (12, 16), Smile, (12, 7), 17,
    [new(8, 9, "w"), new(15, 17, "w")],
    ["Bloop!", "*wobbles*", "Squish. Ready.", "I'm mostly water and optimism."]);

    public static readonly PetBody Cactus = new("cactus", "Cactus", "Prickle",
    [
        "..........6..6..........",
        ".........667766.........",
        "..........6..6..........",
        "........oooooooo........",
        "......oo1hh22223oo......",
        ".....o1hh222222223o.....",
        ".....o1h2222222223o.....",
        ".....o122222222223o.....",
        ".oo..o122222222223o..oo.",
        "o12o.o122222222223o.o23o",
        "o12o.o122222222223o.o23o",
        "o12ooopp22222222ppooo23o",
        "o1222d122222222223d2223o",
        "o3333d122222222223d3333o",
        ".ooooo122222222223ooooo.",
        ".....o122222222223o.....",
        ".....o233333333332o.....",
        "...oooooooooooooooooo...",
        "...o4444444444444444o...",
        "...o5555555555555555o...",
        "....o44444444444444o....",
        "....o44444444444444o....",
        ".....oooooooooooooo.....",
        "........................",
    ],
    Pal(('o', 0x1E3D2A), ('1', 0x9BDB7A), ('2', 0x5FB65A), ('3', 0x3E8A44), ('4', 0xE88A5A), ('5', 0xB45733),
        ('6', 0xFF7FB0), ('7', 0xFFE27A), ('p', 0xFF9DAE), ('e', 0x1B2A1E), ('w', 0xFFFFFF)),
    [(8, 8), (14, 8)], (12, 12), SmallMouth, (12, 3), 15,
    [new(9, 0, "..6..6"), new(9, 1, ".667766"), new(9, 2, "..6..6")],
    ["Watered and ready.", "No hugs, but I'm here.", "*sways*", "I thrive on neglect."]);

    public static readonly PetBody Ghost = new("ghost", "Ghost", "Wisp",
    [
        "........................",
        "........................",
        "........................",
        ".......oooooooooo.......",
        ".....oo1hh2222223oo.....",
        "....o1hh22222222223o....",
        "...o1h22222222222223o...",
        "...o1222222222222223o...",
        "..o122222222222222223o..",
        "..o122222222222222223o..",
        "..o122222222222222223o..",
        "..o122222222222222223o..",
        "..o122222222222222223o..",
        ".oo12pp2222222222pp23oo.",
        "o1d122222222222222223d3o",
        ".oo122222222222222223oo.",
        "..o122222222222222223o..",
        "..o122222222222222223o..",
        "..o122222222222222223o..",
        "..o122222222222222223o..",
        "..o222222222222222222o..",
        "..o3333o33333333o3333o..",
        "...oooo.oooooooo.oooo...",
        "........................",
    ],
    Pal(('o', 0x3A3560), ('1', 0xFFFFFF), ('2', 0xEEECFF), ('3', 0xCBC6F4), ('4', 0xA7A0E6), ('5', 0x8C84D9),
        ('6', 0xFF8FA3), ('7', 0xD9667D), ('p', 0xFFB3C4), ('e', 0x2C2A4A), ('w', 0xFFFFFF)),
    [(7, 10), (15, 10)], (12, 13), Smile, (12, 3), 16,
    [new(0, 12, ".oo"), new(0, 13, "o1d"), new(0, 14, ".oo"), new(0, 15, "..o")],
    ["Boo! Ready.", "*floats*", "I'll haunt your bugs.", "Spooky fast, promise."], Floats: true);

    public static readonly PetBody Fox = new("fox", "Fox", "Maple",
    [
        "........................",
        "..oo................oo..",
        "..o7o..............o7o..",
        "..o77o............o77o..",
        "..o244o..........o443o..",
        "..o2444o........o4443o..",
        "..o1244oooooooooo4423o..",
        ".o1hh22222222222222233o.",
        ".o1h222222222222222233o.",
        ".o12222222222222222233o.",
        ".o12222222222222222233o.",
        ".o12222222222222222233o.",
        ".o44222222222222224445o.",
        ".o44pp422227722224pp45o.",
        "..o444444444444444455o..",
        "...o4444444444444455oooo",
        "....o1dddddddddddd3o444o",
        "....o12244444444233o444o",
        "....o12444444444423o223o",
        "....o12444444444433o223o",
        "....o22444444444433d223o",
        "....o2o445o33o445o3d233o",
        "....oooooooooooooooooooo",
        "........................",
    ],
    Pal(('o', 0x3A1C10), ('1', 0xFFA45C), ('2', 0xF07A2E), ('3', 0xC45A1C), ('4', 0xFFF6EC), ('5', 0xE8D5C4),
        ('6', 0x4A2618), ('7', 0x3A1E14), ('p', 0xFF9DAE), ('e', 0x2A1A12), ('w', 0xFFFFFF)),
    [(6, 10), (16, 10)], (12, 14), Smile, (12, 6), 16,
    [new(20, 1, ".."), new(19, 2, "ooo")],
    ["Yip! What's next?", "*tail swish*", "Ready to hunt bugs.", "Quick and clever, like you."]);

    public static readonly PetBody Turtle = new("turtle", "Turtle", "Pebble",
    [
        "........................",
        "........................",
        "........................",
        "........................",
        "........................",
        "........................",
        "..........oooooo........",
        "........oo1hh112oo......",
        ".......o1h12d222d3o.....",
        "......o1h122d222d23o....",
        "..ooooooo222d222d223o...",
        ".o4hh4444o22d222d223o...",
        "o444444444odddddddddo...",
        "o444444444o222d222d23o..",
        "o444444444o222d222d23o..",
        "op4444444po222d222d33o..",
        "o544444445o333d333d33oo.",
        ".o5555555o3333333333333o",
        "..ooooooooooooooooooooo.",
        "........o445o....o445o..",
        "........o445o....o445o..",
        "........o555o....o555o..",
        "........ooooo....ooooo..",
        "........................",
    ],
    Pal(('o', 0x1F3A2A), ('1', 0x9ED98A), ('2', 0x5DB36B), ('3', 0x3E8A54), ('4', 0xF4E7A8), ('5', 0xD6C27C),
        ('6', 0x8A5A2B), ('7', 0x6F4520), ('p', 0xFF9DAE), ('e', 0x1A2414), ('w', 0xFFFFFF)),
    [(2, 12), (7, 12)], (6, 15), Smile, (5, 10), 17,
    [new(8, 22, "....."), new(8, 21, "ooooo")],
    ["Slow and steady.", "*peeks out*", "Ready when you are.", "I carry my home. And your bugs."]);

    /// <summary>The built-in pets in gallery order; the first is the default.</summary>
    public static readonly IReadOnlyList<PetBody> Bodies = [Pi, Cat, Owl, Robot, Slime, Cactus, Ghost, Fox, Turtle];

    public static PetBody? Body(string? id) => Bodies.FirstOrDefault(b => b.Id == id);

    // ───────────────────────────── Coats ─────────────────────────────

    public static readonly IReadOnlyList<PetCoat> Coats =
    [
        new("ginger", "Ginger", C(0xFFC98A), C(0xF5A25D), C(0xD27A3E), C(0x4A2A1A)),
        new("cream", "Cream", C(0xFFF6E4), C(0xF1DDBA), C(0xD6B98C), C(0x4A3624)),
        new("cocoa", "Cocoa", C(0xC99A74), C(0xA0714E), C(0x7A5237), C(0x2E1C12)),
        new("slate", "Slate", C(0xC3CFDD), C(0x94A6BC), C(0x6D809A), C(0x1F2733)),
        new("mint", "Mint", C(0xC8F5DC), C(0x8FE0B4), C(0x5CBF8C), C(0x173A2A)),
        new("sky", "Sky", C(0xB9DCFF), C(0x7DB8F7), C(0x4F8ED6), C(0x13294A)),
        new("lilac", "Lilac", C(0xDCC8FF), C(0xB89AF0), C(0x8E6FD0), C(0x2A1F45)),
        new("rose", "Rose", C(0xFFC9D6), C(0xF59AB2), C(0xD46E8C), C(0x45182A)),
        new("lemon", "Lemon", C(0xFFF3A6), C(0xFFE066), C(0xE0B634), C(0x4A3A0A)),
        new("snow", "Snow", C(0xFFFFFF), C(0xEDEFF5), C(0xC9CFDD), C(0x2A2D3A)),
    ];

    public static PetCoat? Coat(string? id) => Coats.FirstOrDefault(c => c.Id == id);

    // ───────────────────────────── Accessories ─────────────────────────────

    public static readonly PetAccessory None = new("none", "None");

    public static readonly IReadOnlyList<PetAccessory> Accessories =
    [
        None,
        new("party-hat", "Party hat"),
        new("crown", "Crown"),
        new("top-hat", "Top hat"),
        new("bow", "Bow"),
        new("flower", "Flower"),
        new("glasses", "Glasses"),
        new("shades", "Shades"),
        new("scarf", "Scarf"),
    ];

    public static PetAccessory Accessory(string? id) => Accessories.FirstOrDefault(a => a.Id == id) ?? None;

    /// <summary>Pixel art with its own colours: hats, effects. <see cref="Id"/> names it in cache keys.</summary>
    public sealed record Sprite(string Id, string[] Rows, IReadOnlyDictionary<char, uint> Palette)
    {
        public int Width => Rows.Max(r => r.Length);
        public int Height => Rows.Length;
    }

    // Hats are at most five rows: they sit one row into the head, so even a pet whose head starts on row 3 wears them whole
    public static readonly Sprite PartyHat = new("party-hat",
    [
        "...y...",
        "..obo..",
        "..oro..",
        ".obrbo.",
        "obrbrbo",
    ], Pal(('o', 0x3A2340), ('y', 0xFFD23F), ('r', 0xFF6B8B), ('b', 0x6BC6FF)));

    public static readonly Sprite Crown = new("crown",
    [
        ".y..y..y.",
        "oyooyooyo",
        "oyryygyYo",
        "ooooooooo",
    ], Pal(('o', 0x5A3A10), ('y', 0xFFD23F), ('Y', 0xE0A21A), ('r', 0xFF5A6E), ('g', 0x5CE0B0)));

    public static readonly Sprite TopHat = new("top-hat",
    [
        "..ooooo..",
        "..okkko..",
        "..orrro..",
        "okkkkkkko",
        "ooooooooo",
    ], Pal(('o', 0x14121C), ('k', 0x4B3F72), ('r', 0xFF6B8B)));

    public static readonly Sprite Bow = new("bow",
    [
        "oo...oo",
        "opo.opo",
        "oppkppo",
        "opo.opo",
        "oo...oo",
    ], Pal(('o', 0x5A1830), ('p', 0xFF7FAA), ('k', 0xFFD1E4)));

    public static readonly Sprite Flower = new("flower",
    [
        "..p..",
        ".ppp.",
        "ppypp",
        ".ppp.",
        "..p..",
    ], Pal(('p', 0xFF7FB0), ('y', 0xFFE27A)));

    public static readonly uint GlassesFrame = C(0x33221A);
    public static readonly uint ShadesLens = C(0x16161C);
    public static readonly uint ShadesGlint = C(0xFFFFFF);
    public static readonly uint ScarfLight = C(0xFF6B6B);
    public static readonly uint ScarfDark = C(0xC23B45);

    /// <summary>The soft contact shadow under the feet (black at this opacity, fainter under a floating pet).</summary>
    public const double ShadowOpacity = 0.2;

    // ───────────────────────────── Effects ─────────────────────────────
    // Drawn in the canvas left of the body; pale fills with a dark outline, or mid colours, so they read on light and dark.

    public static readonly Sprite SmallZ = new("z",
    [
        "zzz",
        ".z.",
        "zzz",
    ], Pal(('z', 0x7C9CE8)));

    public static readonly Sprite BigZ = new("Z",
    [
        "zzzz",
        "..z.",
        ".z..",
        "zzzz",
    ], Pal(('z', 0x7C9CE8)));

    public static readonly Sprite ThoughtCloud = new("cloud",
    [
        "..oooo.ooo..",
        ".o1111o111o.",
        "o1111111111o",
        "o1111111111o",
        "o1111111111o",
        ".o11111111o.",
        "..oo1111oo..",
        "....oooo....",
    ], Pal(('o', 0x3A3F55), ('1', 0xFFFFFF)));

    public static readonly Sprite ThoughtTrail = new("trail",
    [
        ".o.",
        "o1o",
        ".o.",
    ], Pal(('o', 0x3A3F55), ('1', 0xFFFFFF)));

    public static readonly Sprite ThoughtDot = new("dot",
    [
        "kk",
        "kk",
    ], Pal(('k', 0x3A3F55)));

    public static readonly Sprite Bang = new("bang",
    [
        ".oo.",
        "oyyo",
        "oyyo",
        "oyyo",
        "oyyo",
        ".oo.",
        "oyyo",
        ".oo.",
    ], Pal(('o', 0x4A2A00), ('y', 0xFFC53D)));

    public static readonly Sprite GearA = new("gear-a",
    [
        "...ooo...",
        "...ogo...",
        "..ogggo..",
        "ooggggdoo",
        "ogggodddo",
        "ooggdddoo",
        "..odddo..",
        "...odo...",
        "...ooo...",
    ], Pal(('o', 0x3A3F4A), ('g', 0xE3E8EF), ('d', 0x9AA4B3)));

    /// <summary>The gear turned by 45°: the two frames alternate.</summary>
    public static readonly Sprite GearB = new("gear-b",
    [
        ".oo...oo.",
        "ogo...ogo",
        "oogooogoo",
        "..ogggo..",
        "..ogodo..",
        "..ogddo..",
        "oogooodoo",
        "ogo...odo",
        ".oo...oo.",
    ], Pal(('o', 0x3A3F4A), ('g', 0xE3E8EF), ('d', 0x9AA4B3)));

    public static readonly Sprite SparkBig = new("spark",
    [
        "..s..",
        "..s..",
        "ssWss",
        "..s..",
        "..s..",
    ], Pal(('s', 0xFFD23F), ('W', 0xFFFBEA)));

    public static readonly Sprite SparkSmall = new("spark-s",
    [
        ".s.",
        "sWs",
        ".s.",
    ], Pal(('s', 0xFFD23F), ('W', 0xFFFBEA)));

    public static readonly Sprite Sweat = new("sweat",
    [
        "..o..",
        ".oso.",
        "ossso",
        "osWso",
        ".ooo.",
    ], Pal(('o', 0x1F4A73), ('s', 0x9AD8FF), ('W', 0xE6F6FF)));

    public static readonly Sprite Heart = new("heart",
    [
        ".oo.oo.",
        "orWorro",
        "orrrrro",
        ".orrro.",
        "..oro..",
        "...o...",
    ], Pal(('o', 0x6A1830), ('r', 0xFF6B8B), ('W', 0xFFD1DC)));

    /// <summary>Every sprite, for checks.</summary>
    public static IEnumerable<Sprite> Sprites =>
    [
        PartyHat, Crown, TopHat, Bow, Flower, SmallZ, BigZ, ThoughtCloud, ThoughtTrail, ThoughtDot, Bang, GearA, GearB,
        SparkBig, SparkSmall, Sweat, Heart,
    ];
}
