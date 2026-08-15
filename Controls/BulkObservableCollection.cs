using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace FileGroupy.Controls;

/// <summary>支持使用单次 Reset 通知批量替换或插入数据的集合</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>替换全部项并仅发送一次集合重置通知</summary>
    public void ReplaceWith(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        NotifyReset();
    }

    /// <summary>在指定位置插入多个项并仅发送一次集合重置通知</summary>
    public void InsertRange(int index, IEnumerable<T> items)
    {
        foreach (var item in items.Reverse())
        {
            Items.Insert(index, item);
        }

        NotifyReset();
    }

    /// <summary>移除指定范围内的项并仅发送一次集合重置通知</summary>
    public void RemoveRange(int index, int count)
    {
        if (count == 0)
        {
            return;
        }

        for (var itemIndex = index + count - 1; itemIndex >= index; itemIndex--)
        {
            Items.RemoveAt(itemIndex);
        }
        NotifyReset();
    }

    /// <summary>通知绑定控件重新读取集合内容</summary>
    private void NotifyReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
