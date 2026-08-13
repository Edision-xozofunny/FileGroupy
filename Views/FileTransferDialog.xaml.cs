using System.Windows;
using FileGroupy.ViewModels;

namespace FileGroupy.Views;

/// <summary>收集复制或移动参数并显示进度的模态对话框</summary>
public partial class FileTransferDialog : Window
{
    /// <summary>初始化对话框并绑定传输任务的状态</summary>
    /// <param name="viewModel">本次批量传输专属的视图模型</param>
    public FileTransferDialog(FileTransferDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}