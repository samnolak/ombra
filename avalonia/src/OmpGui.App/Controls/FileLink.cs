namespace OmpGui.App.Controls;

/// <summary>
/// A file named in the conversation (a change card's path, a tool's file) to open in the Files pane, at
/// <paramref name="Line"/> when it is known. <paramref name="Path"/> is as the agent wrote it (often relative to the project).
/// </summary>
public sealed record FileLink(string Path, int? Line = null);
