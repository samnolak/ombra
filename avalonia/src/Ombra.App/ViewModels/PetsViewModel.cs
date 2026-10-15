using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Controls;
using Ombra.App.Localization;
using Ombra.App.Pets;
using Ombra.App.Pets.Oni;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>One of the poses the settings' preview can show (what the pet does when that happens).</summary>
public sealed partial class PetPreviewOptionViewModel(string kind, string label) : ObservableObject
{
    /// <summary>An event of the pet (runtime-contract.json): idle, thinking, working, waiting, done, tool-error, error, abort, sleep.</summary>
    public string Kind { get; } = kind;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// The pet, ONI: whether it shows and how big, where it is, and what it does, from what the agent does
/// (<see cref="OniPet"/>: a run thinking and working, a question, a finished or stopped run, a failed tool, omp stopping,
/// the user typing or clicking it). Its speech bubble and its own message box. omp knows nothing of pets: all of this
/// is the client's, saved in its settings file (<see cref="PetOptions"/>).
/// </summary>
public sealed partial class PetsViewModel : ObservableObject
{
    public const string Name = "ONI";
    private static readonly TimeSpan BubbleFor = TimeSpan.FromSeconds(5);

    private readonly MainViewModel _host;
    private readonly DispatcherTimer _moodTimer = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _bubbleTimer = new(DispatcherPriority.Background);
    private readonly HashSet<long> _failedSeen = [];
    private readonly bool _loading;
    private bool? _reduceMotionSaved;
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
        Oni = new OniPet(OniSpec.Shared, Clock);
        Oni.Changed += SyncMood;
        Preview = new OniPet(OniSpec.Shared, Clock);
        _moodTimer.Tick += (_, _) =>
        {
            _moodTimer.Stop();
            Oni.Sync();
            SyncMood();
        };
        _bubbleTimer.Interval = BubbleFor;
        _bubbleTimer.Tick += (_, _) =>
        {
            _bubbleTimer.Stop();
            IsBubbleOpen = false;
        };
        PreviewOptions =
        [
            new("idle", Loc.T("Idle")), new("thinking", Loc.T("Thinking")), new("working", Loc.T("Working")),
            new("waiting", Loc.T("Waiting")), new("done", Loc.T("Done")), new("tool-error", Loc.T("A tool failed")),
            new("error", Loc.T("omp stopped")), new("abort", Loc.T("Run stopped")), new("sleep", Loc.T("Asleep")),
        ];
        PreviewOptions[0].IsSelected = true;

        _loading = true;
        ShowPet = saved?.Show ?? true;
        Size = saved?.Size switch
        {
            "small" or "64" => "64",
            "96" => "96",
            _ => "80",
        };
        _reduceMotionSaved = saved?.ReduceMotion;
        Roam = saved?.Roam is "window" ? "window" : "desktop";
        // With the desktop roam this is a file from before it existed: PetPerch moves the pet to the same spot on the desktop
        if (saved is { X: { } px, Y: { } py } && double.IsFinite(px) && double.IsFinite(py)) Position = new Point(Math.Clamp(px, 0, 1), Math.Clamp(py, 0, 1));
        if (IsRoamDesktop && Position is null) DesktopPlace = saved?.Desktop;
        ReduceMotion = _reduceMotionSaved ?? Platform.ReducedMotion.System;
        _loading = false;
        _host.PropertyChanged += OnHostChanged;
    }

    /// <summary>The app's clock (tests set their own).</summary>
    internal TimeProvider Clock { get; }

    /// <summary>What the pet does: every view of it (the perch, the desktop window) shows this one.</summary>
    public OniPet Oni { get; }

    /// <summary>The settings' preview: a pet of its own, shown each pose on request.</summary>
    public OniPet Preview { get; }

    // ───────────────────────── Showing the pet ─────────────────────────

    /// <summary>Show the pet above the message box. On by default: it was asked for, it is how people find it, and at
    /// rest it costs nothing (asleep, it stops drawing); one switch in Settings → Pets turns it off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnScreen), nameof(IsOnDesktop), nameof(ComposerMargin))]
    private bool _showPet;

