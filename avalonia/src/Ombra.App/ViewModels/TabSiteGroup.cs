using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Ombra.App.ViewModels;

/// <summary>
/// Pages of one site that the tab strips (the right panel's, the Browser page's) show as one chip with a list under
/// it, as Nightly's automatic groups do (docs/PRODUCT_DIRECTIONS.md B1): it forms at <see cref="FormAt"/> pages and
/// stays until only <see cref="DissolveAt"/> are left, so closing and reopening one page does not make it flicker.
/// The strip has no room to the right for a group's tabs, so they open downward.
/// </summary>
public sealed partial class TabSiteGroup : ObservableObject
{
    public const int FormAt = 4;
    public const int DissolveAt = 2;

    public TabSiteGroup(string key) => Key = key;

    /// <summary>The site: eTLD+1 as near as a list-free guess gets (<c>codepen.io</c>, <c>bbc.co.uk</c>), or host and
    /// port for a local server or an address (<c>localhost:4321</c>), whose ports are different apps.</summary>
    public string Key { get; }

    /// <summary>The pages in the strip's order.</summary>
    public ObservableCollection<PreviewTab> Members { get; } = [];

    /// <summary>What the chip says: the pages' title when they all have the same one (four "Kiosk · Mix" pages), else the site.</summary>
    [ObservableProperty] private string _label = "";

    [ObservableProperty] private int _count;

    /// <summary>One of its pages is the one shown.</summary>
    [ObservableProperty] private bool _containsActive;

    /// <summary>One of its pages is loading.</summary>
    [ObservableProperty] private bool _isLoading;

    /// <summary>Its list is open.</summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>The whole label and how many pages, for the chip's tooltip.</summary>
    public string ToolTip => Ombra.App.Localization.Loc.F("{0} · {1} tabs", Label, Count);

    partial void OnLabelChanged(string value) => OnPropertyChanged(nameof(ToolTip));
    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(ToolTip));

    internal void SetMembers(IReadOnlyList<PreviewTab> pages)
    {
        if (!Members.SequenceEqual(pages))
        {
            foreach (var gone in Members.Except(pages).ToList()) gone.PropertyChanged -= OnMemberChanged;
            foreach (var added in pages.Except(Members)) added.PropertyChanged += OnMemberChanged;
            Members.Clear();
            foreach (var p in pages) Members.Add(p);
        }
        Count = Members.Count;
        Refresh();
    }

    internal void Detach()
    {
        foreach (var m in Members) m.PropertyChanged -= OnMemberChanged;
        Members.Clear();
        IsOpen = false;
    }

    private void OnMemberChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PreviewTab.IsActive) or nameof(PreviewTab.Title) or nameof(PreviewTab.IsLoading)) Refresh();
    }

    private void Refresh()
    {
        ContainsActive = Members.Any(m => m.IsActive);
        IsLoading = Members.Any(m => m.IsLoading);
        var title = Members.Count > 0 ? Members[0].Title : "";
        Label = title.Length > 0 && Members.All(m => m.Title == title) ? title : Key;
    }

    // Second-level labels under which a country's names register (bbc.co.uk, not co.uk): a guess without the public
    // suffix list, good enough to put one site's pages together
    private static readonly HashSet<string> SecondLevel = ["co", "com", "org", "net", "ac", "gov", "edu", "ne", "or", "go"];

    /// <summary>The group a page belongs to; null for one that is not grouped (not http/https, no host).</summary>
    public static string? SiteKey(Uri? url)
    {
        if (url is null || url.Scheme is not ("http" or "https") || url.Host.Length == 0) return null;
        var host = url.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || IPAddress.TryParse(host.Trim('[', ']'), out _))
            return url.IsDefaultPort ? host : host + ":" + url.Port.ToString(CultureInfo.InvariantCulture);
        var labels = host.Split('.');
        if (labels.Length <= 2) return host;
        var take = labels[^1].Length == 2 && SecondLevel.Contains(labels[^2]) ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }
}

/// <summary>Keeps <see cref="PreviewTab.SiteGroup"/> of a browser's pages up to date (PreviewViewModel).</summary>
internal sealed class TabSiteGrouping
{
    private readonly Dictionary<string, TabSiteGroup> _groups = new(StringComparer.Ordinal);

    /// <summary>The pages that count: in the strip, with a site, not a pop-up (it belongs next to its opener).</summary>
    private static string? KeyOf(PreviewTab tab) => tab.ShowsInStrip && !tab.IsPopup && !tab.IsClosed ? TabSiteGroup.SiteKey(tab.Url) : null;

    public void Regroup(IReadOnlyList<PreviewTab> tabs)
    {
        var byKey = new Dictionary<string, List<PreviewTab>>(StringComparer.Ordinal);
        foreach (var tab in tabs)
            if (KeyOf(tab) is { } key)
            {
                if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = [];
                list.Add(tab);
            }
        foreach (var key in _groups.Keys.ToList())
            if (!byKey.TryGetValue(key, out var pages) || pages.Count <= TabSiteGroup.DissolveAt)
            {
                _groups[key].Detach();
                _groups.Remove(key);
            }
        foreach (var (key, pages) in byKey)
        {
            if (!_groups.TryGetValue(key, out var group))
            {
                if (pages.Count < TabSiteGroup.FormAt) continue;
                _groups[key] = group = new TabSiteGroup(key);
            }
            group.SetMembers(pages);
        }
        foreach (var tab in tabs)
        {
            var group = KeyOf(tab) is { } key ? _groups.GetValueOrDefault(key) : null;
            tab.SiteGroup = group;
            tab.IsGroupHead = group is not null && group.Members[0] == tab;
        }
    }
}
