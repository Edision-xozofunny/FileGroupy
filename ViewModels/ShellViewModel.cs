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

    /// <summary>概览导航项是否为当前页面</summary>
    public bool IsDashboardActive => CurrentViewModel == Dashboard;
    /// <summary>文件浏览导航项是否为当前页面</summary>
    public bool IsExplorerActive => CurrentViewModel == Explorer;

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

    partial void OnCurrentViewModelChanged(ObservableObject value)
    {
        OnPropertyChanged(nameof(IsDashboardActive));
        OnPropertyChanged(nameof(IsExplorerActive));
    }
}
