using System.Collections.ObjectModel;
using System.Windows.Forms;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileGroupy.Models;
using FileGroupy.Services;
using FileGroupy.Views;

namespace FileGroupy.ViewModels;

/// <summary>概览页视图模型，负责选择目录、启动扫描并生成分类卡片</summary>
/// <param name="scanner">通过依赖注入提供的目录扫描服务</param>
public partial class DashboardViewModel(IFileScannerService scanner, IMtpDeviceService mtpDeviceService) : ObservableObject
{
    /// <summary>当前扫描使用的取消源；空值表示没有正在执行的扫描</summary>
    private CancellationTokenSource? _scanCancellationTokenSource;
    /// <summary>递增的扫描会话编号，用于丢弃已取消扫描排队到 UI 线程的旧进度</summary>
    private int _scanSessionId;

    /// <summary>扫描完成后显示在概览页的分类统计卡片集合</summary>
    public ObservableCollection<CategorySummary> Categories { get; } = [];

    /// <summary>由工具生成的当前已选目录路径公开绑定属性</summary>
    [ObservableProperty] private string _selectedPath = "尚未选择文件夹";
    /// <summary>由工具生成的目录基础统计信息公开绑定属性</summary>
    [ObservableProperty] private string _folderInfo = "选择一个本地或可移动磁盘中的文件夹以开始分析";
    /// <summary>由工具生成的扫描执行状态公开绑定属性</summary>
    [ObservableProperty] private bool _isScanning;
    /// <summary>由工具生成的实时扫描统计公开绑定属性</summary>
    [ObservableProperty] private string _scanProgressText = string.Empty;
    /// <summary>由工具生成的扫描失败提示公开绑定属性</summary>
    [ObservableProperty] private string? _errorMessage;

    /// <summary>目录扫描成功后触发，使外层导航可更新文件浏览页</summary>
    public event EventHandler<FolderScanResult>? ScanCompleted;
    /// <summary>用户取消扫描并清空界面状态后触发，使其他页面同步释放旧扫描结果</summary>
    public event EventHandler? ScanCancelled;
    /// <summary>用户点击分类卡片时触发，通知外层导航切换至对应分类</summary>
    public event EventHandler<FileCategory>? CategoryRequested;

