using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FileGroupy.ViewModels;

namespace FileGroupy.Views;

/// <summary>展示删除快照并提供恢复与永久清除操作的窗口</summary>
public partial class DeletedFileRecoveryDialog : Window
{
    private readonly DeletedFileRecoveryViewModel _viewModel;
    private System.Windows.Controls.CheckBox? _selectAllFilesCheckBox;

    public DeletedFileRecoveryDialog(DeletedFileRecoveryViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.AlertRequested += ViewModel_OnAlertRequested;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
        PreviewMouseDown += RecoveryDialog_OnPreviewMouseDown;
    }

    /// <summary>将 DataGrid 多选项同步到视图模型, 供恢复选中命令使用</summary>
    private void RecoveryFilesGrid_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
    }

    private void SelectAllFilesCheckBox_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        _selectAllFilesCheckBox = sender as System.Windows.Controls.CheckBox;
        if (_selectAllFilesCheckBox?.IsChecked == true)
        {
            _viewModel.SetAllFileSelection(true);
        }
        else
        {
            _viewModel.SetAllFileSelection(false);
        }
        e.Handled = true;
    }

    private void FileSelectionCheckBox_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox checkBox)
        {
            return;
        }

        if (checkBox.DataContext is RecoveryFileRow file)
        {
            _viewModel.SetFileSelection(file, checkBox.IsChecked == true);
            if (_selectAllFilesCheckBox is not null)
            {
                _selectAllFilesCheckBox.IsChecked = _viewModel.Files.Count > 0
                    && _viewModel.Files.All(item => item.IsSelected);
            }
        }

        e.Handled = true;
    }

    private static T? FindAncestor<T>(System.Windows.DependencyObject? source) where T : System.Windows.DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void RecoveryFilesGrid_OnMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RecoveryFilesGrid.SelectedItem is RecoveryFileRow row)
        {
            _viewModel.OpenFileDetails(row.Item);
        }
    }

    private void RecoveryDialog_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.IsFileDetailsOpen && !IsInsideDetailsDrawer(e.OriginalSource as DependencyObject))
        {
            _viewModel.CloseFileDetailsCommand.Execute(null);
        }
    }

    private bool IsInsideDetailsDrawer(DependencyObject? source)
    {
        if (source is null || RecoveryDetailsDrawer.Content is not DependencyObject drawerContent)
        {
            return false;
        }

        var current = source;
        var visited = new HashSet<DependencyObject>();
        while (current is not null && visited.Add(current))
        {
            if (ReferenceEquals(current, drawerContent) || ReferenceEquals(current, RecoveryDetailsDrawer))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current)
                ?? (current as FrameworkContentElement)?.Parent
                ?? LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    /// <summary>永久清除前要求用户确认不可逆后果, 在按钮命令执行前中断取消操作</summary>
    private void PermanentlyDeleteButton_OnPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (HandyControl.Controls.MessageBox.Ask("永久清除会删除恢复库中的文件，之后无法找回。是否继续？", "确认永久清除") != MessageBoxResult.Yes)
        {
            e.Handled = true;
        }
    }

    private static void ViewModel_OnAlertRequested(object? sender, string message) =>
        HandyControl.Controls.MessageBox.Warning(message, "删除找回");

    private void RecoveryHistoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new RecoveryHistoryDialog(_viewModel)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }
}