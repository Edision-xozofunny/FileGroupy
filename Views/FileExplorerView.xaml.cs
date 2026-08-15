using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HandyControl.Controls;
using FileGroupy.Models;
using FileGroupy.ViewModels;

namespace FileGroupy.Views;

/// <summary>文件浏览页面的 WPF 视图,处理表格拖拽框选等纯视觉交互</summary>
public partial class FileExplorerView : System.Windows.Controls.UserControl
{
    /// <summary>开始拖拽框选时相对于表格的鼠标坐标;为空表示未框选</summary>
    private System.Windows.Point? _selectionOrigin;
    /// <summary>当前右键命中的文件行, 供上下文菜单命令使用</summary>
    private ExplorerRow? _contextMenuRow;
    private ExplorerRow? _hoverPreviewRow;
    /// <summary>图片悬停预览的取消源,移动鼠标或离开表格时终止后台加载</summary>
    private CancellationTokenSource? _hoverPreviewCancellationTokenSource;
    /// <summary>当前绑定的文件浏览视图模型</summary>
    private FileExplorerViewModel? _viewModel;
    /// <summary>承载当前页面的主窗口, 用于监听抽屉外部点击</summary>
    private System.Windows.Window? _ownerWindow;

    /// <summary>初始化文件浏览页面及其 XAML 组件</summary>
    public FileExplorerView()
    {
        InitializeComponent();
        DataContextChanged += FileExplorerView_OnDataContextChanged;
    }

    /// <summary>订阅当前视图模型, 将文件操作结果显示为全局轻提示</summary>
    private void FileExplorerView_OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        }

        _viewModel = e.NewValue as FileExplorerViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        }
    }

    /// <summary>文件操作状态更新后使用 HandyControl Growl 展示结果</summary>
    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileExplorerViewModel.OperationStatus) && !string.IsNullOrWhiteSpace(_viewModel?.OperationStatus))
        {
            Growl.SuccessGlobal(_viewModel.OperationStatus);
        }
    }

    /// <summary>页面加载后订阅主窗口鼠标预览事件</summary>
    private void ExplorerRoot_OnLoaded(object sender, RoutedEventArgs e)
    {
        _ownerWindow = System.Windows.Window.GetWindow(this);
        if (_ownerWindow is not null)
        {
            _ownerWindow.PreviewMouseDown += OwnerWindow_OnPreviewMouseDown;
        }
    }

    /// <summary>页面卸载时解除主窗口事件订阅, 避免旧页面继续持有引用</summary>
    private void ExplorerRoot_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_ownerWindow is not null)
        {
            _ownerWindow.PreviewMouseDown -= OwnerWindow_OnPreviewMouseDown;
            _ownerWindow = null;
        }
    }

    /// <summary>点击详情抽屉外的任意位置时关闭抽屉</summary>
    private void OwnerWindow_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FileExplorerViewModel viewModel
            && viewModel.IsDetailsDrawerOpen
            && !IsInsideDetailsDrawer(e.OriginalSource as DependencyObject))
        {
            viewModel.CloseDetailsCommand.Execute(null);
        }
    }

    /// <summary>判断鼠标事件源是否位于 Drawer 的实际内容中</summary>
    private bool IsInsideDetailsDrawer(DependencyObject? source)
    {
        if (source is null || DetailsDrawer.Content is not DependencyObject drawerContent)
        {
            return false;
        }

        var current = source;
        var visited = new HashSet<DependencyObject>();
        while (current is not null && visited.Add(current))
        {
            if (ReferenceEquals(current, drawerContent) || ReferenceEquals(current, DetailsDrawer))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current)
                ?? (current as FrameworkContentElement)?.Parent
                ?? LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

        // 轻微抖动视为普通单击,避免显示无意义的选择框
    private void FileCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is FileExplorerViewModel viewModel && sender is System.Windows.Controls.CheckBox { DataContext: ExplorerRow row, IsChecked: bool selected })
        {
            viewModel.SetRowSelection(row, selected);
        }
    }

    private void SelectAllCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is FileExplorerViewModel viewModel && sender is System.Windows.Controls.CheckBox { IsChecked: bool selected })
        {
            viewModel.SetAllSelection(selected);
        }
    }

    private void FilesGrid_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FileExplorerViewModel viewModel)
        {
            viewModel.CloseDetailsCommand.Execute(null);
        }

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

    private async void FilesGrid_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            return;
        }

        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as ExplorerRow;
        if (row?.File is null || row.File.Category != FileCategory.Images)
        {
            HideImageHoverPopup();
            return;
        }

        if (_hoverPreviewRow?.File?.FullPath == row.File.FullPath)
        {
            ImageHoverPopup.HorizontalOffset = e.GetPosition(FilesGrid).X + 24;
            ImageHoverPopup.VerticalOffset = e.GetPosition(FilesGrid).Y + 18;
            return;
        }

        _hoverPreviewCancellationTokenSource?.Cancel();
        _hoverPreviewCancellationTokenSource?.Dispose();
        _hoverPreviewCancellationTokenSource = new CancellationTokenSource();
        var token = _hoverPreviewCancellationTokenSource.Token;
        _hoverPreviewRow = row;

        try
        {
            if (DataContext is not FileExplorerViewModel viewModel)
            {
                return;
            }

            var preview = await viewModel.CreateImageHoverPreviewAsync(row, token);
            if (preview is null || token.IsCancellationRequested)
            {
                return;
            }

            ImageHoverPreviewImage.Source = preview.ImageSource;
            ImageHoverCorruptedText.Visibility = preview.IsCorrupted ? Visibility.Visible : Visibility.Collapsed;
            ImageHoverResolutionText.Text = $"分辨率: {preview.Resolution}";
            ImageHoverSizeText.Text = $"大小: {preview.SizeText}";
            ImageHoverTypeText.Text = $"类型: {preview.TypeText}";
            ImageHoverPopup.HorizontalOffset = e.GetPosition(FilesGrid).X + 24;
            ImageHoverPopup.VerticalOffset = e.GetPosition(FilesGrid).Y + 18;
            ImageHoverPopup.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
            // 鼠标移动频繁会取消旧请求,忽略即可
        }
    }

    private void FilesGrid_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => HideImageHoverPopup();

    private void HideImageHoverPopup()
    {
        _hoverPreviewCancellationTokenSource?.Cancel();
        _hoverPreviewCancellationTokenSource?.Dispose();
        _hoverPreviewCancellationTokenSource = null;
        _hoverPreviewRow = null;
        ImageHoverPopup.IsOpen = false;
        ImageHoverPreviewImage.Source = null;
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

    private void FilesGrid_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as ExplorerRow;
        if (row?.File is not null && DataContext is FileExplorerViewModel viewModel)
        {
            _contextMenuRow = row;
            FilesGrid.SelectedItem = row;
            viewModel.EnsureContextRowSelected(row);
            DetailsMenuItem.Visibility = Visibility.Visible;
            DetailsMenuSeparator.Visibility = Visibility.Visible;
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
        DetailsMenuItem.Visibility = Visibility.Collapsed;
        DetailsMenuSeparator.Visibility = Visibility.Collapsed;
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

    /// <summary>从右键菜单打开当前文件的详情侧栏</summary>
    private void DetailsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is FileExplorerViewModel viewModel && _contextMenuRow is not null)
        {
            viewModel.ShowDetailsCommand.Execute(_contextMenuRow);
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

    private async void FilesGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FileExplorerViewModel viewModel && FilesGrid.SelectedItem is ExplorerRow { File: not null } row)
        {
            await viewModel.OpenFileCommand.ExecuteAsync(row);
        }
    }
}
