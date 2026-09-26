using Avalonia.Controls;

namespace OmpGui.App.Views.Panes;

/// <summary>The header's project name and its menu (which closes once a choice is made: ChoiceMenu).</summary>
public partial class ProjectMenuButton : UserControl
{
    public ProjectMenuButton() => InitializeComponent();

    /// <summary>The button that opens the menu (tests open it).</summary>
    internal Button Button => MenuButton;
}