    /// <summary>The pet's height in logical pixels: 64 | 80 | 96.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PetHeight), nameof(PetBox), nameof(PerchHeight), nameof(ComposerMargin), nameof(IsSize64), nameof(IsSize80), nameof(IsSize96))]
    private string _size = "80";

    public bool IsSize64 => Size == "64";
    public bool IsSize80 => Size == "80";
    public bool IsSize96 => Size == "96";

    public double PetHeight => Size switch { "64" => 64, "96" => 96, _ => 80 };

    /// <summary>The pet's box (what takes the pointer, what is placed): <see cref="OniView.BoxFor"/>.</summary>
    public Avalonia.Size PetBox => OniView.BoxFor(PetHeight);

    /// <summary>On its perch, the pet's distance from the message box's right end: clear of its rounded corner.</summary>
    public const double PetInset = 20;

    /// <summary>Height of the pet's box: the room it takes above the message box.</summary>
    public double PerchHeight => PetBox.Height;

    /// <summary>Below this window height the pet's band costs the conversation too much room: the pet steps aside.</summary>
    public const double MinWindowHeight = 520;

    /// <summary>The window's height (set by <c>PetPerch</c> from its window; unknown: roomy).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoom), nameof(IsOnScreen), nameof(ComposerMargin))]
    private double _windowHeight = double.PositiveInfinity;

    /// <summary>The window is tall enough for the pet's band.</summary>
    public bool HasRoom => WindowHeight >= MinWindowHeight;

    partial void OnWindowHeightChanged(double value) => CloseIfHidden();

    /// <summary>
    /// Less motion: the pet shows each pose still (its effects at rest) and asks for no frames. Follows the system's
    /// reduce-motion setting until the user sets it here.
    /// </summary>
    [ObservableProperty] private bool _reduceMotion;

    partial void OnReduceMotionChanged(bool value)
    {
        if (_loading) return;
        _reduceMotionSaved = value;
        Save();
    }

    /// <summary>
    /// Where a dragged pet may go: <c>desktop</c> (the default: anywhere on the user's screens, over other apps, in a
    /// window of its own) or <c>window</c> (inside the app's window). Its home is the message box either way.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRoamDesktop), nameof(IsRoamWindow), nameof(PlaceText))]
    private string _roam = "desktop";

    public bool IsRoamDesktop => Roam == "desktop";
    public bool IsRoamWindow => !IsRoamDesktop;

    /// <summary>The other choice puts the pet back on the message box: a place kept for one would mean nothing to the other.</summary>
    partial void OnRoamChanged(string value)
    {
        if (_loading) return;
        Position = null;
        DesktopPlace = null;
        Save();
    }

    [RelayCommand]
    private void SetRoam(string? roam)
    {
        if (roam is "desktop" or "window") Roam = roam;
    }

    /// <summary>
    /// Where the user dragged the pet in the window (<see cref="Roam"/> <c>window</c>): its top-left corner as fractions
    /// (0–1) of the room the window leaves it (the window's size less the pet's), so a resize keeps it in the same place
    /// and inside the window. Null: on its perch, the message box's top edge.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRoaming), nameof(IsOnScreen), nameof(ComposerMargin), nameof(PlaceText))]
    [NotifyCanExecuteChangedFor(nameof(ReturnToPerchCommand))]
    private Point? _position;

    /// <summary>Where the user left the pet on the desktop (<see cref="Roam"/> <c>desktop</c>). Null: on its perch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRoaming), nameof(IsOnScreen), nameof(IsOnDesktop), nameof(ComposerMargin), nameof(PlaceText))]
    [NotifyCanExecuteChangedFor(nameof(ReturnToPerchCommand))]
    private PetDesktopPlace? _desktopPlace;

    /// <summary>The user put the pet somewhere else than on the message box.</summary>
    public bool IsRoaming => Position is not null || DesktopPlace is not null;

