using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LogLens.Core;

/// <summary>
/// ObservableCollection that can append or replace many items without firing one
/// notification per item. Small appends stay incremental so the ListBox keeps its
/// scroll position and selection; large ones fall back to a single Reset.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Above this many items in one shot, a Reset is cheaper than N Adds.</summary>
    public const int ResetThreshold = 256;

    private bool _suppress;

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppress) base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppress) base.OnPropertyChanged(e);
    }

    public void AddRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0) return;

        if (items.Count < ResetThreshold)
        {
            foreach (var i in items) Add(i);
            return;
        }

        _suppress = true;
        try
        {
            foreach (var i in items) Items.Add(i);
        }
        finally { _suppress = false; }

        RaiseReset();
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        _suppress = true;
        try
        {
            Items.Clear();
            foreach (var i in items) Items.Add(i);
        }
        finally { _suppress = false; }

        RaiseReset();
    }

    /// <summary>Drop the first n items (ring-buffer trim) with a single notification.</summary>
    public void TrimStart(int n)
    {
        if (n <= 0) return;
        if (n >= Count) { ReplaceAll(Array.Empty<T>()); return; }

        _suppress = true;
        try
        {
            for (int i = 0; i < n; i++) Items.RemoveAt(0);
        }
        finally { _suppress = false; }

        RaiseReset();
    }

    private void RaiseReset()
    {
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        base.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        base.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
