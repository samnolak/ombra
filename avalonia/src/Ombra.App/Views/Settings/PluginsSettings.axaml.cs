using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App.ViewModels;

namespace Ombra.App.Views.Settings;

public partial class PluginsSettings : UserControl
{
    private readonly HashSet<Flyout> _menus = [];
    private MainViewModel? _vm;

    public PluginsSettings()
    {
        InitializeComponent();
        AddHandler(Button.ClickEvent, (_, e) => ConnectorsSettingsMenus.CloseOnChoice(e.Source, _menus), RoutingStrategies.Bubble, handledEventsToo: true);
        // The page's models exist with the window, so they hear the page being shown the first time.
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.Connectors.CopyRequested -= Copy;
                _vm.PluginsHub.SkillFolderRequested -= FocusSkillFolder;
            }
            _vm = DataContext as MainViewModel;
            if (_vm is not null)
            {
                _vm.Connectors.CopyRequested += Copy;
                _vm.PluginsHub.SkillFolderRequested += FocusSkillFolder;
            }
        };
    }

    private void Copy(string text) => ConnectorsSettingsMenus.Copy(this, text);

    /// <summary>Add → Skill folder: the caret in the folder field once the sources show.</summary>
    private void FocusSkillFolder() => Dispatcher.UIThread.Post(() =>
    {
        var box = this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.Name == "SkillFolderBox");
        if (box is null) return;
        box.BringIntoView();
        box.Focus();
    }, DispatcherPriority.Background);
}