    public string PlaceText => (IsRoaming, IsRoamDesktop) switch
    {
        (true, true) => Loc.T("Where you dropped it, over your other apps. Drag it anywhere on your screens."),
        (true, false) => Loc.T("Where you dropped it. Drag it anywhere in the window."),
        (false, true) => Loc.T("On the top edge of the message box. Drag it anywhere on your screens."),
        (false, false) => Loc.T("On the top edge of the message box. Drag it anywhere in the window."),
    };

    /// <summary>The pet was dropped in the window: <paramref name="fraction"/> as in <see cref="Position"/>.</summary>
    internal void MoveTo(Point fraction)
    {
        Position = new Point(Math.Clamp(fraction.X, 0, 1), Math.Clamp(fraction.Y, 0, 1));
        DesktopPlace = null;
        Save();
    }

    /// <summary>The pet was dropped on the desktop.</summary>
    internal void MoveOnDesktop(PetDesktopPlace place)
    {
        DesktopPlace = place;
        Position = null;
        Save();
    }

    /// <summary>Back on the message box (Settings → Pets, or dropped close to its perch).</summary>
    [RelayCommand(CanExecute = nameof(IsRoaming))]
    private void ReturnToPerch()
    {
        if (!IsRoaming) return;
        Position = null;
        DesktopPlace = null;
        Save();
    }

    partial void OnPositionChanged(Point? value) => CloseIfHidden();

    partial void OnDesktopPlaceChanged(PetDesktopPlace? value) => CloseIfHidden();

    /// <summary>The pet is drawn in the window: switched on, not on the desktop, on its perch only in a window tall
    /// enough for its band (dragged elsewhere it needs no band), and neither the setup screen nor the settings page
    /// covers the conversation.</summary>
    public bool IsOnScreen => ShowPet && DesktopPlace is null && (HasRoom || Position is not null) && !_host.ShowSetupScreen && !_host.IsSettingsOpen;

    /// <summary>The pet is on the desktop, in a window of its own (<c>PetWindow</c>): it stays while the settings page
    /// is open and while the app's window is minimised; only the setup screen (no omp to follow yet) puts it away.</summary>
    public bool IsOnDesktop => ShowPet && DesktopPlace is not null && !_host.ShowSetupScreen;

    /// <summary>
    /// The message box's margin: with the pet on its perch, a band as tall as the pet above the box (its feet on the
    /// box's top edge), so it never covers the conversation, a card or a question. Kept while the settings page is open
    /// (no layout churn); given back in a short window (<see cref="MinWindowHeight"/>) and when the pet is elsewhere.
    /// </summary>
    public Thickness ComposerMargin => ShowPet && HasRoom && !IsRoaming && !_host.ShowSetupScreen ? new Thickness(24, PerchHeight - 1, 24, 16) : new Thickness(24, 6, 24, 16);

    /// <summary>Out of sight, the pet says nothing and its message box closes (what was typed in it is kept).</summary>
    private void CloseIfHidden()
    {
        if (IsOnScreen || IsOnDesktop) return;
        IsBubbleOpen = false;
        IsChatOpen = false;
    }

    partial void OnShowPetChanged(bool value)
    {
        if (value) Oni.Clicked();
        else CloseIfHidden();
        Save();
    }

    partial void OnSizeChanged(string value) => Save();

    [RelayCommand]
    private void SetSize(string? size)
    {
        if (size is "64" or "80" or "96") Size = size;
    }

    // ───────────────────────── Mood ─────────────────────────

    /// <summary>The mood on screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(PetTip))]
    private PetMood _mood = PetMood.Idle;

    public string AccessibleName => Loc.F("{0}, your pet: ", Name) + Mood switch
    {
        PetMood.Sleeping => Loc.T("asleep"),
        PetMood.Thinking => Loc.T("thinking"),
        PetMood.Working => Loc.T("working"),
        PetMood.Waiting => Loc.T("waiting for you"),
        PetMood.Done => Loc.T("done"),
        PetMood.Error => Loc.T("something went wrong"),
        _ => Loc.T("idle"),
    };

