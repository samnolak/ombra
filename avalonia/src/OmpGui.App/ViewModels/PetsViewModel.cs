using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.App.Pets;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>A pet in the "My pets" gallery.</summary>
public sealed partial class PetCardViewModel(string id, string name, PetLook look, bool isCustom, bool isChanged) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public PetLook Look { get; } = look;
    /// <summary>Made by the user (can be deleted).</summary>
    public bool IsCustom { get; } = isCustom;
    /// <summary>A built-in pet the user renamed or recoloured (can be reset).</summary>
    public bool IsChanged { get; } = isChanged;
    public string Species => Look.Body.Species;
    public string Detail => IsCustom ? "Yours · " + Species : Species;
    [ObservableProperty] private bool _isChosen;
}

public sealed partial class PetBodyOptionViewModel(PetBody body) : ObservableObject
{
    public PetBody Body { get; } = body;
    public PetLook Look { get; } = new(body);
    public string Label => Body.Species;
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class PetCoatOptionViewModel(PetCoat? coat, string name, uint argb) : ObservableObject
{
    /// <summary>Null: the body's own colours.</summary>
    public PetCoat? Coat { get; } = coat;
    public string Name { get; } = name;
    public IBrush Swatch { get; } = new SolidColorBrush(Color.FromUInt32(argb));
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class PetAccessoryOptionViewModel(PetAccessory accessory) : ObservableObject
{
    public PetAccessory Accessory { get; } = accessory;
    public string Name => Accessory.Name;
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class PetMoodOptionViewModel(PetMood mood, string label) : ObservableObject
{
    public PetMood Mood { get; } = mood;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// The pixel pet: whether it shows and how big, which one (built-in or made by the user), the editor that creates and
/// customizes pets, and the mood it shows, from what the agent does (<see cref="PetMoodMachine"/>). omp knows nothing of
/// pets: all of this is the client's, saved in its settings file (<see cref="PetOptions"/>).
/// </summary>
public sealed partial class PetsViewModel : ObservableObject
{
    public const string DefaultPet = "pi";
    private const int MaxNameLength = 24;
    private static readonly TimeSpan BubbleFor = TimeSpan.FromSeconds(5);

    private readonly MainViewModel _host;
    private readonly PetMoodMachine _machine;
    private readonly DispatcherTimer _moodTimer = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _bubbleTimer = new(DispatcherPriority.Background);
    private readonly List<CustomPetOptions> _custom = [];
    private readonly HashSet<long> _failedSeen = [];
    private readonly bool _loading;
    private long _runsSeen = -1;
    private long _epoch = -1;
    private int _scanFrom;
    private string? _lastFailedTool;
    private bool _bubbleIsActivity;
    private int _quip;

    internal PetsViewModel(MainViewModel host, PetOptions? saved, TimeProvider? clock = null)
    {
        _host = host;
        Clock = clock ?? TimeProvider.System;
        _machine = new PetMoodMachine(Clock.GetUtcNow());
        _moodTimer.Tick += (_, _) =>
        {
            _moodTimer.Stop();
            _machine.Advance(Clock.GetUtcNow());
            SyncMood();
        };
        _bubbleTimer.Interval = BubbleFor;
        _bubbleTimer.Tick += (_, _) =>
        {
            _bubbleTimer.Stop();
            IsBubbleOpen = false;
        };
        MoodOptions =
        [
            new(PetMood.Idle, "Idle"), new(PetMood.Thinking, "Thinking"), new(PetMood.Working, "Working"),
            new(PetMood.Waiting, "Waiting"), new(PetMood.Done, "Done"), new(PetMood.Error, "Oops"), new(PetMood.Sleeping, "Asleep"),
        ];
        MoodOptions[0].IsSelected = true;
        BodyOptions = [.. PetArt.Bodies.Select(b => new PetBodyOptionViewModel(b))];
        AccessoryOptions = [.. PetArt.Accessories.Select(a => new PetAccessoryOptionViewModel(a))];

        _loading = true;
        ShowPet = saved?.Show ?? true;
        Size = saved?.Size is "small" ? "small" : "medium";
        foreach (var c in saved?.Custom ?? []) if (c.Id.Length > 0 && !_custom.Any(x => x.Id == c.Id)) _custom.Add(c);
        RebuildGallery(saved?.Chosen);
        _loading = false;
        _host.PropertyChanged += OnHostChanged;
    }

    /// <summary>The time source of the mood machine (tests).</summary>
    internal TimeProvider Clock { get; }

    // ───────────────────────── Showing the pet ─────────────────────────

    /// <summary>Show the pet above the message box. On by default: it was asked for, it is how people find it, and at
    /// rest it costs nothing (it falls asleep and stops drawing); one switch in Settings → Pets turns it off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnScreen), nameof(ComposerMargin))]
    private bool _showPet;

    /// <summary>small | medium.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PixelScale), nameof(PerchHeight), nameof(ComposerMargin), nameof(IsSmall), nameof(IsMedium), nameof(BubbleMargin))]
    private string _size = "medium";

    public bool IsSmall => Size == "small";
    public bool IsMedium => !IsSmall;

    /// <summary>The pet's distance from the message box's right end: clear of its rounded corner.</summary>
    public const double PetInset = 20;

    public static Thickness PetMargin => new(0, 0, PetInset, 0);

    /// <summary>The speech bubble: left of the pet (and of its effects), at the height of its head.</summary>
    public Thickness BubbleMargin => new(0, IsSmall ? 2 : 8, PetInset + PetFrames.Width * PixelScale + 2, 0);

    /// <summary>Screen pixels per pet pixel: 4/3 (32 px tall) or 2 (48 px tall).</summary>
    public double PixelScale => IsSmall ? 4 / 3.0 : 2;

    /// <summary>Height of the pet: the room it takes above the message box.</summary>
    public double PerchHeight => PetFrames.Height * PixelScale;

    /// <summary>Below this window height the pet's band costs the conversation too much room: the pet steps aside.</summary>
    public const double MinWindowHeight = 520;

    /// <summary>The window's height (set by <c>PetPerch</c> from its window; unknown: roomy).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoom), nameof(IsOnScreen), nameof(ComposerMargin))]
    private double _windowHeight = double.PositiveInfinity;

    /// <summary>The window is tall enough for the pet's band.</summary>
    public bool HasRoom => WindowHeight >= MinWindowHeight;

    partial void OnWindowHeightChanged(double value)
    {
        if (!HasRoom) IsBubbleOpen = false;
    }

    /// <summary>The pet is drawn: switched on, the window tall enough, and neither the setup screen nor the settings page covers the conversation.</summary>
    public bool IsOnScreen => ShowPet && HasRoom && !_host.ShowSetupScreen && !_host.IsSettingsOpen;

    /// <summary>
    /// The message box's margin: with the pet, a band as tall as the pet above the box (its feet on the box's top edge),
    /// so it never covers the conversation, a card or a question. Kept while the settings page is open (no layout churn);
    /// given back in a short window (<see cref="MinWindowHeight"/>).
    /// </summary>
    public Thickness ComposerMargin => ShowPet && HasRoom && !_host.ShowSetupScreen ? new Thickness(24, PerchHeight - 1, 24, 16) : new Thickness(24, 6, 24, 16);

    partial void OnShowPetChanged(bool value)
    {
        if (value)
        {
            _machine.Poke(Clock.GetUtcNow());
            SyncMood();
        }
        else IsBubbleOpen = false;
        Save();
    }

    partial void OnSizeChanged(string value) => Save();

    [RelayCommand]
    private void SetSize(string? size)
    {
        if (size is "small" or "medium") Size = size;
    }

    // ───────────────────────── Mood ─────────────────────────

    /// <summary>Goes up as the user types: the pet glances at the message box.</summary>
    [ObservableProperty] private int _attention;

    /// <summary>The mood on screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private PetMood _mood = PetMood.Idle;

    public string AccessibleName => $"{ChosenName}, your pet: " + Mood switch
    {
        PetMood.Sleeping => "asleep",
        PetMood.Thinking => "thinking",
        PetMood.Working => "working",
        PetMood.Waiting => "waiting for you",
        PetMood.Done => "done",
        PetMood.Error => "something went wrong",
        _ => "idle",
    };

    /// <summary>From each snapshot the window applies: the agent's state, a finished run, a failed tool.</summary>
    internal void OnSnapshot(SessionSnapshot s)
    {
        var now = Clock.GetUtcNow();
        if (s.TranscriptEpoch != _epoch)
        {
            // Another conversation: its history (old failures, old runs) is not news
            _epoch = s.TranscriptEpoch;
            _failedSeen.Clear();
            _runsSeen = -1;
            _scanFrom = 0;
        }
        var primed = _runsSeen >= 0;
        string? failed = null;
        // Only a running tool can still fail: rows before the first one still running were already seen
        var from = Math.Min(_scanFrom, s.Items.Count);
        _scanFrom = s.Items.Count;
        for (var i = from; i < s.Items.Count; i++)
        {
            if (s.Items[i] is not ToolItem t) continue;
            if (t.Status == ToolStatus.Running) _scanFrom = Math.Min(_scanFrom, i);
            else if (t.Status == ToolStatus.Failed && _failedSeen.Add(t.Key) && primed) failed = t.Name;
        }
        var finished = primed && s.RunsEnded > _runsSeen && _host.Phase == SessionPhase.Ready && !LastRunInterrupted(s);
        _runsSeen = Math.Max(_runsSeen, s.RunsEnded);
        _machine.SetBase(PetMoodMachine.BaseMood(s, _host.Phase, _host.HasDialog), now);
        if (failed is not null)
        {
            _lastFailedTool = failed;
            _machine.Flash(PetMood.Error, PetMoodMachine.OopsFor, now);
        }
        if (finished) _machine.Flash(PetMood.Done, PetMoodMachine.DoneFor, now);
        SyncMood();
    }

    private static bool LastRunInterrupted(SessionSnapshot s)
    {
        for (var i = s.Items.Count - 1; i >= 0; i--)
            if (s.Items[i] is TurnEndItem end) return end.Interrupted;
        return false;
    }

    /// <summary>The user is here (typing, clicking the pet): the pet stays awake, or wakes up.</summary>
    internal void Poke()
    {
        _machine.Poke(Clock.GetUtcNow());
        SyncMood();
    }

    private void SyncMood()
    {
        Mood = _machine.Shown;
        _moodTimer.Stop();
        if (!ShowPet || _shutDown) return;
        var now = Clock.GetUtcNow();
        if (_machine.NextChange(now) is { } next)
        {
            _moodTimer.Interval = next - now + TimeSpan.FromMilliseconds(5);
            _moodTimer.Start();
        }
    }

    private bool _shutDown;

    /// <summary>The window is closing: no timer may fire into a window that is gone.</summary>
    internal void Shutdown()
    {
        _shutDown = true;
        _moodTimer.Stop();
        _bubbleTimer.Stop();
    }

    // ───────────────────────── Speech bubble ─────────────────────────

    [ObservableProperty] private bool _isBubbleOpen;
    [ObservableProperty] private string _bubbleText = "";

    /// <summary>A click on the pet: a bubble with what the agent is doing, or a word from the pet; a click again closes it.</summary>
    [RelayCommand]
    private void Talk()
    {
        _bubbleTimer.Stop();
        if (IsBubbleOpen)
        {
            IsBubbleOpen = false;
            return;
        }
        var wasAsleep = Mood == PetMood.Sleeping;
        Poke();
        _bubbleIsActivity = false;
        BubbleText = wasAsleep ? "*yawns* I'm up!" : Say();
        IsBubbleOpen = true;
        if (!_shutDown) _bubbleTimer.Start();
    }

    private string Say()
    {
        if (Mood == PetMood.Waiting || _host.HasDialog)
            return _host.CurrentDialog?.IsApproval == true ? "omp needs your approval, just above." : "omp is asking you something, just above.";
        if (_host.HasError) return Activity();
        if (Mood == PetMood.Error && _lastFailedTool is { } tool) return $"Oops, {tool} failed. omp is on it.";
        if (_host.IsRunning) return Activity();
        if (Mood == PetMood.Done) return "All done!";
        var quips = ChosenLook.Body.Quips;
        return quips[_quip++ % quips.Length];
    }

    private string Activity()
    {
        _bubbleIsActivity = true;
        return _host.StatusText;
    }

    private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.StatusText) when IsBubbleOpen && _bubbleIsActivity:
                BubbleText = _host.StatusText;
                break;
            case nameof(MainViewModel.ComposerText):
                Poke();
                Attention++;
                break;
            case nameof(MainViewModel.ShowSetupScreen):
                OnPropertyChanged(nameof(IsOnScreen));
                OnPropertyChanged(nameof(ComposerMargin));
                break;
            case nameof(MainViewModel.IsSettingsOpen):
                OnPropertyChanged(nameof(IsOnScreen));
                if (_host.IsSettingsOpen) IsBubbleOpen = false;
                else CancelEditor();
                break;
        }
    }

    // ───────────────────────── My pets ─────────────────────────

    public ObservableCollection<PetCardViewModel> Gallery { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChosenLook), nameof(ChosenName), nameof(AccessibleName), nameof(CustomizeTitle), nameof(EditorTitle))]
    [NotifyPropertyChangedFor(nameof(CanChangeBody), nameof(CanDelete), nameof(CanReset))]
    private PetCardViewModel? _chosen;

    public PetLook ChosenLook => Chosen?.Look ?? new PetLook(PetArt.Pi);
    public string ChosenName => Chosen?.Name ?? PetArt.Pi.DefaultName;

    [RelayCommand]
    private void ChoosePet(PetCardViewModel? card)
    {
        if (card is null || card == Chosen) return;
        Chosen = card;
        foreach (var c in Gallery) c.IsChosen = c == card;
        _quip = 0;
        if (IsEditorOpen && !IsCreating) StartCustomize();
        Save();
    }

    private void RebuildGallery(string? chosenId)
    {
        Gallery.Clear();
        foreach (var body in PetArt.Bodies)
        {
            var over = _custom.FirstOrDefault(c => c.Id == body.Id);
            Gallery.Add(new PetCardViewModel(body.Id, CleanName(over?.Name) ?? body.DefaultName,
                new PetLook(body, PetArt.Coat(over?.Coat), PetArt.Accessory(over?.Accessory)), false, over is not null));
        }
        foreach (var c in _custom)
        {
            if (PetArt.Body(c.Id) is not null || PetArt.Body(c.Body) is not { } body) continue; // an override, or a body this version lacks
            Gallery.Add(new PetCardViewModel(c.Id, CleanName(c.Name) ?? "My " + body.Species.ToLowerInvariant(),
                new PetLook(body, PetArt.Coat(c.Coat), PetArt.Accessory(c.Accessory)), true, false));
        }
        var chosen = Gallery.FirstOrDefault(c => c.Id == chosenId) ?? Gallery[0];
        foreach (var c in Gallery) c.IsChosen = c == chosen;
        Chosen = chosen;
    }

    private static string? CleanName(string? name) =>
        name?.Trim() is { Length: > 0 } n ? (n.Length > MaxNameLength ? n[..MaxNameLength] : n) : null;

    private void Save()
    {
        if (_loading) return;
        var options = new PetOptions
        {
            Show = ShowPet,
            Chosen = Chosen?.Id ?? DefaultPet,
            Size = Size,
            Custom = _custom.Count > 0 ? [.. _custom] : null,
        };
        _host.PersistPet(options);
    }

    // ───────────────────────── Create and customize ─────────────────────────

    public IReadOnlyList<PetBodyOptionViewModel> BodyOptions { get; }
    public ObservableCollection<PetCoatOptionViewModel> CoatOptions { get; } = [];
    public IReadOnlyList<PetAccessoryOptionViewModel> AccessoryOptions { get; }
    public IReadOnlyList<PetMoodOptionViewModel> MoodOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle), nameof(EditorSaveLabel), nameof(CanChangeBody), nameof(CanDelete), nameof(CanReset))]
    private bool _isEditorOpen;

    /// <summary>The editor makes a new pet (else it changes the chosen one).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle), nameof(EditorSaveLabel), nameof(CanChangeBody), nameof(CanDelete), nameof(CanReset))]
    private bool _isCreating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraftLook))]
    private PetBody _draftBody = PetArt.Pi;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraftLook))]
    private PetCoat? _draftCoat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraftLook))]
    private PetAccessory _draftAccessory = PetArt.None;

    [ObservableProperty] private string _draftName = "";
    [ObservableProperty] private PetMood _previewMood = PetMood.Idle;

    public PetLook DraftLook => new(DraftBody, DraftCoat, DraftAccessory);
    public string EditorTitle => IsCreating ? "Create a pet" : "Customize " + ChosenName;
    public string CustomizeTitle => "Customize " + ChosenName;
    public string EditorSaveLabel => IsCreating ? "Create pet" : "Save changes";
    public bool CanChangeBody => IsCreating || Chosen?.IsCustom == true;
    public bool CanDelete => IsEditorOpen && !IsCreating && Chosen?.IsCustom == true;
    public bool CanReset => IsEditorOpen && !IsCreating && Chosen is { IsCustom: false, IsChanged: true };
    public string NamePlaceholder => "My " + DraftBody.Species.ToLowerInvariant();

    partial void OnDraftBodyChanged(PetBody value)
    {
        foreach (var o in BodyOptions) o.IsSelected = o.Body == value;
        RebuildCoats();
        OnPropertyChanged(nameof(NamePlaceholder));
    }

    partial void OnDraftCoatChanged(PetCoat? value)
    {
        foreach (var o in CoatOptions) o.IsSelected = o.Coat == value;
    }

    partial void OnDraftAccessoryChanged(PetAccessory value)
    {
        foreach (var o in AccessoryOptions) o.IsSelected = o.Accessory == value;
    }

    partial void OnPreviewMoodChanged(PetMood value)
    {
        foreach (var o in MoodOptions) o.IsSelected = o.Mood == value;
    }

    /// <summary>
    /// "Natural" first (the body's own colours, its swatch the body's coat colour), then the coats, less the one that
    /// looks the same as natural (a ginger cat needs no second ginger).
    /// </summary>
    private void RebuildCoats()
    {
        CoatOptions.Clear();
        var natural = DraftBody.Palette['2'];
        CoatOptions.Add(new PetCoatOptionViewModel(null, "Natural", natural) { IsSelected = DraftCoat is null });
        foreach (var c in PetArt.Coats)
            if (c == DraftCoat || Distance(c.Mid, natural) > 48)
                CoatOptions.Add(new PetCoatOptionViewModel(c, c.Name, c.Mid) { IsSelected = DraftCoat == c });
    }

    private static int Distance(uint a, uint b) =>
        Math.Abs((int)(a >> 16 & 0xFF) - (int)(b >> 16 & 0xFF)) + Math.Abs((int)(a >> 8 & 0xFF) - (int)(b >> 8 & 0xFF)) + Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF));

    [RelayCommand]
    private void StartCreate()
    {
        IsCreating = true;
        DraftBody = PetArt.Cat;
        DraftCoat = null;
        DraftAccessory = PetArt.None;
        DraftName = "";
        RebuildCoats();
        OnDraftBodyChanged(DraftBody);
        OnDraftAccessoryChanged(DraftAccessory);
        PreviewMood = PetMood.Idle;
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void StartCustomize()
    {
        var look = ChosenLook;
        IsCreating = false;
        DraftBody = look.Body;
        DraftCoat = look.Coat;
        DraftAccessory = look.Accessory;
        DraftName = ChosenName;
        RebuildCoats();
        OnDraftBodyChanged(DraftBody);
        OnDraftAccessoryChanged(DraftAccessory);
        PreviewMood = PetMood.Idle;
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void ChooseBody(PetBodyOptionViewModel? o)
    {
        if (o is not null && CanChangeBody) DraftBody = o.Body;
    }

    [RelayCommand]
    private void ChooseCoat(PetCoatOptionViewModel? o)
    {
        if (o is not null) DraftCoat = o.Coat;
    }

    [RelayCommand]
    private void ChooseAccessory(PetAccessoryOptionViewModel? o)
    {
        if (o is not null) DraftAccessory = o.Accessory;
    }

    [RelayCommand]
    private void ChoosePreviewMood(PetMoodOptionViewModel? o)
    {
        if (o is not null) PreviewMood = o.Mood;
    }

    [RelayCommand]
    private void CancelEditor() => IsEditorOpen = false;

    /// <summary>Create: a new pet, chosen at once. Customize: the chosen pet's new name, colours and accessory.</summary>
    [RelayCommand]
    private void SaveEditor()
    {
        if (!IsEditorOpen) return;
        string id;
        if (IsCreating)
        {
            id = "my-" + Guid.NewGuid().ToString("N")[..8];
            _custom.Add(new CustomPetOptions
            {
                Id = id,
                Name = CleanName(DraftName) ?? NamePlaceholder,
                Body = DraftBody.Id,
                Coat = DraftCoat?.Id,
                Accessory = DraftAccessory == PetArt.None ? null : DraftAccessory.Id,
            });
        }
        else if (Chosen is { } chosen)
        {
            id = chosen.Id;
            var body = chosen.IsCustom ? DraftBody : chosen.Look.Body;
            var entry = new CustomPetOptions
            {
                Id = id,
                // A built-in pet keeps no name of its own when it is given its default one back
                Name = CleanName(DraftName) is { } n && (chosen.IsCustom || n != body.DefaultName) ? n : chosen.IsCustom ? chosen.Name : null,
                Body = body.Id,
                Coat = DraftCoat?.Id,
                Accessory = DraftAccessory == PetArt.None ? null : DraftAccessory.Id,
            };
            var at = _custom.FindIndex(c => c.Id == id);
            var unchanged = !chosen.IsCustom && entry is { Name: null, Coat: null, Accessory: null };
            if (at >= 0 && unchanged) _custom.RemoveAt(at);
            else if (at >= 0) _custom[at] = entry;
            else if (!unchanged) _custom.Add(entry);
        }
        else return;
        RebuildGallery(id);
        IsEditorOpen = false;
        Save();
    }

    /// <summary>Deletes the chosen pet (only one the user made); the default pet is chosen instead.</summary>
    [RelayCommand]
    private void DeleteChosen()
    {
        if (Chosen is not { IsCustom: true } chosen) return;
        _custom.RemoveAll(c => c.Id == chosen.Id);
        RebuildGallery(DefaultPet);
        IsEditorOpen = false;
        Save();
    }

    /// <summary>Gives a built-in pet its own name, colours and nothing to wear again.</summary>
    [RelayCommand]
    private void ResetChosen()
    {
        if (Chosen is not { IsCustom: false } chosen) return;
        _custom.RemoveAll(c => c.Id == chosen.Id);
        RebuildGallery(chosen.Id);
        IsEditorOpen = false;
        Save();
    }
}
