using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Layout;
using Ombra.App.Localization;
using Ombra.App.Nightly;

namespace Ombra.App.Controls;

/// <summary>
/// Draws a Nightly page's popups in Ombra (<see cref="NightlyPopup"/>): a <c>&lt;select&gt;</c> dropdown or form
/// autocomplete under its field, the context menu at the pointer, as the app's own menu cards (a <see cref="Flyout"/> of
/// <c>Button.menu-item</c> rows). The flyout never takes the focus from the pane. A dropdown or a menu takes the keys
/// meanwhile, as Gecko's own popups do (<see cref="HandleKey"/>: arrows, Enter, Esc, a letter to jump); autocomplete
/// leaves them to the page's field, which moves its selection itself and sends the popup again. A click on a row (or
/// Enter) answers its index; a click elsewhere, Esc, or another popup answers -1 (dismissed). One popup at a time.
/// </summary>
internal sealed class NightlyPopupPresenter
{
    private readonly Canvas _layer = new() { IsHitTestVisible = false };
    private readonly Border _anchor = new() { Width = 1, Height = 1 };
    private readonly Action<uint, int> _answer;
    private Shown? _shown;

    private sealed class Shown(uint id, Flyout flyout)
    {
        public uint Id { get; } = id;
        public Flyout Flyout { get; } = flyout;
        public NightlyPopup? Popup;
        public bool Answered;
        /// <summary>The row the keyboard is on (an index into the items); -1 for none.</summary>
        public int Highlight = -1;
        public readonly List<Button> Rows = [];
        public readonly Dictionary<int, Button> RowOf = [];
    }

    /// <param name="answer">The popup's id and the chosen item's index (-1: dismissed); sent to Nightly.</param>
    public NightlyPopupPresenter(Action<uint, int> answer)
    {
        _answer = answer;
        _layer.Children.Add(_anchor);
    }

    /// <summary>Over the pane, where the anchor (the page's field, or the pointer) is placed.</summary>
    public Control Layer => _layer;

    /// <summary>The popup on screen, if any.</summary>
    public NightlyPopup? Current => _shown?.Popup;

    /// <summary>The id of the popup on screen; 0 for none.</summary>
    public uint CurrentId => _shown?.Id ?? 0;

    /// <summary>The rows of the popup on screen (separators and headings aside), for tests and the acceptance check.</summary>
    public IReadOnlyList<Button> Rows => _shown?.Rows ?? [];

    /// <summary>A dropdown or a menu is shown: it takes the keys (<see cref="HandleKey"/>, <see cref="TypeAhead"/>).</summary>
    public bool TakesKeys => _shown?.Popup is { Kind: not NightlyPopup.PopupKind.Autocomplete };

    /// <summary>The item the keyboard is on (-1: none), for tests and the acceptance check.</summary>
    public int Highlight => _shown?.Highlight ?? -1;

    /// <summary>Shows popup <paramref name="id"/> at <paramref name="anchor"/> (pane coordinates), or updates it when it is the one shown.</summary>
    public void Show(uint id, NightlyPopup popup, Rect anchor)
    {
        if (_shown is { } current && current.Id == id)
        {
            // The page's selection (autocomplete), else where the keyboard was while that item is still there.
            var keep = popup.Kind != NightlyPopup.PopupKind.Autocomplete && current.Highlight >= 0 && current.Highlight < popup.Items.Count
                && popup.Items[current.Highlight].CanChoose ? current.Highlight : popup.Selected;
            current.Popup = popup;
            current.Flyout.Content = Build(current, popup, anchor.Width);
            SetHighlight(current, keep);
            return;
        }
        Close(); // Nightly closed the older one before it opened this one
        Canvas.SetLeft(_anchor, anchor.X);
        Canvas.SetTop(_anchor, anchor.Y);
        _anchor.Width = Math.Max(1, anchor.Width);
        _anchor.Height = Math.Max(1, anchor.Height);
        _layer.UpdateLayout();
        var flyout = new Flyout
        {
            ShowMode = FlyoutShowMode.Transient,
            OverlayDismissEventPassThrough = true,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
        };
        var shown = new Shown(id, flyout) { Popup = popup };
        flyout.Content = Build(shown, popup, anchor.Width);
        SetHighlight(shown, popup.Selected);
        flyout.Closed += (_, _) =>
        {
            if (_shown == shown) _shown = null;
            if (shown.Answered) return;
            shown.Answered = true;
            _answer(id, -1);
        };
        _shown = shown;
        flyout.ShowAt(_anchor);
    }