    /// <summary>The pet's tooltip: how it is, and what it does.</summary>
    public string PetTip => AccessibleName + "\n" + Loc.T("Click to message omp · drag to move");

    /// <summary>
    /// From each snapshot the window applies: the agent's state (held a moment before the pet shows it), a failed
    /// tool (a short wince), a run's end: finished (joy) or stopped (back to idle, no joy).
    /// </summary>
    internal void OnSnapshot(SessionSnapshot s)
    {
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
        // Stopped by the user: omp went through Aborting, or the run's last answer or tool says so (a quick stop leaves
        // no turn summary)
        if (_host.Phase == SessionPhase.Aborting) _stopping = true;
        var ended = primed && s.RunsEnded > _runsSeen && _host.Phase == SessionPhase.Ready;
        var interrupted = ended && (_stopping || LastRunInterrupted(s));
        if (s.RunsEnded > _runsSeen) _stopping = false;
        _runsSeen = Math.Max(_runsSeen, s.RunsEnded);
        if (failed is not null)
        {
            _lastFailedTool = failed;
            Oni.ToolFailed();
        }
        Oni.SetBase(PetMoods.BaseMood(s, _host.Phase, _host.HasDialog));
        if (interrupted) Oni.Aborted();
        else if (ended) Oni.Done();
        SyncMood();
    }

    private bool _stopping;

    /// <summary>The last run was stopped: its summary says so, or its last answer was aborted, or a tool of it was cut short.</summary>
    private static bool LastRunInterrupted(SessionSnapshot s)
    {
        for (var i = s.Items.Count - 1; i >= 0; i--)
        {
            switch (s.Items[i])
            {
                case TurnEndItem end: return end.Interrupted;
                case AssistantItem { StopReason: "aborted" }: return true;
                case ToolItem { Status: ToolStatus.Interrupted }: return true;
                case UserItem: return false;
            }
        }
        return false;
    }

    /// <summary>The user is here (clicking the pet, opening its message box): the pet notices, or wakes up.</summary>
    internal void Poke()
    {
        Oni.Clicked();
        SyncMood();
    }

    private void SyncMood()
    {
        Mood = Oni.Mood;
        _moodTimer.Stop();
        if (!ShowPet || _shutDown) return;
        var now = Clock.GetUtcNow();
        if (Oni.NextChange() is { } next)
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

    /// <summary>A bubble with what the agent is doing, or a word from the pet (a click on the pet opens it with the pet's
    /// message box); again closes it.</summary>
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
        BubbleText = wasAsleep ? Loc.T("*yawns* I'm up!") : Say();
        IsBubbleOpen = true;
        if (!_shutDown) _bubbleTimer.Start();
    }

    private static string[] Quips =>
    [
        Loc.T("The mask stays on. The spirits are restless."),
        Loc.T("Ready when you are."),
        Loc.T("*the spirits circle, waiting for work*"),
        Loc.T("I was not asleep. I was meditating."),
    ];

    private string Say()
    {
        if (Mood == PetMood.Waiting || _host.HasDialog)
        {
            // On the desktop the question is in the app's window, not just above the pet
            var where = IsOnDesktop ? Loc.T("in its window") : Loc.T("just above");
            return _host.CurrentDialog?.IsApproval == true ? Loc.F("omp needs your approval, {0}.", where) : Loc.F("omp is asking you something, {0}.", where);
        }
        if (_host.HasError) return Activity();
        if (Mood == PetMood.Error && _lastFailedTool is { } tool) return Loc.F("Oops, {0} failed. omp is on it.", tool);
        if (_host.IsRunning) return Activity();
        if (Mood == PetMood.Done) return Loc.T("All done!");
        var quips = Quips;
        return quips[_quip++ % quips.Length];
    }

    private string Activity()
    {
        _bubbleIsActivity = true;
        return _host.StatusText;
    }

