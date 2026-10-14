using Ombra.App.Localization;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// Settings → Permissions: the "don't ask again" rules given on approval cards (this session, this project, always),
/// each with Remove. A removed rule is out of force at once: omp's next matching request is asked again.
/// </summary>
public sealed partial class PermissionRulesViewModel : WorkspacePageViewModel
{
    public PermissionRulesViewModel(MainViewModel main) : base(main, "permissions")
    {
        main.ApprovalRules.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    public ObservableCollection<PermissionRuleRowViewModel> Rules { get; } = [];

    [ObservableProperty] private bool _isEmpty = true;

    /// <summary>The saved rules could not be read (the client's settings file does not parse).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    private string _loadError = "";

    public bool HasLoadError => LoadError.Length > 0;

    /// <summary>Reads the saved rules again (another window may have changed them).</summary>
    public override Task LoadAsync()
    {
        Main.ApprovalRules.Reload(); // raises Changed → Refresh
        Refresh();
        return Task.CompletedTask;
    }

    private void Refresh()
    {
        var (cwd, session) = Main.RuleContext;
        var grants = Main.ApprovalRules.Grants;
        Rules.Clear();
        foreach (var g in grants) Rules.Add(new PermissionRuleRowViewModel(g, this, cwd, session));
        IsEmpty = Rules.Count == 0;
        LoadError = Main.ApprovalRules.LoadError ?? "";
    }

    internal void Remove(PermissionRuleRowViewModel row)
    {
        try
        {
            Main.ApprovalRules.Remove(row.Grant);
            Say(Loc.F("Removed. omp asks again for {0}.", row.Grant.Rule.Description));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Say(Loc.T("Not removed: ") + e.Message, error: true);
        }
        Refresh();
    }
}

public sealed partial class PermissionRuleRowViewModel(ApprovalGrant grant, PermissionRulesViewModel owner, string? cwd, string? session) : ObservableObject
{
    public ApprovalGrant Grant { get; } = grant;

    /// <summary>The rule as written (<c>bash(npm test:*)</c>).</summary>
    public string RuleText => Grant.Rule.Text;

    /// <summary>The rule in words, capitalized: "Commands starting with “npm test”".</summary>
    public string Description => Grant.Rule.Description is { Length: > 0 } d ? char.ToUpperInvariant(d[0]) + d[1..] : "";

    public string ScopeLabel => Grant.Scope switch
    {
        ApprovalScope.Session => Grant.Session == session ? Loc.T("This session") : Loc.T("Earlier session"),
        ApprovalScope.Project => SessionCatalog.SamePath(Grant.Project, cwd) ? Loc.T("This project") : Loc.T("Project"),
        _ => Loc.T("All projects"),
    };

    /// <summary>Where it applies, in words.</summary>
    public string Detail => Grant.Scope switch
    {
        ApprovalScope.Session => Loc.T("Until the app quits"),
        ApprovalScope.Project => GitProbe.ShownPath(Grant.Project ?? ""),
        _ => Loc.T("Every project"),
    };

    [RelayCommand] private void Remove() => owner.Remove(this);
}
