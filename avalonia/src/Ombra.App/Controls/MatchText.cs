using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Ombra.App.Controls;

/// <summary>
/// A title with the characters a search matched in bold (the command palette's entries): the matched indexes come from
/// the item, so a recycled row draws its new item's own.
/// </summary>
public sealed class MatchText : TextBlock
{
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<MatchText, string?>(nameof(Source));
    public static readonly StyledProperty<IReadOnlyList<int>?> MatchesProperty =
        AvaloniaProperty.Register<MatchText, IReadOnlyList<int>?>(nameof(Matches));

    public string? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    /// <summary>Indexes of <see cref="Source"/> to highlight, ascending.</summary>
    public IReadOnlyList<int>? Matches { get => GetValue(MatchesProperty); set => SetValue(MatchesProperty, value); }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == MatchesProperty) Build();
    }

    private void Build()
    {
        Inlines ??= [];
        Inlines.Clear();
        var text = Source ?? "";
        if (Matches is not { Count: > 0 } marks)
        {
            Inlines.Add(new Run(text));
            return;
        }
        var at = 0;
        var m = 0;
        while (at < text.Length)
        {
            var matched = m < marks.Count && marks[m] == at;
            var end = at;
            if (matched)
            {
                // A run of consecutive matched characters
                while (m < marks.Count && marks[m] == end && end < text.Length) { m++; end++; }
                Inlines.Add(new Run(text[at..end]) { FontWeight = FontWeight.Bold });
            }
            else
            {
                while (m < marks.Count && marks[m] < at) m++; // out of order or out of range: skipped
                end = m < marks.Count && marks[m] < text.Length ? marks[m] : text.Length;
                if (end <= at) end = at + 1;
                Inlines.Add(new Run(text[at..end]));
            }
            at = end;
        }
    }
}
