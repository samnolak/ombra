namespace OmpGui.ClientCore;

/// <summary>
/// The pixel pet that keeps the user company above the message box (a client feature: omp has no pets). Saved in the
/// client's settings file under <c>pet</c>; every field is optional so an older or hand-edited file still loads.
/// </summary>
public sealed record PetOptions
{
    /// <summary>Show the pet above the message box (default on).</summary>
    public bool? Show { get; init; }
    /// <summary>Id of the chosen pet: a built-in one (<c>cat</c>, <c>owl</c>…) or a created one (<c>my-…</c>).</summary>
    public string? Chosen { get; init; }
    /// <summary><c>small</c> | <c>medium</c> (default medium).</summary>
    public string? Size { get; init; }
    /// <summary>Pets the user created, and changes to built-in ones (an entry whose id is a built-in pet's).</summary>
    public List<CustomPetOptions>? Custom { get; init; }
    /// <summary><c>desktop</c> (default): dragged off the message box, the pet goes anywhere on the user's screens, over
    /// other apps. <c>window</c>: it stays inside the app's window.</summary>
    public string? Roam { get; init; }
    /// <summary>Where the user dragged the pet inside the window (<see cref="Roam"/> <c>window</c>): its top-left corner as
    /// fractions (0–1) of the room the window leaves it (the window's size less the pet's), so it stays in the same place,
    /// in the window, at any size. Both null: on the message box. A file from before <see cref="Roam"/> existed has only
    /// these: the pet moves to the same spot on the desktop the first time it is shown.</summary>
    public double? X { get; init; }
    public double? Y { get; init; }
    /// <summary>Where the user left the pet on the desktop (<see cref="Roam"/> <c>desktop</c>); null: on the message box.</summary>
    public PetDesktopPlace? Desktop { get; init; }
}

/// <summary>
/// A place on the desktop, in desktop pixels (the units of a window's position: points on macOS, physical pixels on
/// Windows), relative to the monitor it is on, so a monitor moved in the display arrangement keeps the pet on it.
/// The monitor is told by its name and bounds; a monitor that is gone gives the pet to the nearest one.
/// </summary>
public sealed record PetDesktopPlace
{
    /// <summary>The monitor's name as the system gives it (null when it has none).</summary>
    public string? Screen { get; init; }
    /// <summary>The monitor's bounds when the pet was left on it.</summary>
    public int ScreenX { get; init; }
    public int ScreenY { get; init; }
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }
    /// <summary>The pet's top-left corner from the monitor's top-left corner.</summary>
    public int X { get; init; }
    public int Y { get; init; }
}

/// <summary>One pet the user made or changed: which body, which coat colour, which accessory, and its name.</summary>
public sealed record CustomPetOptions
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    /// <summary>Built-in body it is drawn from (<c>cat</c>, <c>robot</c>…).</summary>
    public string? Body { get; init; }
    /// <summary>Coat colour id; null keeps the body's own colours.</summary>
    public string? Coat { get; init; }
    /// <summary>Accessory id (<c>party-hat</c>, <c>glasses</c>, <c>scarf</c>…); null for none.</summary>
    public string? Accessory { get; init; }
}
