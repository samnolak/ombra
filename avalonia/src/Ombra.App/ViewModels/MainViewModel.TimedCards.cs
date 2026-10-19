using System.Diagnostics;
using Avalonia.Threading;

namespace Ombra.App.ViewModels;

/// <summary>
/// Cards that close by themselves: brief notes (4 s) and Undo offers (8 s). One shows at a time and up to
/// <see cref="TimedCardQueueLimit"/> more wait their turn (the oldest waiting one gives way); the same note again
/// extends the one showing instead of stacking. The clock pauses while the card is hovered or has the keyboard
/// (<see cref="HoldTimedCard"/>, from the window), and gives the reader a moment after the pointer leaves.
/// </summary>
public sealed partial class MainViewModel
{
    internal const int TimedCardQueueLimit = 2;

    /// <summary>How long a brief note stays (tests shorten it).</summary>
    internal TimeSpan BriefCardDuration { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>How long an Undo offer stays (tests shorten it).</summary>
    internal TimeSpan UndoCardDuration { get; set; } = TimeSpan.FromSeconds(8);

    private readonly List<(SessionCardViewModel Card, TimeSpan Duration, bool Urgent)> _timedQueue = [];
    private SessionCardViewModel? _timedCard;
    private TimeSpan _timedDuration, _timedLeft;
    private long _timedSince;
    private bool _timedHeld, _timedUrgent;

    /// <summary>The cards waiting behind the one showing.</summary>
    internal int QueuedTimedCards => _timedQueue.Count;

    private void ShowTimedCard(SessionCardViewModel card, TimeSpan duration) => ShowTimedCard(card, duration, urgent: false);

    /// <summary>
    /// A note that needs the user (a permission, an error that blocks what they did): it shows at once, never waits
    /// behind informational notes nor counts against the queue; the note it interrupts resumes after it.
    /// </summary>
    private void ShowUrgentBrief(string iconKey, string title, string message, string detail = "") =>
        ShowTimedCard(new SessionCardViewModel("brief", iconKey, title, message) { Detail = detail }, BriefCardDuration, urgent: true);

    private void ShowTimedCard(SessionCardViewModel card, TimeSpan duration, bool urgent)
    {
        if (_timedCard is { } shown && ReferenceEquals(SessionCard, shown) && SameNotice(shown, card))
        {
            StartTimedClock(Max(duration, _timedLeft));
            return;
        }
        if (urgent) _timedQueue.RemoveAll(q => SameNotice(q.Card, card));
        else if (_timedQueue.Any(q => SameNotice(q.Card, card))) return;
        if (_timedCard is { } current && ReferenceEquals(SessionCard, current))
        {
            if (urgent)
            {
                // The interrupted note goes back to the front with the time it had left
                var left = _timedHeld || _cardTimer is not { IsEnabled: true } ? _timedLeft : _timedLeft - Stopwatch.GetElapsedTime(_timedSince);
                _timedQueue.Insert(0, (current, Max(left, _timedDuration * 0.4), _timedUrgent));
                StopTimedCard();
                PresentTimedCard(card, duration, urgent);
                return;
            }
            _timedQueue.Add((card, duration, false));
            // Informational notes wait two deep; the oldest waiting one gives way (urgent ones never count)
            while (_timedQueue.Count(q => !q.Urgent) > TimedCardQueueLimit)
                _timedQueue.RemoveAt(_timedQueue.FindIndex(q => !q.Urgent));
            return;
        }
        DropTimedCards();
        PresentTimedCard(card, duration, urgent);
    }

    private void PresentTimedCard(SessionCardViewModel card, TimeSpan duration, bool urgent)
    {
        PresentCard(card);
        _timedCard = card;
        _timedDuration = duration;
        _timedUrgent = urgent;
        StartTimedClock(duration);
    }

    private void ShowNextTimedCard()
    {
        if (_timedQueue.Count == 0) return;
        var (card, duration, urgent) = _timedQueue[0];
        _timedQueue.RemoveAt(0);
        PresentTimedCard(card, duration, urgent);
    }

    /// <summary>The pointer is over the card or the keyboard is in it: its clock waits.</summary>
    internal void HoldTimedCard(bool held)
    {
        if (_timedHeld == held) return;
        _timedHeld = held;
        if (_timedCard is null) return;
        if (held)
        {
            if (_cardTimer is not { IsEnabled: true }) return;
            _timedLeft -= Stopwatch.GetElapsedTime(_timedSince);
            _cardTimer.Stop();
        }
        // Left with a moment to finish reading, not closing under the pointer as it goes
        else StartTimedClock(Max(_timedLeft, _timedDuration * 0.4));
    }

    private void StartTimedClock(TimeSpan left)
    {
        _timedLeft = left;
        _timedSince = Stopwatch.GetTimestamp();
        _cardTimer?.Stop();
        if (_timedHeld) return;
        var card = _timedCard;
        _cardTimer = new DispatcherTimer(left, DispatcherPriority.Background, (_, _) =>
        {
            _cardTimer?.Stop();
            if (card is not null && ReferenceEquals(SessionCard, card)) CloseSessionCard();
        });
        _cardTimer.Start();
    }

    private void StopTimedCard()
    {
        _cardTimer?.Stop();
        _cardTimer = null;
        _timedCard = null;
    }

    /// <summary>The notes belong to their moment: a switch of chats or a card that asks drops them.</summary>
    private void DropTimedCards()
    {
        StopTimedCard();
        _timedQueue.Clear();
    }

    private static bool SameNotice(SessionCardViewModel a, SessionCardViewModel b) =>
        a.Kind == b.Kind && a.Title == b.Title && a.Message == b.Message && a.Detail == b.Detail && a.IconKey == b.IconKey;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