    /// <summary>The page closed popup <paramref name="id"/> itself: it goes without an answer.</summary>
    public void Close(uint id)
    {
        if (_shown?.Id == id) Close();
    }

    /// <summary>The popup goes without an answer (the page closed it, or its window is gone).</summary>
    public void Close()
    {
        if (_shown is not { } shown) return;
        _shown = null;
        shown.Answered = true;
        shown.Flyout.Hide();
    }

    /// <summary>The user dismissed the popup (Esc): the page hears -1. False when none is shown.</summary>
    public bool Dismiss()
    {
        if (_shown is not { } shown) return false;
        Answer(shown, -1);
        return true;
    }

    /// <summary>Chooses item <paramref name="index"/> of the popup shown, as a click on its row does.</summary>
    public bool Choose(int index)
    {
        if (_shown is not { Popup: { } popup } shown || index < 0 || index >= popup.Items.Count || !popup.Items[index].CanChoose) return false;
        Answer(shown, index);
        return true;
    }

    private void Answer(Shown shown, int index)
    {
        if (shown.Answered) return;
        shown.Answered = true;
        if (_shown == shown) _shown = null;
        _answer(shown.Id, index);
        shown.Flyout.Hide();
    }

    /// <summary>
    /// A key while a dropdown or a menu is shown (<see cref="TakesKeys"/>): the arrows, Home, End and the page keys move
    /// the highlight, Enter, Space and Tab choose it, Esc dismisses, a character jumps to the next item it starts.
    /// Every key is the popup's meanwhile, as in Gecko's own popups: the page sees none.
    /// </summary>
    public bool HandleKey(Key key, string? symbol)
    {
        if (_shown is not { Popup: { Kind: not NightlyPopup.PopupKind.Autocomplete } popup } shown) return false;
        switch (key)
        {
            case Key.Down: Move(shown, popup, shown.Highlight, +1, 1); break;
            case Key.Up: Move(shown, popup, shown.Highlight < 0 ? popup.Items.Count : shown.Highlight, -1, 1); break;
            case Key.PageDown: Move(shown, popup, shown.Highlight, +1, 10); break;
            case Key.PageUp: Move(shown, popup, shown.Highlight < 0 ? popup.Items.Count : shown.Highlight, -1, 10); break;
            case Key.Home: Move(shown, popup, -1, +1, 1); break;
            case Key.End: Move(shown, popup, popup.Items.Count, -1, 1); break;
            case Key.Escape: Answer(shown, -1); break;
            case Key.Enter or Key.Return or Key.Space or Key.Tab:
                if (shown.Highlight >= 0 && popup.Items[shown.Highlight].CanChoose) Answer(shown, shown.Highlight);
                else if (key is Key.Enter or Key.Return) Answer(shown, -1);
                break;
            default:
                if (symbol is { Length: > 0 } s && !char.IsControl(s[0])) TypeAhead(s);
                break;
        }
        return true;
    }

    /// <summary>Typed text while a dropdown or a menu is shown: the highlight goes to the next item that starts with it.</summary>
    public bool TypeAhead(string text)
    {
        if (_shown is not { Popup: { Kind: not NightlyPopup.PopupKind.Autocomplete } popup } shown) return false;
        var count = popup.Items.Count;
        for (var step = 1; step <= count; step++)
        {
            var i = ((shown.Highlight < 0 ? -1 : shown.Highlight) + step + count) % count;
            if (popup.Items[i].CanChoose && Label(popup.Items[i]).StartsWith(text, StringComparison.CurrentCultureIgnoreCase))
            {
                SetHighlight(shown, i);
                break;
            }
        }
        return true;
    }

