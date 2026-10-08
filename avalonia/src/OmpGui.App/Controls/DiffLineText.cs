using Avalonia;
using Avalonia.Controls;

namespace OmpGui.App.Controls;

/// <summary>
/// One line of the Files pane's Changes, coloured by its file's language as the conversation's diffs are
/// (SyntaxHighlighter). A virtualized list recycles it, so the colours come from its own properties: the line, the file
/// name, and whether a block comment or string was open where the line begins. A theme change colours it again.
/// </summary>
public sealed class DiffLineText : SelectableTextBlock
{
    public static readonly StyledProperty<string?> LineProperty = AvaloniaProperty.Register<DiffLineText, string?>(nameof(Line));
    public static readonly StyledProperty<string?> FileNameProperty = AvaloniaProperty.Register<DiffLineText, string?>(nameof(FileName));
    public static readonly StyledProperty<bool> StartsInBlockProperty = AvaloniaProperty.Register<DiffLineText, bool>(nameof(StartsInBlock));

    public string? Line { get => GetValue(LineProperty); set => SetValue(LineProperty, value); }
    public string? FileName { get => GetValue(FileNameProperty); set => SetValue(FileNameProperty, value); }
    public bool StartsInBlock { get => GetValue(StartsInBlockProperty); set => SetValue(StartsInBlockProperty, value); }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LineProperty || change.Property == FileNameProperty || change.Property == StartsInBlockProperty
            || change.Property == ThemeVariantScope.ActualThemeVariantProperty)
            Colour();
    }

    private void Colour()
    {
        Inlines ??= [];
        Inlines.Clear();
        var inBlock = StartsInBlock;
        SyntaxHighlighter.AddRuns(Inlines, Line ?? "", SyntaxHighlighter.ForName(FileName), ref inBlock);
    }
}
