using Avalonia;
using Avalonia.Controls;
using Ombra.ClientCore;

namespace Ombra.App.Controls;

/// <summary>
/// The files one tool call changed, as one <see cref="DiffView"/> card each, stacked (a patch over several files shows
/// each file under its own header). Cards are kept while their file stays at the same place in the list, so a card the
/// user unfolded stays unfolded when the row updates.
/// </summary>
public sealed class DiffStack : StackPanel
{
    public static readonly StyledProperty<IReadOnlyList<FileChange>?> ChangesProperty =
        AvaloniaProperty.Register<DiffStack, IReadOnlyList<FileChange>?>(nameof(Changes));
    public static readonly StyledProperty<System.Windows.Input.ICommand?> OpenFileCommandProperty =
        AvaloniaProperty.Register<DiffStack, System.Windows.Input.ICommand?>(nameof(OpenFileCommand));
    public static readonly StyledProperty<System.Windows.Input.ICommand?> CollapseCommandProperty =
        AvaloniaProperty.Register<DiffStack, System.Windows.Input.ICommand?>(nameof(CollapseCommand));

    public IReadOnlyList<FileChange>? Changes { get => GetValue(ChangesProperty); set => SetValue(ChangesProperty, value); }
    /// <summary>Opens a changed file (the Files pane); its parameter is a <see cref="FileLink"/>.</summary>
    public System.Windows.Input.ICommand? OpenFileCommand { get => GetValue(OpenFileCommandProperty); set => SetValue(OpenFileCommandProperty, value); }
    /// <summary>Folds the cards away (the tool row's line): each card's header has a chevron for it.</summary>
    public System.Windows.Input.ICommand? CollapseCommand { get => GetValue(CollapseCommandProperty); set => SetValue(CollapseCommandProperty, value); }

    protected override Type StyleKeyOverride => typeof(StackPanel);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChangesProperty) Sync();
        else if (change.Property == OpenFileCommandProperty)
            foreach (var card in Children.OfType<DiffView>()) card.OpenFileCommand = OpenFileCommand;
        else if (change.Property == CollapseCommandProperty)
            foreach (var card in Children.OfType<DiffView>()) card.CollapseCommand = CollapseCommand;
    }

    private void Sync()
    {
        var changes = Changes ?? [];
        while (Children.Count > changes.Count) Children.RemoveAt(Children.Count - 1);
        for (var i = 0; i < changes.Count; i++)
        {
            if (i < Children.Count) ((DiffView)Children[i]).Change = changes[i];
            else Children.Add(new DiffView { Classes = { "tool-diff" }, Change = changes[i], OpenFileCommand = OpenFileCommand, CollapseCommand = CollapseCommand });
        }
    }
}