    /// <summary>A short line from the pet (a message sent, or not), closed after a while.</summary>
    private void ShowBubble(string text)
    {
        _bubbleTimer.Stop();
        _bubbleIsActivity = false;
        BubbleText = text;
        IsBubbleOpen = true;
        if (!_shutDown) _bubbleTimer.Start();
    }

    // ───────────────────────── The pet's message box ─────────────────────────

    /// <summary>The small message box beside the pet (a click on the pet opens it): Enter sends to the conversation.</summary>
    [ObservableProperty] private bool _isChatOpen;

    /// <summary>What is typed in it; kept when it closes without sending.</summary>
    [ObservableProperty] private string _chatText = "";

    public string ChatPlaceholder => _host.IsRunning ? Loc.T("Queue a message — Enter to send") : Loc.T("Message omp — Enter to send");

    partial void OnChatTextChanged(string value) => Oni.Typed();

    /// <summary>A click on the pet: its message box opens, and the pet says what omp is doing (or a word of its own).</summary>
    internal void OpenChat()
    {
        if (!IsOnScreen && !IsOnDesktop) return;
        IsChatOpen = true;
        if (!IsBubbleOpen) Talk();
        else Poke();
    }

    /// <summary>Closes the message box (and the pet's bubble with it).</summary>
    internal void CloseChat()
    {
        if (!IsChatOpen) return;
        IsChatOpen = false;
        _bubbleTimer.Stop();
        IsBubbleOpen = false;
    }

    /// <summary>
    /// Enter in the pet's message box: the text goes to the conversation as from the message box (a prompt, or queued
    /// while a run goes on; terminal-only commands handled in the window), and the box closes. When omp cannot take a
    /// message (starting, signing in, stopping) the pet says so and keeps the text.
    /// </summary>
    [RelayCommand]
    private void SendChat()
    {
        var text = ChatText.Trim();
        if (text.Length == 0) return;
        var queued = _host.IsRunning;
        if (!_host.SendFromPet(text))
        {
            ShowBubble(Loc.T("omp can't take a message right now."));
            return;
        }
        ChatText = "";
        IsChatOpen = false;
        ShowBubble(queued ? Loc.T("Queued for after this run.") : Loc.T("Sent!"));
    }

    private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.StatusText) when IsBubbleOpen && _bubbleIsActivity:
                BubbleText = _host.StatusText;
                break;
            case nameof(MainViewModel.ComposerText):
                Oni.Typed();
                break;
            case nameof(MainViewModel.ShowSetupScreen):
                OnPropertyChanged(nameof(IsOnScreen));
                OnPropertyChanged(nameof(IsOnDesktop));
                OnPropertyChanged(nameof(ComposerMargin));
                CloseIfHidden();
                break;
            case nameof(MainViewModel.IsRunning):
                OnPropertyChanged(nameof(ChatPlaceholder));
                break;
            case nameof(MainViewModel.IsSettingsOpen):
                OnPropertyChanged(nameof(IsOnScreen));
                if (_host.IsSettingsOpen) CloseIfHidden();
                break;
        }
    }

    private void Save()
    {
        if (_loading) return;
        _host.PersistPet(new PetOptions
        {
            Show = ShowPet,
            Size = Size,
            // Unset while it only follows the system, so a change there still reaches the pet
            ReduceMotion = _reduceMotionSaved,
            Roam = Roam,
            X = Position?.X,
            Y = Position?.Y,
            Desktop = DesktopPlace,
        });
    }

    // ───────────────────────── The settings' preview ─────────────────────────

    public IReadOnlyList<PetPreviewOptionViewModel> PreviewOptions { get; }

    /// <summary>The preview shows what the pet does when <paramref name="option"/> happens.</summary>
    [RelayCommand]
    private void ShowPreview(PetPreviewOptionViewModel? option)
    {
        if (option is null) return;
        foreach (var o in PreviewOptions) o.IsSelected = o == option;
        Preview.Show(option.Kind);
    }
}
