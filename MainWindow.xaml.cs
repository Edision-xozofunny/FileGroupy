using System.Windows;
using System.Windows.Media.Animation;
using FileGroupy.ViewModels;

namespace FileGroupy;

public partial class MainWindow : Window
{
    /// <summary>当前悬停的导航按钮, 供容器重新布局后重新对齐共享底纹</summary>
    private System.Windows.Controls.Button? _hoveredNavigationButton;
    private bool _isClosing;
    private readonly DashboardViewModel _dashboard;

    /// <summary>初始化主窗口并设置通过依赖注入创建的壳层视图模型</summary>
    /// <param name="viewModel">负责导航和页面状态的壳层视图模型</param>
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _dashboard = viewModel.Dashboard;
        viewModel.Explorer.ExternalChangeDetected += Explorer_OnExternalChangeDetected;
        Closing += MainWindow_OnClosing;
    }

    /// <summary>在主窗口内容区右上角提示当前目录存在外部变化</summary>
    private static void Explorer_OnExternalChangeDetected(object? sender, string message) =>
        HandyControl.Controls.Growl.Warning(message, "MainWindowGrowl");

    /// <summary>扫描进行中时先取消并等待数据库事务结束, 再允许窗口退出</summary>
    private async void MainWindow_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosing || !_dashboard.IsScanning)
        {
            return;
        }

        e.Cancel = true;
        _isClosing = true;
        await _dashboard.StopAsync();
        Close();
    }

    /// <summary>将共享悬停底纹平滑移动到当前鼠标所在的导航项</summary>
    private void NavigationButton_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
        {
            return;
        }

        _hoveredNavigationButton = button;
        PositionNavigationHoverSurface(button, animate: true);
        NavigationHoverSurface.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(100)));
    }

    /// <summary>容器尺寸变化后按当前按钮的实际边界重新定位底纹</summary>
    private void NavigationMenuPanel_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_hoveredNavigationButton is not null)
        {
            PositionNavigationHoverSurface(_hoveredNavigationButton, animate: false);
        }
    }

    /// <summary>使共享底纹精确覆盖导航按钮的布局区域, 包含 Margin 带来的坐标偏移</summary>
    private void PositionNavigationHoverSurface(System.Windows.Controls.Button button, bool animate)
    {
        if (button.ActualWidth == 0 || button.ActualHeight == 0)
        {
            return;
        }

        var targetPoint = button.TranslatePoint(new System.Windows.Point(), NavigationMenuPanel);
        NavigationHoverSurface.Width = button.ActualWidth;
        NavigationHoverSurface.Height = button.ActualHeight;

        if (!animate)
        {
            NavigationHoverTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            NavigationHoverTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            NavigationHoverTransform.X = targetPoint.X;
            NavigationHoverTransform.Y = targetPoint.Y;
            return;
        }

        NavigationHoverTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(targetPoint.X, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        NavigationHoverTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(targetPoint.Y, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>鼠标离开导航区域后淡出共享悬停底纹</summary>
    private void NavigationMenuPanel_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hoveredNavigationButton = null;
        NavigationHoverSurface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
    }
}
