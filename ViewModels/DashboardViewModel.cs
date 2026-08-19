using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileGroupy.Models;
using FileGroupy.Services;
using FileGroupy.Views;
using Microsoft.Extensions.DependencyInjection;

namespace FileGroupy.ViewModels;

/// <summary>概览页视图模型, 负责选择来源, 扫描文件并生成分类卡片</summary>
public partial class DashboardViewModel(
    IFileScannerService scanner,
    IMtpDeviceService mtpDeviceService,
    IPathHistoryStore pathHistoryStore,
    IServiceProvider serviceProvider) : ObservableObject
{
    /// <summary>当前扫描使用的取消源, 空值表示没有正在执行的扫描</summary>
    private CancellationTokenSource? _scanCancellationTokenSource;
    /// <summary>最近一次扫描委托, 用于刷新当前来源</summary>
    private Func<IProgress<FileScanProgress>, CancellationToken, Task<FolderScanResult>>? _lastScan;
    /// <summary>最近一次扫描的显示路径</summary>
    private string _lastScanPath = string.Empty;
    /// <summary>最近一次扫描是否来自本地文件系统</summary>
    private bool _lastScanWasLocal;
    private MtpDeviceInfo? _lastMtpDevice;
    private string _lastMtpRootPath = string.Empty;
    /// <summary>用于丢弃已取消扫描产生的旧进度</summary>
    private int _scanSessionId;
    /// <summary>当前扫描完成信号, 用于应用关闭时等待缓存事务收尾</summary>
    private TaskCompletionSource? _scanCompletionSource;

    /// <summary>扫描完成后显示在概览页的分类统计卡片集合</summary>
    public ObservableCollection<CategorySummary> Categories { get; } = [];

    /// <summary>当前扫描来源的显示路径</summary>
    [ObservableProperty] private string _selectedPath = "尚未选择文件夹";
    /// <summary>当前来源的基础统计信息</summary>
    [ObservableProperty] private string _folderInfo = "选择一个本地或可移动磁盘中的文件夹以开始分析";
    /// <summary>是否正在扫描</summary>
    [ObservableProperty] private bool _isScanning;
    /// <summary>取消后立即隐藏进度区域, 后台任务仍在快速收尾</summary>
    [ObservableProperty] private bool _isScanProgressVisible;
    /// <summary>实时扫描进度文本</summary>
    [ObservableProperty] private string _scanProgressText = string.Empty;
    /// <summary>当前缓存读取或源目录处理阶段标题</summary>
    [ObservableProperty] private string _scanStageTitle = "正在准备";
    /// <summary>扫描失败提示</summary>
    [ObservableProperty] private string? _errorMessage;

    /// <summary>目录扫描成功后触发,使外层导航可更新文件浏览页</summary>
    public event EventHandler<FolderScanResult>? ScanCompleted;
    /// <summary>用户取消扫描并清空界面状态后触发</summary>
    public event EventHandler? ScanCancelled;
    /// <summary>用户点击分类卡片时触发</summary>
    public event EventHandler<FileCategory>? CategoryRequested;

    [RelayCommand(CanExecute = nameof(CanChooseFolder))]
    private async Task ChooseFolderAsync()
    {
        await CancelActiveScanAsync();
        ClearScanState();
        ScanCancelled?.Invoke(this, EventArgs.Empty);
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要分析的文件夹",
            InitialDirectory = pathHistoryStore.GetLastPath(PathHistoryKind.Scan)
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        pathHistoryStore.SaveLastPath(PathHistoryKind.Scan, dialog.FolderName);
        _lastScanWasLocal = true;
        await StartScanAsync(dialog.FolderName, (progress, cancellationToken) => scanner.RefreshAsync(dialog.FolderName, progress, cancellationToken));
    }

    [RelayCommand(CanExecute = nameof(CanChooseFolder))]
    private async Task ChooseMtpDeviceAsync()
    {
        await CancelActiveScanAsync();
        ClearScanState();
        ScanCancelled?.Invoke(this, EventArgs.Empty);
        IReadOnlyList<MtpDeviceInfo> devices;
        try
        {
            devices = await mtpDeviceService.GetAvailablePortableDevicesAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"无法读取手机设备：{exception.Message}";
            return;
        }

        if (devices.Count == 0)
        {
            ErrorMessage = "未发现可访问的 MTP/PTP 设备请解锁手机，并在 USB 用途中选择“文件传输”或“照片传输”后重试";
            return;
        }

        var dialog = new MtpDevicePickerDialog(devices)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (dialog.ShowDialog() == true && dialog.SelectedDevice is { } device)
        {
            var folderDialog = new MtpFolderPickerDialog(device, mtpDeviceService)
            {
                Owner = System.Windows.Application.Current.MainWindow
            };
            if (folderDialog.ShowDialog() == true && folderDialog.SelectedFolder is { } folder)
            {
                _lastScanWasLocal = false;
                _lastMtpDevice = device;
                _lastMtpRootPath = folder.FullPath;
                await StartScanAsync($"{device.DisplayName}（{GetProtocolName(device.Protocol)}）/{folder.Name}", (progress, cancellationToken) => mtpDeviceService.ScanAsync(device, folder.FullPath, progress, cancellationToken));
            }
        }
    }

    /// <summary>统一处理本地目录和 MTP 设备的扫描生命周期</summary>
    private async Task StartScanAsync(
        string displayPath,
        Func<IProgress<FileScanProgress>, CancellationToken, Task<FolderScanResult>> scan)
    {
        _lastScanPath = displayPath;
        _lastScan = scan;
        IsScanning = true;
        IsScanProgressVisible = true;
        ErrorMessage = null;
        SelectedPath = displayPath;
        FolderInfo = string.Empty;
        var cancellationTokenSource = new CancellationTokenSource();
        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scanCancellationTokenSource = cancellationTokenSource;
        _scanCompletionSource = completionSource;
        var scanSessionId = ++_scanSessionId;
        InitializeCategories();
        ScanStageTitle = "正在读取缓存";
        ScanProgressText = "正在读取缓存...";

        try
        {
            var progress = new Progress<FileScanProgress>(value => UpdateScanProgress(scanSessionId, value));
            var result = await scan(progress, cancellationTokenSource.Token);
            Populate(result);
            ScanCompleted?.Invoke(this, result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ErrorMessage = $"无法完成扫描：{exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_scanCancellationTokenSource, cancellationTokenSource))
            {
                IsScanning = false;
                IsScanProgressVisible = false;
                _scanCancellationTokenSource = null;
                _scanCompletionSource = null;
            }
            cancellationTokenSource.Dispose();
            completionSource.TrySetResult();
        }
    }

    /// <summary>关闭应用前取消扫描并等待当前扫描及缓存写入事务完成</summary>
    public async Task StopAsync()
    {
        _scanSessionId++;
        _scanCancellationTokenSource?.Cancel();
        var completion = _scanCompletionSource?.Task;
        if (completion is not null)
        {
            await completion;
        }
    }

    /// <summary>请求取消当前扫描并立即清空界面状态</summary>
    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void CancelScan()
    {
        _scanSessionId++;
        _scanCancellationTokenSource?.Cancel();
        IsScanning = false;
        IsScanProgressVisible = false;
        ClearScanState();
        ScanCancelled?.Invoke(this, EventArgs.Empty);
    }

    private async Task CancelActiveScanAsync()
    {
        var completion = _scanCompletionSource?.Task;
        if (completion is null)
        {
            return;
        }

        _scanSessionId++;
        _scanCancellationTokenSource?.Cancel();
        await completion;
    }

    /// <summary>使用最近一次扫描来源重新读取当前数据</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshCurrentAsync()
    {
        if (_lastScan is not null)
        {
            if (_lastScanWasLocal)
            {
                await StartScanAsync(_lastScanPath, (progress, cancellationToken) => scanner.RefreshAsync(_lastScanPath, progress, cancellationToken));
            }
            else
            {
                if (_lastMtpDevice is not null && !string.IsNullOrWhiteSpace(_lastMtpRootPath))
                {
                    await StartScanAsync(_lastScanPath, (progress, cancellationToken) => mtpDeviceService.RefreshAsync(_lastMtpDevice, _lastMtpRootPath, progress, cancellationToken));
                }
                else
                {
                    await StartScanAsync(_lastScanPath, _lastScan);
                }
            }
        }
    }

    private bool CanChooseFolder() => !IsScanning;
    private bool CanRefresh() => !IsScanning && _lastScan is not null;

    partial void OnIsScanningChanged(bool value)
    {
        CancelScanCommand.NotifyCanExecuteChanged();
        ChooseFolderCommand.NotifyCanExecuteChanged();
        ChooseMtpDeviceCommand.NotifyCanExecuteChanged();
        RefreshCurrentCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void OpenCategory(FileCategory category) => CategoryRequested?.Invoke(this, category);

    /// <summary>打开本地删除文件的快照管理与恢复窗口</summary>
    [RelayCommand]
    private void OpenDeletedFileRecovery()
    {
        var viewModel = serviceProvider.GetRequiredService<DeletedFileRecoveryViewModel>();
        var dialog = new DeletedFileRecoveryDialog(viewModel)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        viewModel.RecoveryCompleted += RecoveryViewModel_OnRecoveryCompleted;
        dialog.ShowDialog();
        viewModel.RecoveryCompleted -= RecoveryViewModel_OnRecoveryCompleted;
    }

    private async void RecoveryViewModel_OnRecoveryCompleted(object? sender, EventArgs e)
    {
        await RefreshCurrentCommand.ExecuteAsync(null);
    }

    public void ApplyExplorerSnapshot(FolderScanResult result)
    {
        SelectedPath = result.Path;
        Populate(result);
    }

    /// <summary>根据扫描结果更新分类卡片和统计信息</summary>
    private void Populate(FolderScanResult result)
    {
        InitializeCategories();
        var summaries = Enum.GetValues<FileCategory>()
            .ToDictionary(category => category, _ => new CategoryScanSummary(0, 0));
        var totalSize = 0L;
        foreach (var file in result.Files)
        {
            totalSize += file.Size;
            var summary = summaries[file.Category];
            summaries[file.Category] = new CategoryScanSummary(summary.FileCount + 1, summary.TotalSize + file.Size);
        }

        foreach (var category in Enum.GetValues<FileCategory>())
        {
            var summary = summaries[category];
            ReplaceCategory(category, summary.FileCount, summary.TotalSize);
        }

        FolderInfo = $"{result.FolderCount:N0} 个文件夹  |  {result.Files.Count:N0} 个文件  |  {SizeFormatter.Format(totalSize)}";
        ScanProgressText = result.SkippedItemCount == 0
            ? "扫描完成"
            : $"扫描部分完成：已跳过 {result.SkippedItemCount:N0} 个无法读取的目录或文件";
    }

    /// <summary>接收后台服务节流后的扫描统计</summary>
    private void UpdateScanProgress(int scanSessionId, FileScanProgress progress)
    {
        if (scanSessionId != _scanSessionId || !IsScanning)
        {
            return;
        }

        if (progress.Phase is FileScanPhase.ReadingCache or FileScanPhase.Scanning)
        {
            foreach (var category in Enum.GetValues<FileCategory>())
            {
                var summary = progress.CategorySummaries[category];
                ReplaceCategory(category, summary.FileCount, summary.TotalSize);
            }
        }

        (ScanStageTitle, ScanProgressText) = progress.Phase switch
        {
            FileScanPhase.ReadingCache => (
                "正在读取缓存",
                $"已读取 {progress.FilesDiscovered:N0} 个文件元数据，{SizeFormatter.Format(progress.BytesDiscovered)}"),
            FileScanPhase.ValidatingCache => (
                "正在校验缓存",
                $"已检查 {progress.FoldersScanned:N0} 个文件夹，{progress.FilesDiscovered:N0} 个文件"),
            FileScanPhase.RefreshingSource => (
                "正在校验源目录",
                $"已检查 {progress.FoldersScanned:N0} 个文件夹，{progress.FilesDiscovered:N0} 个文件"),
            _ => (
                "正在扫描源目录",
                $"已扫描 {progress.FoldersScanned:N0} 个文件夹，发现 {progress.FilesDiscovered:N0} 个文件，{SizeFormatter.Format(progress.BytesDiscovered)}")
        };
    }

    /// <summary>清空已取消扫描产生的概览数据</summary>
    private void ClearScanState()
    {
        _lastScan = null;
        _lastScanPath = string.Empty;
        _lastScanWasLocal = false;
        _lastMtpDevice = null;
        _lastMtpRootPath = string.Empty;
        Categories.Clear();
        SelectedPath = "尚未选择文件夹";
        FolderInfo = "选择一个本地或可移动磁盘中的文件夹以开始分析";
        ScanProgressText = "扫描已取消，结果已清空";
        ScanStageTitle = "正在准备";
        ErrorMessage = null;
    }

    /// <summary>初始化固定数量的分类卡片</summary>
    private void InitializeCategories()
    {
        Categories.Clear();
        foreach (var category in FileCategoryCatalog.DisplayOrder)
        {
            Categories.Add(CreateCategorySummary(category, 0, 0));
        }
    }

    /// <summary>替换一个分类卡片的不可变统计对象</summary>
    private void ReplaceCategory(FileCategory category, int fileCount, long totalSize)
    {
        var index = Categories.ToList().FindIndex(item => item.Category == category);
        var summary = CreateCategorySummary(category, fileCount, totalSize);
        if (index >= 0)
        {
            Categories[index] = summary;
        }
        else
        {
            Categories.Add(summary);
        }
    }

    /// <summary>创建一个可直接绑定到分类卡片的统计对象</summary>
    private static CategorySummary CreateCategorySummary(FileCategory category, int fileCount, long totalSize) => new()
    {
        Category = category,
        Name = GetCategoryName(category),
        Icon = GetCategoryIcon(category),
        FileCount = fileCount,
        TotalSize = totalSize
    };

    private static string GetCategoryName(FileCategory category) => category switch
    {
        FileCategory.Images => "图像",
        FileCategory.Audio => "音频",
        FileCategory.Video => "视频",
        FileCategory.Office => "Office 与 PDF 文档",
        FileCategory.Archives => "压缩文件",
        FileCategory.SourceCode => "源代码",
        FileCategory.Installers => "安装包",
        _ => "其他文件"
    };

    private static string GetCategoryIcon(FileCategory category) => category switch
    {
        FileCategory.Images => "\uE8B4",
        FileCategory.Audio => "\uE90B",
        FileCategory.Video => "\uE714",
        FileCategory.Office => "\uE8A5",
        FileCategory.Archives => "\uE8B7",
        FileCategory.SourceCode => "\uE943",
        FileCategory.Installers => "\uE896",
        _ => "\uE8A4"
    };

    /// <summary>提供设备扫描来源的协议标识</summary>
    private static string GetProtocolName(PortableDeviceProtocol protocol) => protocol == PortableDeviceProtocol.Ptp ? "PTP" : "MTP";
}
