using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace VelaShell.ViewModels;

/// <summary>用一次 Reset 通知批量替换内容,避免逐项 Clear/Add 触发多轮布局。</summary>
internal sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        List<T> materialized = [.. items];
        Items.Clear();
        foreach (T item in materialized)
        {
            Items.Add(item);
        }
        OnPropertyChanged(new(nameof(Count)));
        OnPropertyChanged(new("Item[]"));
        OnCollectionChanged(new(NotifyCollectionChangedAction.Reset));
    }
}
