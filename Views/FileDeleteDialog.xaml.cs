using System.Windows;
using FileGroupy.ViewModels;

namespace FileGroupy.Views;

/// <summary>批量删除对话框，承载删除参数确认与执行进度展示</summary>
public partial class FileDeleteDialog : Window
{
    /// <summary>初始化删除对话框并绑定视图模型</summary>
    /// <param name="viewModel">删除任务视图模型</param>
    public FileDeleteDialog(FileDeleteDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
