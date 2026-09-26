using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Controls;

/// <summary>
/// The pet above the message box (DataContext = <see cref="MainViewModel"/>). A click on the pet opens its speech
/// bubble and makes it hop for joy; neither the pet nor the bubble can take focus, so the keyboard stays in the message box.
/// It tells the view model its window's height, so a short window gives the pet's band back to the conversation.
/// </summary>
public sealed partial class PetPerch : UserControl
{
    public PetPerch()
    {
        InitializeComponent();
        Pet.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(Pet).Properties.IsLeftButtonPressed || DataContext is not MainViewModel vm) return;
            vm.Pets.TalkCommand.Execute(null);
            Pet.Cheer();
            e.Handled = true;
        };
    }

    private TopLevel? _top;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        if (_top is not null) _top.PropertyChanged += OnTopChanged;
        ReportHeight();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_top is not null) _top.PropertyChanged -= OnTopChanged;
        _top = null;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ReportHeight();
    }

    private void OnTopChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ClientSizeProperty) ReportHeight();
    }

    private void ReportHeight()
    {
        if (_top is { } top && DataContext is MainViewModel vm && top.ClientSize.Height > 0) vm.Pets.WindowHeight = top.ClientSize.Height;
    }
}
