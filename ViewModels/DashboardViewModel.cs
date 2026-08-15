using System.Collections.ObjectModel;
using System.Windows.Forms;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileGroupy.Models;
using FileGroupy.Services;
using FileGroupy.Views;

namespace FileGroupy.ViewModels;

/// <summary>概览页视图模型, 负责选择来源, 扫描文件并生成分类卡片</summary>
public partial class DashboardViewModel(IFileScannerService scanner, IMtpDeviceService mtpDeviceService) : ObservableObject
{
    /// <summary>当前扫描使用的取消源, 空值表示没有正在执行的扫描</summary>
    private CancellationTokenSource? _scanCancellationTokenSource;
    /// <summary>最近一次扫描委托, 用于刷新当前来源</summary>
    private Func<IProgress<FileScanProgress>, CancellationToken, Task<FolderScanResult>>? _lastScan;
    /// <summary>最近一次扫描的显示路径</summary>
    private string _lastScanPath = string.Empty;
    /// <summary>用于丢弃已取消扫描产生的旧进度</summary>
    private int _scanSessionId;

    /// <summary>扫描完成后显示在概览页的分类统计卡片集合</summary>
    public ObservableCollection<CategorySummary> Categories { get; } = [];

    /// <summary>当前扫描来源的显示路径</summary>
    [ObservableProperty] private string _selectedPath = "尚未选择文件夹";
    /// <summary>当前来源的基础统计信息</summary>
    [ObservableProperty] private string _folderInfo = "选择一个本地或可移动磁盘中的文件夹以开始分析";
    /// <summary>是否正在扫描</summary>
    [ObservableProperty] private bool _isScanning;
    /// <summary>实时扫描进度文本</summary>
    [ObservableProperty] private string _scanProgressText = string.Empty;
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
        _lastScanPath = displayPath;
        _lastScan = scan;
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

    /// <summary>请求取消当前扫描并立即清空界面状态</summary>
    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void CancelScan()
    {
        _scanSessionId++;
        _scanCancellationTokenSource?.Cancel();
        ClearScanState();
        ScanCancelled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>使用最近一次扫描来源重新读取当前数据</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshCurrentAsync()
    {
        if (_lastScan is not null)
        {
            await StartScanAsync(_lastScanPath, _lastScan);
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

    public void ApplyExplorerSnapshot(FolderScanResult result)
    {
        SelectedPath = result.Path;
        Populate(result);
    }

    /// <summary>根据扫描结果更新分类卡片和统计信息</summary>
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

    /// <summary>接收后台服务节流后的扫描统计</summary>
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

    /// <summary>清空已取消扫描产生的概览数据</summary>
    private void ClearScanState()
    {
        Categories.Clear();
        SelectedPath = "尚未选择文件夹";
        FolderInfo = "选择一个本地或可移动磁盘中的文件夹以开始分析";
        ScanProgressText = "扫描已取消，结果已清空";
        ErrorMessage = null;
    }

    /// <summary>初始化固定数量的分类卡片</summary>
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
