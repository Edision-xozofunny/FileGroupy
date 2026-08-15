using System.Windows;
using FileGroupy.ViewModels;

namespace FileGroupy.Views;

/// <summary>承载文本或图片内嵌预览的模态窗口</summary>
public partial class FilePreviewDialog : Window
{
    /// <summary>初始化预览窗口并设置指定文件的预览状态</summary>
    /// <param name="viewModel">预览窗口视图模型</param>
    public FilePreviewDialog(FilePreviewViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
