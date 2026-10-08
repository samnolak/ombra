using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>
    /// A message taken from the message box that omp never got: omp was between the stop and the start of a restart
    /// (an approval-mode change, Recover, Save and restart) when it was sent. Its text and images go back into the box,
    /// before anything typed since, so sending again is one key.
    /// </summary>
    private void PutBackUnsent((string Text, ImageAttachment[] Images) content)
    {
        ComposerText = ComposerText.Length == 0 ? content.Text : content.Text + "\n" + ComposerText;
        foreach (var image in content.Images) TryAddImage(image, image.Name);
        ComposerMessage = "omp is restarting, so the message wasn't sent. It is back in the box: send it again once omp is ready.";
        CaretToEndRequested?.Invoke();
        FocusComposerRequested?.Invoke();
    }
}
