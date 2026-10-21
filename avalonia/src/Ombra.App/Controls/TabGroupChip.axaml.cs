using System.Windows.Input;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Ombra.App.ViewModels;

namespace Ombra.App.Controls;

/// <summary>
/// A site group's chip in a tab strip and its list under it (TabGroupChip.axaml; <see cref="Group"/>).
/// The list opens as the KSO kiosk's language list does: the card settles down into place and its rows follow it in,
/// 40 ms apart, on one strong ease-out; it closes at once. Reduced motion: fades only.
/// </summary>
public partial class TabGroupChip : UserControl
{
    public static readonly StyledProperty<TabSiteGroup?> GroupProperty = AvaloniaProperty.Register<TabGroupChip, TabSiteGroup?>(nameof(Group));
    public static readonly StyledProperty<bool> IsActiveProperty = AvaloniaProperty.Register<TabGroupChip, bool>(nameof(IsActive));
    public static readonly StyledProperty<ICommand?> SelectCommandProperty = AvaloniaProperty.Register<TabGroupChip, ICommand?>(nameof(SelectCommand));
    public static readonly StyledProperty<ICommand?> CloseCommandProperty = AvaloniaProperty.Register<TabGroupChip, ICommand?>(nameof(CloseCommand));

    /// <summary>The site group the chip stands for.</summary>
    public TabSiteGroup? Group { get => GetValue(GroupProperty); set => SetValue(GroupProperty, value); }

    /// <summary>The page shown is one of the group's (the chip is lifted as the shown tab is).</summary>
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    /// <summary>Shows a page picked in the list (parameter: the <see cref="PreviewTab"/>).</summary>
    public ICommand? SelectCommand { get => GetValue(SelectCommandProperty); set => SetValue(SelectCommandProperty, value); }

    /// <summary>Closes a page from its row's × (parameter: the <see cref="PreviewTab"/>).</summary>
    public ICommand? CloseCommand { get => GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }

    // The kiosk list's curve (cubic-bezier(.22, 1, .36, 1)): fast out of the gate, a long soft landing
    private static readonly SplineEasing Out = new(0.22, 1, 0.36, 1);
    private static readonly TimeSpan CardTime = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RowTime = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan RowStagger = TimeSpan.FromMilliseconds(40);
    private const int StaggeredRows = 6;

    public TabGroupChip() => InitializeComponent();

    private void OnChipClick(object? sender, RoutedEventArgs e)
    {
        if (Group is { } group) group.IsOpen = !group.IsOpen;
    }

    private void OnRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: PreviewTab page }) return;
        if (Group is { } group) group.IsOpen = false;
        if (SelectCommand?.CanExecute(page) == true) SelectCommand.Execute(page);
    }

    private void OnRowCloseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PreviewTab page } && CloseCommand?.CanExecute(page) == true) CloseCommand.Execute(page);
    }

    private void OnListOpened(object? sender, EventArgs e)
    {
        var reduced = TopLevel.GetTopLevel(this) is Window w && w.Classes.Contains("reduced-motion");
        _ = Enter(ListCard, TimeSpan.Zero, CardTime, reduced ? 0 : -6);
        var i = 0;
        foreach (var row in Rows.GetRealizedContainers())
        {
            if (row is not ContentPresenter { Child: Control content }) continue;
            var delay = TimeSpan.FromTicks(RowStagger.Ticks * Math.Min(i++, StaggeredRows)) + TimeSpan.FromMilliseconds(40);
            _ = Enter(content, delay, RowTime, reduced ? 0 : -8);
        }
    }

    /// <summary>Fades <paramref name="target"/> in from <paramref name="fromY"/> px above its place (0: a fade only).</summary>
    private static Task Enter(Control target, TimeSpan delay, TimeSpan duration, double fromY)
    {
        var animation = new Animation
        {
            Duration = duration,
            Delay = delay,
            Easing = Out,
            FillMode = FillMode.Backward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d), new Setter(TranslateTransform.YProperty, fromY) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) } },
            },
        };
        return animation.RunAsync(target);
    }
}
