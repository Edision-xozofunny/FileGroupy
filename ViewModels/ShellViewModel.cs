using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FileGroupy.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    /// <summary>概览与分类页面的视图模型</summary>
    public DashboardViewModel Dashboard { get; }
    /// <summary>树形文件浏览页面的视图模型</summary>
    public FileExplorerViewModel Explorer { get; }

    [ObservableProperty] private ObservableObject _currentViewModel;

    /// <summary>概览导航项是否显示活动指示条</summary>
    [ObservableProperty] private bool _isDashboardActive;
    /// <summary>文件浏览导航项是否显示活动指示条</summary>
    [ObservableProperty] private bool _isExplorerActive;

    public ShellViewModel(DashboardViewModel dashboard, FileExplorerViewModel explorer)
    {
        Dashboard = dashboard;
        Explorer = explorer;
        _currentViewModel = Dashboard;
        Dashboard.ScanCompleted += (_, result) => Explorer.Load(result);
        Dashboard.ScanCancelled += (_, _) => Explorer.Clear();
        Explorer.FilesChanged += (_, result) => Dashboard.ApplyExplorerSnapshot(result);
        Explorer.RefreshRequested += async (_, _) => await Dashboard.RefreshCurrentCommand.ExecuteAsync(null);
        Dashboard.CategoryRequested += (_, category) =>
        {
            Explorer.ShowCategory(category);
            CurrentViewModel = Explorer;
            SelectExplorerNavigation();
        };
    }

    [RelayCommand]
    private void ShowDashboard()
    {
        CurrentViewModel = Dashboard;
        IsDashboardActive = !IsDashboardActive;
        IsExplorerActive = false;
    }

    [RelayCommand]
    private void ShowExplorer()
    {
        Explorer.ShowAll();
        CurrentViewModel = Explorer;
        SelectExplorerNavigation();
    }

    /// <summary>选择文件浏览导航项, 再次点击同一项时隐藏活动指示条</summary>
    private void SelectExplorerNavigation()
    {
        IsExplorerActive = !IsExplorerActive;
        IsDashboardActive = false;
    }
}
