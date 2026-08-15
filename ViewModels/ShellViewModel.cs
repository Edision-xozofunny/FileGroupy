using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FileGroupy.ViewModels;

/// <summary>应用壳层视图模型，集中管理左侧导航和页面间数据同步</summary>
public partial class ShellViewModel : ObservableObject
{
    /// <summary>概览与分类页面的视图模型</summary>
    public DashboardViewModel Dashboard { get; }
    /// <summary>树形文件浏览页面的视图模型</summary>
    public FileExplorerViewModel Explorer { get; }

    /// <summary>由工具生成的当前活动页面视图模型公开绑定属性</summary>
    [ObservableProperty] private ObservableObject _currentViewModel;

    /// <summary>订阅概览页事件，并建立扫描结果和分类导航到文件浏览页的桥接</summary>
    /// <param name="dashboard">通过依赖注入提供的概览页视图模型</param>
    /// <param name="explorer">通过依赖注入提供的文件浏览页视图模型</param>
        /// <summary>切换到概览与分类页面</summary>
        /// <summary>切换到全部文件浏览页面</summary>
    public ShellViewModel(DashboardViewModel dashboard, FileExplorerViewModel explorer)
    {
        Dashboard = dashboard;
        Explorer = explorer;
        _currentViewModel = Dashboard;
        Dashboard.ScanCompleted += (_, result) => Explorer.Load(result);
        Dashboard.ScanCancelled += (_, _) => Explorer.Clear();
        Explorer.FilesChanged += (_, result) => Dashboard.ApplyExplorerSnapshot(result);
        Dashboard.CategoryRequested += (_, category) =>
        {
            Explorer.ShowCategory(category);
            CurrentViewModel = Explorer;
        };
    }

    [RelayCommand]
    private void ShowDashboard() => CurrentViewModel = Dashboard;

    [RelayCommand]
    private void ShowExplorer()
    {
        Explorer.ShowAll();
        CurrentViewModel = Explorer;
    }
}