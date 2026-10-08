using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace OmpGui.App.ViewModels;

/// <summary>
/// The conversation's rows. Showing another chat puts that chat's rows in at once, with one reset: added one by one, a
/// long conversation raised an event per row, and the list laid itself out again for each.
/// </summary>
public sealed class RowList : ObservableCollection<RowViewModel>
{
    /// <summary>Replaces every row with <paramref name="rows"/> (the same instances, nothing disposed).</summary>
    public void ResetTo(IEnumerable<RowViewModel> rows)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
