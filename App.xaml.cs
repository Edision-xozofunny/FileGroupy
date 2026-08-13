using System.Windows;
using FileGroupy.Services;
using FileGroupy.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace FileGroupy;

/// <summary>应用程序入口，负责配置依赖注入容器并启动主窗口</summary>
public partial class App : System.Windows.Application
{
    /// <summary>应用生命周期内持有的服务容器，退出时统一释放</summary>
    private ServiceProvider? _serviceProvider;

    /// <summary>创建服务容器并显示主窗口</summary>
    /// <param name="e">WPF 启动事件参数</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        services.AddSingleton<IFileScannerService, FileScannerService>();
        services.AddSingleton<IMtpDeviceService, MtpDeviceService>();
        services.AddSingleton<IFileTransferService, FileTransferService>();
        services.AddSingleton<IFilePreviewService, FilePreviewService>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<FileExplorerViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();
        _serviceProvider = services.BuildServiceProvider();

        _serviceProvider.GetRequiredService<MainWindow>().Show();
    }

    /// <summary>释放由依赖注入容器创建的可释放资源</summary>
    /// <param name="e">WPF 退出事件参数</param>
    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}