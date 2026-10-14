using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ombra.App.Controls;

/// <summary>
/// One line of the Files pane's Changes, coloured by its file's language as the conversation's diffs are
/// (SyntaxHighlighter), with the words that changed in it on the stronger added / removed tint (DiffWords). A virtualized
/// list recycles it, so the colours come from its own properties: the line, the file name, whether a block comment or
/// string was open where the line begins, its marks and whether it was removed. A theme change colours it again.
/// </summary>
public sealed class DiffLineText : SelectableTextBlock
{
    public static readonly StyledProperty<string?> LineProperty = AvaloniaProperty.Register<DiffLineText, string?>(nameof(Line));
    public static readonly StyledProperty<string?> FileNameProperty = AvaloniaProperty.Register<DiffLineText, string?>(nameof(FileName));
    public static readonly StyledProperty<bool> StartsInBlockProperty = AvaloniaProperty.Register<DiffLineText, bool>(nameof(StartsInBlock));
    public static readonly StyledProperty<IReadOnlyList<DiffWords.Span>?> MarksProperty =
        AvaloniaProperty.Register<DiffLineText, IReadOnlyList<DiffWords.Span>?>(nameof(Marks));
    public static readonly StyledProperty<bool> IsRemovedProperty = AvaloniaProperty.Register<DiffLineText, bool>(nameof(IsRemoved));

    public string? Line { get => GetValue(LineProperty); set => SetValue(LineProperty, value); }
    public string? FileName { get => GetValue(FileNameProperty); set => SetValue(FileNameProperty, value); }
    public bool StartsInBlock { get => GetValue(StartsInBlockProperty); set => SetValue(StartsInBlockProperty, value); }
    /// <summary>The changed words (null: none).</summary>
    public IReadOnlyList<DiffWords.Span>? Marks { get => GetValue(MarksProperty); set => SetValue(MarksProperty, value); }
    /// <summary>The line was removed: its marks are red, else green.</summary>
    public bool IsRemoved { get => GetValue(IsRemovedProperty); set => SetValue(IsRemovedProperty, value); }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LineProperty || change.Property == FileNameProperty || change.Property == StartsInBlockProperty
            || change.Property == MarksProperty || change.Property == IsRemovedProperty
            || change.Property == ThemeVariantScope.ActualThemeVariantProperty)
            Colour();
    }

    private void Colour()
    {
        Inlines ??= [];
        Inlines.Clear();
        var inBlock = StartsInBlock;
        var mark = Marks is { Count: > 0 } && Application.Current is { } app
            && app.TryGetResource(IsRemoved ? "GuiDiffRemovedWordBg" : "GuiDiffAddedWordBg", app.ActualThemeVariant, out var b) ? b as IBrush : null;
        DiffWords.AddRuns(Inlines, Line ?? "", SyntaxHighlighter.ForName(FileName), ref inBlock, Marks, mark);
    }
}
