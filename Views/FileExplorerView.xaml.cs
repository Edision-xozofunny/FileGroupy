using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FileGroupy.Models;
using FileGroupy.ViewModels;

namespace FileGroupy.Views;

/// <summary>文件浏览页面的 WPF 视图,处理表格拖拽框选等纯视觉交互</summary>
public partial class FileExplorerView : System.Windows.Controls.UserControl
{
    /// <summary>开始拖拽框选时相对于表格的鼠标坐标;为空表示未框选</summary>
    private System.Windows.Point? _selectionOrigin;
    private ExplorerRow? _contextMenuRow;
    private ExplorerRow? _hoverPreviewRow;
    /// <summary>图片悬停预览的取消源,移动鼠标或离开表格时终止后台加载</summary>
    private CancellationTokenSource? _hoverPreviewCancellationTokenSource;

    /// <summary>初始化文件浏览页面及其 XAML 组件</summary>
    public FileExplorerView() => InitializeComponent();

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

    private async void FilesGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FileExplorerViewModel viewModel && FilesGrid.SelectedItem is ExplorerRow { File: not null } row)
        {
            await viewModel.OpenFileCommand.ExecuteAsync(row);
        }
    }
}
