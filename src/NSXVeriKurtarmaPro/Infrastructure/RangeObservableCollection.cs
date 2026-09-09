using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace NSXVeriKurtarmaPro.Infrastructure;

public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replace a result snapshot without exposing an empty collection to WPF.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        // Materialize before mutation, including when callers pass this collection itself.
        T[] snapshot = items.ToArray();
        CheckReentrancy();
        Items.Clear();
        foreach (T item in snapshot)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        int added = 0;
        foreach (T item in items)
        {
            Items.Add(item);
            added++;
        }

        if (added == 0)
            return;

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
