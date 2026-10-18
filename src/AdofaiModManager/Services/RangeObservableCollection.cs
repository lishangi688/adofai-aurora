using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AdofaiModManager.Services;

/// <summary>
/// 支持批量增删的 ObservableCollection。
///
/// 在线 Mod 列表一次会有几十到几百条，逐条 Add 会让 WPF 反复生成容器，
/// 切换来源时出现明显停顿。这里用一次 CollectionChanged 通知完成整批更新。
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> items)
    {
        var list = items as IList<T> ?? items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        // 追加时逐项 Add：保留现有滚动位置和选中项。
        // WPF 的 ListCollectionView 不支持带 StartIndex 的多项 Add 通知，
        // 而一次 Reset 又会让列表滚回顶部，所以这里保持逐项通知。
        foreach (var item in list)
        {
            Add(item);
        }
    }

    public void ReplaceRange(IEnumerable<T> items)
    {
        var list = items as IList<T> ?? items.ToList();

        CheckReentrancy();
        Items.Clear();
        foreach (var item in list)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Reset));
    }
}
