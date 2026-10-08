using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>The pixel pet above the message box (<see cref="PetsViewModel"/>): a client feature, omp has no pets.</summary>
public partial class MainViewModel
{
    private PetsViewModel? _pets;

    /// <summary>Created on first use (the window binds it at once), with what the settings file says.</summary>
    public PetsViewModel Pets => _pets ??= CreatePets();

    private PetsViewModel CreatePets()
    {
        PetOptions? saved = null;
        try { saved = _settings?.Load().Pet; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The pet starts with its defaults; the settings page reports the file's problem when it opens.
        }
        var pets = new PetsViewModel(this, saved);
        if (_open.Last is { } s) pets.OnSnapshot(s);
        return pets;
    }

    /// <summary>Saves the pet settings (the rest of the client's settings file is kept as it is).</summary>
    internal void PersistPet(PetOptions options) => Persist(o => o with { Pet = options });

    /// <summary>
    /// The pet's message box: <paramref name="text"/> goes the way of the message box's Send (terminal-only commands
    /// handled here, a prompt when ready, a follow-up while a run goes on) without touching the message box's draft
    /// or its attachments. False when omp cannot take a message now.
    /// </summary>
    internal bool SendFromPet(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Phase is not (SessionPhase.Ready or SessionPhase.Running) || IsSigningIn) return false;
        var draft = ComposerText;
        if (HandleTerminalOnlyCommand(text))
        {
            // Handling a command empties the message box, which held a draft, not the command: the draft comes back
            if (ComposerText.Length == 0 && draft.Length > 0) ComposerText = draft;
            return true;
        }
        _ = SubmitAsync(() => (text.Trim(), Array.Empty<ImageAttachment>()));
        return true;
    }

    /// <summary>Called from <see cref="Apply"/>: the pet's mood follows the agent.</summary>
    private void ApplyPet(SessionSnapshot s) => _pets?.OnSnapshot(s);
}
