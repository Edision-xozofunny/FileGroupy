using System.Windows;
using FileGroupy.ViewModels;

namespace FileGroupy;

/// <summary>应用主窗口，承载侧边导航和当前页面内容</summary>
public partial class MainWindow : Window
{
    /// <summary>初始化主窗口并设置通过依赖注入创建的壳层视图模型</summary>
    /// <param name="viewModel">负责导航和页面状态的壳层视图模型</param>
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}