namespace Ombra.App.ViewModels;

/// <summary>One detail of a sent page comment (<see cref="SentCommentViewModel.Fields"/>): its name and value.</summary>
public sealed record SentCommentField(string Key, string Value);
