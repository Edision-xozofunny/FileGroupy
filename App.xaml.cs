using System.Windows;
using FileGroupy.Cache;
using FileGroupy.Configuration;
using FileGroupy.Services;
using FileGroupy.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileGroupy;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;

    /// <summary>创建服务容器并显示主窗口</summary>
    /// <param name="e">WPF 启动事件参数</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Microsoft.Data.Sqlite.Core 需要由宿主显式初始化 SQLitePCL 原生实现.
        SQLitePCL.Batteries_V2.Init();
        var applicationOptions = ApplicationOptionsLoader.Load();
        CacheDatabaseMigration.MigrateIfPathChanged(applicationOptions);
        // 恢复服务会在启动时查询 state/is_moved, 因此必须先完成独立 schema 升级.
        RecoverySchemaInitializer.EnsureCreated(applicationOptions);

        var services = new ServiceCollection();
        services.AddSingleton(applicationOptions);
        services.AddDbContextFactory<ScanCacheDbContext>(dbContextOptions =>
            dbContextOptions.UseSqlite(CacheDatabaseOptions.GetConnectionString(applicationOptions)));
        services.AddSingleton<IScanCacheStore, SqliteScanCacheStore>();
        services.AddSingleton<RecoveryObjectStore>();
        services.AddSingleton<IDeletedFileRecoveryService, DeletedFileRecoveryService>();
        services.AddSingleton<IPathHistoryStore, JsonPathHistoryStore>();
        services.AddSingleton<IFileScannerService, FileScannerService>();
        services.AddSingleton<IMtpDeviceService, MtpDeviceService>();
        services.AddSingleton<IFileTransferService, FileTransferService>();
        services.AddSingleton<IFilePreviewService, FilePreviewService>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<FileExplorerViewModel>();
        services.AddTransient<DeletedFileRecoveryViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();
        _serviceProvider = services.BuildServiceProvider();

        // 配置允许禁用启动修复, 便于排障或外部恢复工具访问恢复库.
        if (applicationOptions.Startup.RecoverPendingDeletionSnapshots)
        {
            _serviceProvider.GetRequiredService<IDeletedFileRecoveryService>().RecoverInterruptedOperationsAsync().GetAwaiter().GetResult();
        }
        _serviceProvider.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
