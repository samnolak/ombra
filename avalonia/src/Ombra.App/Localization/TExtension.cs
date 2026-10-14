using Avalonia.Markup.Xaml;

namespace Ombra.App.Localization;

/// <summary>XAML: <c>Text="{l:T 'New chat'}"</c> — the text in the app's language (<see cref="Loc.T"/>). Also for a
/// binding's format: <c>StringFormat={l:T 'Remove {0} from sidebar'}</c>.</summary>
public sealed class TExtension(string text) : MarkupExtension
{
    public string Text { get; } = text;

    /// <remarks>A text that starts with a placeholder is written <c>'{}{0} …'</c> (XAML would read <c>{0</c> as a markup
    /// extension); the <c>{}</c> is not part of the key.</remarks>
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text.StartsWith("{}", StringComparison.Ordinal) ? Text[2..] : Text);
}