    /// <summary>显示文件夹选择对话框并异步扫描用户选定目录</summary>
    /// <summary>请求打开指定分类的文件列表</summary>
    /// <param name="category">用户点击的文件分类</param>
    /// <summary>根据扫描结果重新计算并填充所有分类卡片</summary>
    /// <param name="result">刚完成的目录扫描结果</param>
    /// <summary>返回指定分类的中文显示名称</summary>
    /// <param name="category">待转换的文件分类</param>
    /// <returns>用于界面的中文名称</returns>
    /// <summary>返回指定分类用于卡片展示的简短标识</summary>
    /// <param name="category">待转换的文件分类</param>
    /// <returns>用于界面的简短文本</returns>
    [RelayCommand(CanExecute = nameof(CanChooseFolder))]
    private async Task ChooseFolderAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择要分析的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        await StartScanAsync(dialog.SelectedPath, (progress, cancellationToken) => scanner.ScanAsync(dialog.SelectedPath, progress, cancellationToken));
    }

    /// <summary>显示当前 Windows 已识别的 MTP/PTP 设备，并扫描用户选中的可访问目录</summary>
    [RelayCommand(CanExecute = nameof(CanChooseFolder))]
    private async Task ChooseMtpDeviceAsync()
    {
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
                await StartScanAsync($"{device.DisplayName}（{GetProtocolName(device.Protocol)}）/{folder.Name}", (progress, cancellationToken) => mtpDeviceService.ScanAsync(device, folder.FullPath, progress, cancellationToken));
            }
        }
    }

    /// <summary>统一处理本地目录和 MTP 设备的扫描生命周期</summary>
    private async Task StartScanAsync(
        string displayPath,
        Func<IProgress<FileScanProgress>, CancellationToken, Task<FolderScanResult>> scan)
    {
        IsScanning = true;
        ErrorMessage = null;
        SelectedPath = displayPath;
        _scanCancellationTokenSource = new CancellationTokenSource();
        var scanSessionId = ++_scanSessionId;
        InitializeCategories();
        ScanProgressText = "正在准备扫描...";

        try
        {
            var progress = new Progress<FileScanProgress>(value => UpdateScanProgress(scanSessionId, value));
            var result = await scan(progress, _scanCancellationTokenSource.Token);
            Populate(result);
            ScanCompleted?.Invoke(this, result);
        }
        catch (OperationCanceledException)
        {
            // 取消按钮已立即重置状态；此处仅接收后台任务结束信号
        }
        catch (Exception exception)
        {
            ErrorMessage = $"无法完成扫描：{exception.Message}";
        }
        finally
        {
            IsScanning = false;
            _scanCancellationTokenSource.Dispose();
            _scanCancellationTokenSource = null;
        }
    }

    /// <summary>请求取消当前扫描，扫描器会在下一个目录或条目检查点停止</summary>
    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void CancelScan()
    {
        // 使已排队的旧进度回调失效，再立即释放界面和跨页面保存的扫描结果
        _scanSessionId++;
        _scanCancellationTokenSource?.Cancel();
        ClearScanState();
        ScanCancelled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>决定是否允许打开新的目录选择对话框</summary>
    private bool CanChooseFolder() => !IsScanning;

    partial void OnIsScanningChanged(bool value)
    {
        CancelScanCommand.NotifyCanExecuteChanged();
        ChooseFolderCommand.NotifyCanExecuteChanged();
        ChooseMtpDeviceCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void OpenCategory(FileCategory category) => CategoryRequested?.Invoke(this, category);

    /// <summary>接收文件浏览页的增删改结果并刷新概览卡片与统计信息。</summary>
    /// <param name="result">基于当前扫描路径的最新文件集合快照。</param>
    public void ApplyExplorerSnapshot(FolderScanResult result)
    {
        SelectedPath = result.Path;
        Populate(result);
    }

    private void Populate(FolderScanResult result)
    {
        InitializeCategories();
        foreach (var category in Enum.GetValues<FileCategory>())
        {
            var items = result.Files.Where(file => file.Category == category).ToList();
            ReplaceCategory(category, items.Count, items.Sum(file => file.Size));
        }

        FolderInfo = $"{result.FolderCount:N0} 个文件夹  |  {result.Files.Count:N0} 个文件  |  {SizeFormatter.Format(result.Files.Sum(file => file.Size))}";
        ScanProgressText = result.SkippedItemCount == 0
            ? "扫描完成"
            : $"扫描部分完成：已跳过 {result.SkippedItemCount:N0} 个无法读取的目录或文件";
    }

    /// <summary>接收后台服务节流后的扫描统计，仅更新固定数量的分类卡片以控制渲染开销</summary>
    /// <param name="scanSessionId">产生该进度的扫描会话编号</param>
    /// <param name="progress">当前已发现文件与分类汇总快照</param>
    private void UpdateScanProgress(int scanSessionId, FileScanProgress progress)
    {
        if (scanSessionId != _scanSessionId || !IsScanning)
        {
            return;
        }

        foreach (var category in Enum.GetValues<FileCategory>())
        {
            var summary = progress.CategorySummaries[category];
            ReplaceCategory(category, summary.FileCount, summary.TotalSize);
        }

        ScanProgressText = $"已扫描 {progress.FoldersScanned:N0} 个文件夹，发现 {progress.FilesDiscovered:N0} 个文件，{SizeFormatter.Format(progress.BytesDiscovered)}";
    }

    /// <summary>清空取消扫描产生的所有概览数据，使界面恢复到初始状态</summary>
    private void ClearScanState()
    {
        Categories.Clear();
        SelectedPath = "尚未选择文件夹";
        FolderInfo = "选择一个本地或可移动磁盘中的文件夹以开始分析";
        ScanProgressText = "扫描已取消，结果已清空";
        ErrorMessage = null;
    }

    /// <summary>初始化固定数量的分类卡片，使扫描开始后界面可立即呈现分类结构</summary>
    private void InitializeCategories()
    {
        Categories.Clear();
        foreach (var category in Enum.GetValues<FileCategory>())
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
        _ => "其他文件"
    };

    private static string GetCategoryIcon(FileCategory category) => category switch
    {
        FileCategory.Images => "图",
        FileCategory.Audio => "音",
        FileCategory.Video => "影",
        FileCategory.Office => "文档",
        FileCategory.Archives => "包",
        FileCategory.SourceCode => "代码",
        _ => "他"
    };

    /// <summary>提供设备扫描来源的协议标识</summary>
    private static string GetProtocolName(PortableDeviceProtocol protocol) => protocol == PortableDeviceProtocol.Ptp ? "PTP" : "MTP";
}