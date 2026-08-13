using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FileGroupy.ViewModels;

namespace FileGroupy.Views;

/// <summary>文件浏览页面的 WPF 视图，处理表格拖拽框选等纯视觉交互</summary>
public partial class FileExplorerView : System.Windows.Controls.UserControl
{
    /// <summary>开始拖拽框选时相对于表格的鼠标坐标；为空表示未框选</summary>
    private System.Windows.Point? _selectionOrigin;
    /// <summary>当前右键菜单关联的文件行，供菜单点击后稳定传递命令参数。</summary>
    private ExplorerRow? _contextMenuRow;

    /// <summary>初始化文件浏览页面及其 XAML 组件</summary>
    public FileExplorerView() => InitializeComponent();

    /// <summary>复选框绑定改变后，通知视图模型重新计算选择汇总</summary>
    /// <param name="sender">触发事件的复选框</param>
    /// <param name="e">WPF 路由事件参数</param>
        /// <summary>记录鼠标按下位置，作为后续框选矩形的起点</summary>
        /// <param name="sender">文件表格控件</param>
        /// <param name="e">鼠标按下事件参数</param>
        /// <summary>鼠标按住移动时绘制选择矩形，并选择与矩形相交的文件行</summary>
        /// <param name="sender">文件表格控件</param>
        /// <param name="e">鼠标移动事件参数</param>
        // 轻微抖动视为普通单击，避免显示无意义的选择框
        // 仅对已生成可视容器的行做几何命中判断，兼容表格虚拟化
        /// <summary>结束框选并隐藏临时选择矩形</summary>
        /// <param name="sender">文件表格控件</param>
        /// <param name="e">鼠标释放事件参数</param>
    private void FileCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is FileExplorerViewModel viewModel && sender is System.Windows.Controls.CheckBox { DataContext: ExplorerRow row, IsChecked: bool selected })
        {
            viewModel.SetRowSelection(row, selected);
        }
    }

    /// <summary>处理表头全选框的勾选变化并同步全部扫描文件</summary>
    /// <param name="sender">触发事件的表头复选框</param>
    /// <param name="e">WPF 路由事件参数</param>
    private void SelectAllCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is FileExplorerViewModel viewModel && sender is System.Windows.Controls.CheckBox { IsChecked: bool selected })
        {
            viewModel.SetAllSelection(selected);
        }
    }

    private void FilesGrid_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(e.OriginalSource as DependencyObject) is not null ||
            FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is null)
        {
            _selectionOrigin = null;
            return;
        }

        _selectionOrigin = e.GetPosition(FilesGrid);
    }

    private void FilesGrid_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_selectionOrigin is not { } origin || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(FilesGrid);
        if (Math.Abs(current.X - origin.X) < 8 && Math.Abs(current.Y - origin.Y) < 8)
        {
            return;
        }

        var rectangle = new Rect(origin, current);
        SelectionBox.Visibility = Visibility.Visible;
        SelectionBox.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        SelectionBox.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        SelectionBox.Margin = new Thickness(rectangle.Left, rectangle.Top, 0, 0);
        SelectionBox.Width = rectangle.Width;
        SelectionBox.Height = rectangle.Height;

        var selectedRows = FilesGrid.Items.OfType<ExplorerRow>().Where(row =>
        {
            var container = FilesGrid.ItemContainerGenerator.ContainerFromItem(row) as DataGridRow;
            if (container is null)
            {
                return false;
            }

            var rowBounds = container.TransformToAncestor(FilesGrid).TransformBounds(new Rect(new System.Windows.Point(), container.RenderSize));
            return rectangle.IntersectsWith(rowBounds);
        });

        if (DataContext is FileExplorerViewModel viewModel)
        {
            viewModel.SelectRows(selectedRows, true);
        }
    }

    private void FilesGrid_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _selectionOrigin = null;
        SelectionBox.Visibility = Visibility.Collapsed;
    }

    private void SearchButton_OnClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>右键文件行时仅显示针对单个文件的 Shell 打开命令</summary>
    private void FilesGrid_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as ExplorerRow;
        if (row?.File is not null && DataContext is FileExplorerViewModel viewModel)
        {
            _contextMenuRow = row;
            FilesGrid.SelectedItem = row;
            OpenMenuItem.IsEnabled = viewModel.OpenFileCommand.CanExecute(row);
            OpenWithMenuItem.IsEnabled = viewModel.OpenWithFileCommand.CanExecute(row);
            OpenLocationMenuItem.IsEnabled = viewModel.OpenFileLocationCommand.CanExecute(row);
            OpenMenuItem.Visibility = Visibility.Visible;
            OpenWithMenuItem.Visibility = Visibility.Visible;
            OpenLocationMenuItem.Visibility = Visibility.Visible;
            OpenMenuSeparator.Visibility = Visibility.Visible;
            return;
        }

        _contextMenuRow = null;
        OpenMenuItem.Visibility = Visibility.Collapsed;
        OpenWithMenuItem.Visibility = Visibility.Collapsed;
        OpenLocationMenuItem.Visibility = Visibility.Collapsed;
        OpenMenuSeparator.Visibility = Visibility.Collapsed;
    }

    private async void OpenMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is FileExplorerViewModel viewModel && _contextMenuRow is not null)
        {
            await viewModel.OpenFileCommand.ExecuteAsync(_contextMenuRow);
        }
    }

    private async void OpenWithMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is FileExplorerViewModel viewModel && _contextMenuRow is not null)
        {
            await viewModel.OpenWithFileCommand.ExecuteAsync(_contextMenuRow);
        }
    }

    private async void OpenLocationMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is FileExplorerViewModel viewModel && _contextMenuRow is not null)
        {
            await viewModel.OpenFileLocationCommand.ExecuteAsync(_contextMenuRow);
        }
    }

    /// <summary>向上查找指定类型的可视树父元素</summary>
    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
            {
                return match;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    /// <summary>双击实际文件行时按 Windows 默认关联打开，未关联时交给系统“打开方式”</summary>
    private async void FilesGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FileExplorerViewModel viewModel && FilesGrid.SelectedItem is ExplorerRow { File: not null } row)
        {
            await viewModel.OpenFileCommand.ExecuteAsync(row);
        }
    }
}