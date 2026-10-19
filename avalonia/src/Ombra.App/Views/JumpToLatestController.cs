using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Ombra.App.Controls;
using Ombra.App.Localization;
using Ombra.App.Platform;
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.App.Views;

/// <summary>A new-activity dot, not a count of tokens or synthetic fold rows. Older pages and layout changes are not new activity.</summary>
internal sealed class JumpToLatestController : IDisposable
{
    private readonly Button _button;
    private readonly ChatScrollController _scroll;
    private readonly MainViewModel _model;
    private readonly TextBlock _dot;
    private readonly DispatcherTimer _hide = new() { Interval = Motion.Base };
    private RowViewModel? _last;
    private TranscriptItem? _lastModel;

    public JumpToLatestController(Button button, ChatScrollController scroll, MainViewModel model)
    {
        _button = button;
        _scroll = scroll;
        _model = model;
        var icon = button.Content as Control;
        button.Content = null;
        _dot = new TextBlock { Text = "•", Classes = { "jump-new" }, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (icon is not null) content.Children.Add(icon);
        content.Children.Add(_dot);
        button.Content = content;
        button.Opacity = 0;
        _hide.Tick += Hide;
        scroll.FollowingChanged += FollowingChanged;
        model.TranscriptChanged += TranscriptChanged;
        model.Rows.CollectionChanged += RowsChanged;
        RememberLast();
        FollowingChanged();
    }

    internal bool HasNewActivity => _dot.IsVisible;

    private RowViewModel? Last()
    {
        for (var i = _model.Rows.Count - 1; i >= 0; i--)
            if (_model.Rows[i] is not TurnFoldRowViewModel and not StepGroupRowViewModel) return _model.Rows[i];
        return null;
    }

    private void RememberLast()
    {
        _last = Last();
        _lastModel = _last?.Model;
    }

    private void RowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Reset) return;
        SetNew(false);
        RememberLast();
    }

    private void TranscriptChanged()
    {
        var latest = Last();
        var changed = ReferenceEquals(latest, _last) ? !Equals(latest?.Model, _lastModel)
            : _last is not null && _model.Rows.Contains(_last);
        if (!_scroll.IsFollowing && changed && latest is not UserRowViewModel) SetNew(true);
        _last = latest;
        _lastModel = latest?.Model;
    }

    private void SetNew(bool value)
    {
        _dot.IsVisible = value;
        var label = Loc.T(value ? "New activity below — jump to latest" : "Jump to latest");
        ToolTip.SetTip(_button, label);
        AutomationProperties.SetName(_button, label);
    }

    private void FollowingChanged()
    {
        _hide.Stop();
        var show = !_scroll.IsFollowing;
        _button.IsHitTestVisible = show;
        _button.Focusable = show;
        if (show)
        {
            _button.IsVisible = true;
            _button.Opacity = 1;
        }
        else
        {
            SetNew(false);
            _button.Opacity = 0;
            if (ReducedMotion.System) _button.IsVisible = false;
            else if (_button.IsVisible) _hide.Start();
        }
    }

    private void Hide(object? sender, EventArgs e)
    {
        _hide.Stop();
        if (_scroll.IsFollowing) _button.IsVisible = false;
    }

    public void Dispose()
    {
        _hide.Stop();
        _scroll.FollowingChanged -= FollowingChanged;
        _model.TranscriptChanged -= TranscriptChanged;
        _model.Rows.CollectionChanged -= RowsChanged;
    }
}
