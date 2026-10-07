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
    /// <summary>Where the user dragged the pet: its top-left corner as fractions (0–1) of the room the window leaves it
    /// (the window's size less the pet's), so it stays in the same place, in the window, at any size. Both null: on the message box.</summary>
    public double? X { get; init; }
    public double? Y { get; init; }
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
