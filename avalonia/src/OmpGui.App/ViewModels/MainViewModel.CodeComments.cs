using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OmpGui.App.ViewModels;

/// <summary>A comment on a line of the Files pane's Changes, waiting in the message box as a chip until it is sent.</summary>
public sealed partial class CodeCommentViewModel(string path, int? line, bool removedLine, string lineText, string comment, MainViewModel owner) : ObservableObject
{
    /// <summary>Relative to the project, '/'-separated.</summary>
    public string Path { get; } = path;
    public int? Line { get; } = line;
    /// <summary>The line is one the change took out (its number is in the last commit).</summary>
    public bool IsRemovedLine { get; } = removedLine;
    public string LineText { get; } = lineText;
    public string Comment { get; } = comment;

    /// <summary>"app.py:41" on the chip.</summary>
    public string Where => System.IO.Path.GetFileName(Path) + (Line is { } n ? ":" + n.ToString(CultureInfo.InvariantCulture) : "");

    public string Tooltip => $"{Path}{(Line is { } n ? $", line {n}" : "")}{(IsRemovedLine ? " (removed)" : "")}\n{LineText.Trim()}\n\n{Comment}";

    [RelayCommand] private void Remove() => owner.RemoveCodeComment(this);
}

/// <summary>Comments on the code (the Files pane's Changes): chips in the message box, sent after the message.</summary>
public sealed partial class MainViewModel
{
    public const int MaxCodeComments = 20;

    /// <summary>How the comments block begins in a sent message (the conversation shows it as a chip).</summary>
    public const string CodeCommentsMarker = "Comments on the code, ";

    /// <summary>The first line when comments go without words of the user's own.</summary>
    public const string CodeCommentsOnlyLine = "Please make the changes my comments on the code ask for.";
    public const string AllCommentsOnlyLine = "Please make the changes my comments ask for.";

    public ObservableCollection<CodeCommentViewModel> CodeComments { get; } = [];
    public bool HasCodeComments => CodeComments.Count > 0;

    /// <summary>A comment on a line goes in the message box as a chip; false when there are too many already.</summary>
    internal bool AddCodeComment(string path, int? line, bool removedLine, string lineText, string comment)
    {
        if (CodeComments.Count >= MaxCodeComments)
        {
            ComposerMessage = $"At most {MaxCodeComments} comments on the code at a time: send or remove some first.";
            return false;
        }
        CodeComments.Add(new CodeCommentViewModel(path, line, removedLine, lineText, comment, this));
        CodeCommentsChanged();
        return true;
    }

    internal void RemoveCodeComment(CodeCommentViewModel c)
    {
        CodeComments.Remove(c);
        CodeCommentsChanged();
        FocusComposerRequested?.Invoke();
    }

    private void CodeCommentsChanged()
    {
        OnPropertyChanged(nameof(HasCodeComments));
        AttachmentsChanged();
    }

    /// <summary>
    /// The message with the comments after it (code first, then the page), clearing both. Comments sent alone get a first
    /// line that says what to do (it is also what names a new session).
    /// </summary>
    private string WithComments(string text)
    {
        var code = BuildCodeCommentsPrompt(CodeComments);
        var page = TakeAnnotations();
        if (CodeComments.Count > 0)
        {
            CodeComments.Clear();
            CodeCommentsChanged();
        }
        if (code.Length == 0 && page.Length == 0) return text;
        var first = text.Length > 0 ? text : code.Length == 0 ? PreviewViewModel.CommentsOnlyLine : page.Length == 0 ? CodeCommentsOnlyLine : AllCommentsOnlyLine;
        return string.Join("\n\n", new[] { first, code, page }.Where(s => s.Length > 0));
    }

    /// <summary>The comments as a message for the agent: each with its file, line and the line's text.</summary>
    public static string BuildCodeCommentsPrompt(IReadOnlyList<CodeCommentViewModel> comments)
    {
        if (comments.Count == 0) return "";
        var sb = new StringBuilder(CodeCommentsMarker + "left on the uncommitted changes (the line's text is quoted):\n");
        for (var i = 0; i < comments.Count; i++)
        {
            var c = comments[i];
            var where = c.Line is { } n ? $"{c.Path}, line {n}{(c.IsRemovedLine ? " of the last commit (a removed line)" : "")}" : c.Path;
            sb.Append(CultureInfo.InvariantCulture, $"\n{i + 1}. {where}: {c.Comment}\n");
            sb.Append(CultureInfo.InvariantCulture, $"   > {c.LineText.Trim()}\n");
        }
        sb.Append("\nMake the changes these comments ask for.");
        return sb.ToString();
    }
}