    /// <summary>From <paramref name="from"/>, <paramref name="steps"/> choosable items in <paramref name="direction"/> (stops at the ends).</summary>
    private static void Move(Shown shown, NightlyPopup popup, int from, int direction, int steps)
    {
        var target = shown.Highlight;
        for (int i = from + direction, found = 0; i >= 0 && i < popup.Items.Count && found < steps; i += direction)
        {
            if (!popup.Items[i].CanChoose) continue;
            target = i;
            found++;
        }
        SetHighlight(shown, target);
    }

    private static void SetHighlight(Shown shown, int index)
    {
        if (shown.RowOf.TryGetValue(shown.Highlight, out var old)) old.Classes.Remove("current");
        shown.Highlight = shown.RowOf.ContainsKey(index) ? index : -1;
        if (!shown.RowOf.TryGetValue(shown.Highlight, out var row)) return;
        row.Classes.Add("current");
        if (row.IsAttachedToVisualTree()) row.BringIntoView();
        else row.AttachedToVisualTree += (_, _) => row.BringIntoView();
    }

    private Control Build(Shown shown, NightlyPopup popup, double anchorWidth)
    {
        shown.Rows.Clear();
        shown.RowOf.Clear();
        shown.Highlight = -1; // SetHighlight puts it on a row of these
        var panel = new StackPanel { Spacing = 2, MinWidth = popup.Kind == NightlyPopup.PopupKind.Menu ? 220 : Math.Max(160, anchorWidth) };
        for (var i = 0; i < popup.Items.Count; i++)
        {
            var item = popup.Items[i];
            switch (item.Kind)
            {
                case NightlyPopup.ItemKind.Separator:
                    panel.Children.Add(new Border { Classes = { "menu-separator" } });
                    continue;
                case NightlyPopup.ItemKind.Header:
                    panel.Children.Add(new TextBlock { Text = item.Label, Classes = { "menu-heading" } });
                    continue;
            }
            var label = Label(item);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
            if (item.Detail.Length > 0)
            {
                var detail = new TextBlock { Text = item.Detail, Classes = { "menu-shortcut" } };
                Grid.SetColumn(detail, 1);
                grid.Children.Add(detail);
            }
            var row = new Button
            {
                Classes = { "menu-item" },
                // The keyboard stays with the pane: a row never takes the focus.
                Focusable = false,
                IsEnabled = !item.Disabled,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = item.Indent ? new Thickness(12, 0, 0, 0) : default,
                Content = grid,
                Tag = i,
            };
            AutomationProperties.SetName(row, label);
            var index = i;
            row.Click += (_, _) => Answer(shown, index);
            shown.Rows.Add(row);
            shown.RowOf[i] = row;
            panel.Children.Add(row);
        }
        return new ScrollViewer { Content = panel, MaxHeight = 420, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    /// <summary>A row's text: the page's, or Ombra's label of a context menu command.</summary>
    public static string Label(NightlyPopup.Item item) => item.Command switch
    {
        "open-link" => Loc.T("Open link in new tab"),
        "copy-link" => Loc.T("Copy link"),
        "open-image" => Loc.T("Open image in new tab"),
        "copy-image" => Loc.T("Copy image"),
        "copy-image-link" => Loc.T("Copy image link"),
        "undo" => Loc.T("Undo"),
        "cut" => Loc.T("Cut"),
        "copy" => Loc.T("Copy"),
        "paste" => Loc.T("Paste"),
        "delete" => Loc.T("Delete"),
        "select-all" => Loc.T("Select all"),
        "back" => Loc.T("Back"),
        "forward" => Loc.T("Forward"),
        "reload" => Loc.T("Reload"),
        _ => item.Label.Length > 0 ? item.Label : item.Command ?? "",
    };
}
