using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FileGroupy.Controls;

/// <summary>标识可在表格滚动时固定显示的分组行。</summary>
public interface IStickyDataGridRow
{
    /// <summary>当前行是否为应固定的分组标题。</summary>
    bool IsStickyRow { get; }

    /// <summary>固定分组层级，0 表示根分组，数值越大层级越深。</summary>
    int StickyLevel { get; }
}

/// <summary>在标准 DataGrid 顶部叠加当前分组行，实现资源管理器式的滚动分组标题。</summary>
public sealed class StickyDataGrid : DataGrid
{
    /// <summary>固定分组行刷新是否已排队，避免大批量行变更时重复调度。</summary>
    private bool _isStickyUpdateScheduled;

    /// <summary>当前滚动位置所属的最近分组行。</summary>
    public object? StickyItem
    {
        get => GetValue(StickyItemProperty);
        private set => SetValue(StickyItemPropertyKey, value);
    }

    /// <summary>当前滚动位置需要固定显示的分组行列表。</summary>
    public IReadOnlyList<object> StickyItems
    {
        get => (IReadOnlyList<object>)GetValue(StickyItemsProperty);
        private set => SetValue(StickyItemsPropertyKey, value);
    }

    private static readonly DependencyPropertyKey StickyItemPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(StickyItem), typeof(object), typeof(StickyDataGrid), new PropertyMetadata(null));

    /// <summary>当前滚动位置所属的最近分组行。</summary>
    public static readonly DependencyProperty StickyItemProperty = StickyItemPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey StickyItemsPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(StickyItems), typeof(IReadOnlyList<object>), typeof(StickyDataGrid), new PropertyMetadata(Array.Empty<object>()));

    /// <summary>当前滚动位置需要固定显示的分组行列表。</summary>
    public static readonly DependencyProperty StickyItemsProperty = StickyItemsPropertyKey.DependencyProperty;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ScrollViewer_OnScrollChanged), true);
        ScheduleStickyUpdate(System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>数据或布局变化后更新固定分组行。</summary>
    protected override void OnItemsChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        ScheduleStickyUpdate(System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>滚动时按渲染优先级刷新固定标题。</summary>
    /// <param name="sender">触发滚动事件的对象。</param>
    /// <param name="e">滚动变化参数。</param>
    private void ScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0)
        {
            ScheduleStickyUpdate(System.Windows.Threading.DispatcherPriority.Render);
        }
    }

    /// <summary>按首个可视行回溯最近分组节点，更新顶部固定标题。</summary>
    private void UpdateStickyItem()
    {
        if (Items.Count == 0)
        {
            StickyItem = null;
            StickyItems = Array.Empty<object>();
            return;
        }

        var firstVisibleIndex = GetFirstVisibleRowIndex();
        if (firstVisibleIndex < 0)
        {
            return;
        }

        var stickyItems = GetStickyItems(firstVisibleIndex);
        StickyItems = stickyItems;
        StickyItem = stickyItems.Count > 0 ? stickyItems[^1] : null;
    }

    /// <summary>从首个可视行向上回溯所有分组层级，构建固定分组列表。</summary>
    /// <param name="firstVisibleIndex">首个可视行索引。</param>
    /// <returns>按从根到子分组排序的固定列表。</returns>
    private IReadOnlyList<object> GetStickyItems(int firstVisibleIndex)
    {
        var stickyItems = new Stack<object>();
        var seenLevels = new HashSet<int>();
        for (var index = firstVisibleIndex; index >= 0; index--)
        {
            if (Items[index] is not IStickyDataGridRow { IsStickyRow: true } stickyRow)
            {
                continue;
            }

            if (!seenLevels.Add(stickyRow.StickyLevel))
            {
                continue;
            }

            stickyItems.Push(Items[index]);
            if (stickyRow.StickyLevel == 0)
            {
                break;
            }
        }

        return stickyItems.ToArray();
    }

    /// <summary>以当前已实现的可视行集合计算首个可视索引，避免遍历全部数据项。</summary>
    /// <returns>首个可视行索引；没有可视行时返回 -1。</returns>
    private int GetFirstVisibleRowIndex()
    {
        var firstVisibleIndex = -1;
        foreach (var row in EnumerateVisualRows(this))
        {
            var bounds = row.TransformToAncestor(this).TransformBounds(new Rect(new System.Windows.Point(), row.RenderSize));
            if (bounds.Bottom <= ColumnHeaderHeight)
            {
                continue;
            }

            if (firstVisibleIndex < 0 || row.GetIndex() < firstVisibleIndex)
            {
                firstVisibleIndex = row.GetIndex();
            }
        }

        return firstVisibleIndex;
    }

    /// <summary>调度一次粘性标题刷新，合并同一帧内的重复请求。</summary>
    /// <param name="priority">调度优先级。</param>
    private void ScheduleStickyUpdate(System.Windows.Threading.DispatcherPriority priority)
    {
        if (_isStickyUpdateScheduled)
        {
            return;
        }

        _isStickyUpdateScheduled = true;
        Dispatcher.BeginInvoke(() =>
        {
            _isStickyUpdateScheduled = false;
            UpdateStickyItem();
        }, priority);
    }

    /// <summary>枚举当前 DataGrid 已可视化的行容器。</summary>
    /// <param name="root">可视树根节点。</param>
    /// <returns>已生成的行容器序列。</returns>
    private static IEnumerable<DataGridRow> EnumerateVisualRows(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is DataGridRow row)
            {
                yield return row;
            }

            foreach (var nestedRow in EnumerateVisualRows(child))
            {
                yield return nestedRow;
            }
        }
    }

}
