using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FileGroupy.Controls;

/// <summary>标识可在表格滚动时固定显示的分组行。</summary>
public interface IStickyDataGridRow
{
    /// <summary>当前行是否为应固定的分组标题。</summary>
    bool IsStickyRow { get; }
}

/// <summary>在标准 DataGrid 顶部叠加当前分组行，实现资源管理器式的滚动分组标题。</summary>
public sealed class StickyDataGrid : DataGrid
{
    /// <summary>当前滚动位置所属的最近分组行。</summary>
    public object? StickyItem
    {
        get => GetValue(StickyItemProperty);
        private set => SetValue(StickyItemPropertyKey, value);
    }

    private static readonly DependencyPropertyKey StickyItemPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(StickyItem), typeof(object), typeof(StickyDataGrid), new PropertyMetadata(null));

    /// <summary>当前滚动位置所属的最近分组行。</summary>
    public static readonly DependencyProperty StickyItemProperty = StickyItemPropertyKey.DependencyProperty;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ScrollViewer_OnScrollChanged), true);
        LoadingRow -= StickyDataGrid_OnLoadingRow;
        LoadingRow += StickyDataGrid_OnLoadingRow;
        Dispatcher.BeginInvoke(UpdateStickyItem, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>数据或布局变化后更新固定分组行。</summary>
    protected override void OnItemsChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        Dispatcher.BeginInvoke(UpdateStickyItem, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void StickyDataGrid_OnLoadingRow(object? sender, DataGridRowEventArgs e) =>
        Dispatcher.BeginInvoke(UpdateStickyItem, System.Windows.Threading.DispatcherPriority.Background);

    private void ScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0)
        {
            Dispatcher.BeginInvoke(UpdateStickyItem, System.Windows.Threading.DispatcherPriority.Render);
        }
    }

    private void UpdateStickyItem()
    {
        if (Items.Count == 0)
        {
            StickyItem = null;
            return;
        }

        var firstVisibleIndex = -1;
        for (var index = 0; index < Items.Count; index++)
        {
            if (ItemContainerGenerator.ContainerFromIndex(index) is not DataGridRow row)
            {
                continue;
            }

            var bounds = row.TransformToAncestor(this).TransformBounds(new Rect(new System.Windows.Point(), row.RenderSize));
            if (bounds.Bottom > ColumnHeaderHeight)
            {
                firstVisibleIndex = index;
                break;
            }
        }

        if (firstVisibleIndex < 0)
        {
            return;
        }

        for (var index = firstVisibleIndex; index >= 0; index--)
        {
            if (Items[index] is IStickyDataGridRow { IsStickyRow: true })
            {
                StickyItem = Items[index];
                return;
            }
        }

        StickyItem = null;
    }

}
