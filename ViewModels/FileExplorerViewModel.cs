using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileGroupy.Controls;
using FileGroupy.Models;
using FileGroupy.Services;
using FileGroupy.Views;

namespace FileGroupy.ViewModels;

/// <summary>文件浏览页视图模型, 提供分类树, 选择状态和批量文件操作</summary>
public partial class FileExplorerViewModel(
    IFileTransferService transferService,
    IMtpDeviceService mtpDeviceService,
    IFilePreviewService previewService,
    IFileScannerService scanner,
    IDeletedFileRecoveryService recoveryService,
    IPathHistoryStore pathHistoryStore,
    IScanCacheStore scanCacheStore) : ObservableObject
{
    /// <summary>最近一次扫描的全部文件</summary>
    private readonly List<FileItem> _files = [];
    /// <summary>当前展开的分类节点集合</summary>
    private readonly HashSet<FileCategory> _expandedCategories = Enum.GetValues<FileCategory>().ToHashSet();
    /// <summary>当前展开的扩展名节点集合</summary>
    private readonly HashSet<string> _expandedExtensions = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>跨折叠和视图重建保留的已选文件完整路径集合</summary>
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>可选的单一分类筛选条件;为空时显示所有分类</summary>
    private FileCategory? _categoryFilter;
    /// <summary>当前搜索命中的文件路径, 空值表示未应用搜索</summary>
    private HashSet<string>? _searchMatchedPaths;
    /// <summary>用于取消被新关键字取代的后台搜索任务</summary>
    private CancellationTokenSource? _searchCancellationTokenSource;
    /// <summary>树操作版本, 用于丢弃过期异步结果</summary>
    private int _treeOperationVersion;
    /// <summary>最近一次扫描结果的展示路径</summary>
    private string _currentScanPath = string.Empty;
    /// <summary>最近一次扫描结果中的目录数量</summary>
    private int _currentFolderCount;
    /// <summary>最近一次扫描结果中的跳过项数量</summary>
    private int _currentSkippedItemCount;
    /// <summary>当前结果是否来自本地目录扫描</summary>
    private bool _isLocalScan;
    /// <summary>是否仅显示延迟校验后无法解码的图像文件</summary>
    private bool _showInvalidImagesOnly;
    /// <summary>用于取消无效图像延迟校验任务</summary>
    private CancellationTokenSource? _invalidImageValidationCancellationTokenSource;
    /// <summary>当前本地扫描根目录的外部变更监听器</summary>
    private FileSystemWatcher? _localFileWatcher;
    /// <summary>用于合并短时间内连续文件变更通知的防抖取消源</summary>
    private CancellationTokenSource? _fileChangeRefreshCancellationTokenSource;
    /// <summary>监听器刚启用时忽略扫描期间积压的延迟事件</summary>
    private DateTimeOffset _localWatcherStartedAt;
    /// <summary>当前目录的外部变化是否已经提示, 防止同一批变化重复弹窗</summary>
    private bool _hasNotifiedExternalChange;

    /// <summary>绑定到表格的根行和文件子行集合</summary>
    public BulkObservableCollection<ExplorerRow> Rows { get; } = [];
    /// <summary>文件树可选的最大展开层级</summary>
    public IReadOnlyList<int> ExpansionLevels { get; } = [1, 2, 3, 4, 5];

    /// <summary>页面标题</summary>
    [ObservableProperty] private string _title = "全部文件";
    /// <summary>页面副标题</summary>
    [ObservableProperty] private string _subtitle = "选择文件夹后，以文件类型为根节点浏览内容";
    /// <summary>表头全选框状态</summary>
    [ObservableProperty] private bool _isAllSelected;
    /// <summary>当前已选文件数量</summary>
    [ObservableProperty] private int _selectedFileCount;
    /// <summary>批量展开目标层级</summary>
    [ObservableProperty] private int _selectedExpansionLevel = 2;
    /// <summary>树操作是否正在进行</summary>
    [ObservableProperty] private bool _isTreeOperationInProgress;
    /// <summary>是否正在刷新当前 DataGrid 数据源</summary>
    [ObservableProperty] private bool _isRefreshOverlayVisible;
    /// <summary>与 Dashboard 同步的刷新阶段标题</summary>
    [ObservableProperty] private string _refreshStageTitle = "正在准备";
    /// <summary>与 Dashboard 同步的刷新进度文本</summary>
    [ObservableProperty] private string _refreshProgressText = string.Empty;
    /// <summary>文件模糊检索关键字</summary>
    [ObservableProperty] private string _searchQuery = string.Empty;
    /// <summary>最近一次文件操作的状态文本</summary>
    [ObservableProperty] private string _operationStatus = string.Empty;
    /// <summary>当前在表格中选中的行, 仅用于同步表格状态和详情侧栏</summary>
    [ObservableProperty] private ExplorerRow? _selectedRow;
    /// <summary>文件详情侧栏是否打开, 仅由右键菜单显式触发</summary>
    [ObservableProperty] private bool _isDetailsDrawerOpen;
    /// <summary>当前扫描结果是否已完成无效图像延迟校验</summary>
    [ObservableProperty] private bool _hasInvalidImageValidation;

    /// <summary>是否存在可展示的文件操作状态</summary>
    public bool HasOperationStatus => !string.IsNullOrWhiteSpace(OperationStatus);
    /// <summary>扫描结果中无法解码的图像数量</summary>
    public int InvalidImageCount => _files.Count(file => file.IsInvalidImage == true);
    /// <summary>是否正在仅显示无效图像</summary>
    public bool IsInvalidImagesFilterActive => _showInvalidImagesOnly;
    /// <summary>无效图像按钮在校验前后的提示文本</summary>
    public string InvalidImageFilterText => HasInvalidImageValidation ? "无效图像" : "无效图像 (点击检测)";
    /// <summary>当前搜索结果的数量提示</summary>
    public string SearchResultText => string.IsNullOrWhiteSpace(SearchQuery)
        ? string.Empty
        : $"匹配 {_searchMatchedPaths?.Count ?? 0:N0} 个文件";
    /// <summary>是否存在可清空的搜索关键字</summary>
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);

    /// <summary>文件集合发生增删改后触发</summary>
    public event EventHandler<FolderScanResult>? FilesChanged;
    /// <summary>请求重新扫描当前来源</summary>
    public event EventHandler? RefreshRequested;
    /// <summary>当前加载目录检测到外部变化时请求主窗口显示提示</summary>
    public event EventHandler<string>? ExternalChangeDetected;

    /// <summary>接收新扫描结果并显示全部分类</summary>
    public void Load(FolderScanResult result)
    {
        _files.Clear();
        _files.AddRange(result.Files);
        _selectedPaths.Clear();
        _searchMatchedPaths = null;
        SearchQuery = string.Empty;
        _categoryFilter = null;
        _showInvalidImagesOnly = false;
        HasInvalidImageValidation = false;
        OnPropertyChanged(nameof(IsInvalidImagesFilterActive));
        OperationStatus = string.Empty;
        _currentScanPath = result.Path;
        _currentFolderCount = result.FolderCount;
        _currentSkippedItemCount = result.SkippedItemCount;
        _isLocalScan = result.Files.Count == 0 || result.Files.All(file => file.SourceKind == StorageSourceKind.LocalFileSystem);
        _expandedCategories.Clear();
        _expandedExtensions.Clear();
        ConfigureLocalFileWatcher();
        OnPropertyChanged(nameof(InvalidImageCount));
        Title = "全部文件";
        Subtitle = result.SkippedItemCount == 0
            ? $"{result.Path}  |  {result.Files.Count:N0} 个文件"
            : $"{result.Path}  |  {result.Files.Count:N0} 个文件  |  已跳过 {result.SkippedItemCount:N0} 个无法读取项";
        BuildRows();
        ExpandToLevelCommand.NotifyCanExecuteChanged();
        CollapseAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>清空上一次扫描的文件、选择和树形行,使浏览页面恢复初始状态</summary>
    public void Clear()
    {
        _files.Clear();
        _selectedPaths.Clear();
        _searchMatchedPaths = null;
        SearchQuery = string.Empty;
        _expandedExtensions.Clear();
        _expandedCategories.Clear();
        _expandedCategories.UnionWith(Enum.GetValues<FileCategory>());
        _categoryFilter = null;
        _showInvalidImagesOnly = false;
        HasInvalidImageValidation = false;
        OnPropertyChanged(nameof(IsInvalidImagesFilterActive));
        OperationStatus = string.Empty;
        _currentScanPath = string.Empty;
        _currentFolderCount = 0;
        _currentSkippedItemCount = 0;
        _isLocalScan = false;
        DisposeLocalFileWatcher();
        Rows.Clear();
        Title = "全部文件";
        Subtitle = "选择文件夹后，以文件类型为根节点浏览内容";
        UpdateSelectionSummary();
        ExpandToLevelCommand.NotifyCanExecuteChanged();
        CollapseAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>仅显示指定分类下的文件</summary>
    public void ShowCategory(FileCategory category)
    {
        _categoryFilter = category;
        _showInvalidImagesOnly = false;
        OnPropertyChanged(nameof(IsInvalidImagesFilterActive));
        Title = $"{GetCategoryName(category)}文件";
        Subtitle = "分类卡片筛选结果";
        BuildRows();
    }

    /// <summary>清除分类筛选并显示全部文件</summary>
    public void ShowAll()
    {
        _categoryFilter = null;
        _showInvalidImagesOnly = false;
        OnPropertyChanged(nameof(IsInvalidImagesFilterActive));
        Title = "全部文件";
        Subtitle = _files.Count == 0 ? "选择文件夹后，以文件类型为根节点浏览内容" : $"共 {_files.Count:N0} 个文件";
        BuildRows();
    }

    /// <summary>请求刷新当前扫描来源</summary>
    [RelayCommand]
    private void RefreshCurrent() => RefreshRequested?.Invoke(this, EventArgs.Empty);

    public void BeginRefreshOverlay(string stageTitle, string progressText)
    {
        RefreshStageTitle = stageTitle;
        RefreshProgressText = progressText;
        IsRefreshOverlayVisible = true;
    }

    public void UpdateRefreshOverlay(string stageTitle, string progressText)
    {
        if (!IsRefreshOverlayVisible)
        {
            return;
        }

        RefreshStageTitle = stageTitle;
        RefreshProgressText = progressText;
    }

    public void EndRefreshOverlay() => IsRefreshOverlayVisible = false;

    /// <summary>关闭当前文件详情侧栏</summary>
    [RelayCommand]
    private void CloseDetails() => IsDetailsDrawerOpen = false;

    /// <summary>显示指定文件行的详情侧栏</summary>
    [RelayCommand]
    private void ShowDetails(ExplorerRow? row)
    {
        if (row?.File is null)
        {
            return;
        }

        SelectedRow = row;
        IsDetailsDrawerOpen = true;
    }

    /// <summary>清除当前全部选择</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        _selectedPaths.Clear();
        SynchronizeVisibleSelection();
    }

    /// <summary>切换仅显示无法解码图像的筛选条件</summary>
    [RelayCommand]
    private async Task ToggleInvalidImagesFilterAsync()
    {
        // 关闭筛选只需恢复完整文件树, 已完成的校验结果仍保留在文件模型中.
        if (_showInvalidImagesOnly)
        {
            _showInvalidImagesOnly = false;
            OnPropertyChanged(nameof(IsInvalidImagesFilterActive));
            Title = "全部文件";
            Subtitle = $"共 {_files.Count:N0} 个文件";
            BuildRows();
            return;
        }

        _invalidImageValidationCancellationTokenSource?.Cancel();
        _invalidImageValidationCancellationTokenSource?.Dispose();
        _invalidImageValidationCancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _invalidImageValidationCancellationTokenSource.Token;
        IsTreeOperationInProgress = true;
        _showInvalidImagesOnly = !_showInvalidImagesOnly;
        OnPropertyChanged(nameof(IsInvalidImagesFilterActive));
        Title = "无效图像";
        Subtitle = "正在验证图像文件...";

        try
        {
            // 快照避免后台校验期间受界面筛选或文件操作影响.
            var imageFiles = _files.Where(file => file.Category == FileCategory.Images).ToArray();
            var localImages = imageFiles.Where(file => file.SourceKind == StorageSourceKind.LocalFileSystem).ToArray();
            var deviceImages = imageFiles.Where(file => file.SourceKind == StorageSourceKind.MtpDevice).ToArray();
            var localTask = scanner.FindInvalidImagePathsAsync(localImages, cancellationToken);
            var deviceTask = mtpDeviceService.FindInvalidImagePathsAsync(deviceImages, cancellationToken);
            await Task.WhenAll(localTask, deviceTask);
            cancellationToken.ThrowIfCancellationRequested();
            var invalidPaths = localTask.Result.Concat(deviceTask.Result).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < _files.Count; index++)
            {
                var file = _files[index];
                if (file.Category == FileCategory.Images)
                {
                    _files[index] = file with { IsInvalidImage = invalidPaths.Contains(file.FullPath) };
                }
            }

            OnPropertyChanged(nameof(InvalidImageCount));
            HasInvalidImageValidation = true;
            Subtitle = $"已验证 {imageFiles.Length:N0} 个图像, 发现 {InvalidImageCount:N0} 个无法解码文件";
            // 校验完成后一次性重建行集合, 避免每个坏图逐条触发 UI 更新.
            await RebuildRowsAsync(++_treeOperationVersion);
        }
        catch (OperationCanceledException)
        {
            _showInvalidImagesOnly = false;
            HasInvalidImageValidation = false;
            OnPropertyChanged(nameof(IsInvalidImagesFilterActive));
            Title = "全部文件";
            Subtitle = _files.Count == 0 ? "选择文件夹后，以文件类型为根节点浏览内容" : $"共 {_files.Count:N0} 个文件";
            BuildRows();
        }
        finally
        {
            IsTreeOperationInProgress = false;
        }
    }

    partial void OnHasInvalidImageValidationChanged(bool value) => OnPropertyChanged(nameof(InvalidImageFilterText));

    /// <summary>展开或折叠分类及扩展名节点</summary>
    [RelayCommand]
    private async Task ToggleGroupAsync(ExplorerRow row)
    {
        // 分类展开只生成直接子项, 不重新构建整棵树, 以降低大目录展开成本.
        if (IsTreeOperationInProgress)
        {
            return;
        }

        var expand = !row.IsExpanded;
        if (row.IsCategory)
        {
            if (expand)
            {
                _expandedCategories.Add(row.Category);
            }
            else
            {
                _expandedCategories.Remove(row.Category);
            }
        }
        else if (row.IsExtensionGroup)
        {
            var key = GetExtensionKey(row.Category, row.GroupExtension);
            if (expand)
            {
                _expandedExtensions.Add(key);
            }
            else
            {
                _expandedExtensions.Remove(key);
            }
        }
        else
        {
            return;
        }

        row.IsExpanded = expand;
        var rowIndex = Rows.IndexOf(row);
        if (rowIndex < 0)
        {
            BuildRows();
            return;
        }

        if (!expand)
        {
            Rows.RemoveRange(rowIndex + 1, GetVisibleDescendantCount(rowIndex, row));
            return;
        }

        var operationVersion = ++_treeOperationVersion;
        IsTreeOperationInProgress = true;
        try
        {
            var children = await Task.Run(() => CreateDirectChildren(row));
            if (operationVersion == _treeOperationVersion)
            {
                Rows.InsertRange(rowIndex + 1, children);
            }
        }
        finally
        {
            if (operationVersion == _treeOperationVersion)
            {
                IsTreeOperationInProgress = false;
            }
        }
    }

    /// <summary>异步展开至选定深度;本树只有分类、扩展名、文件三层,超过三级等同于完全展开</summary>
    [RelayCommand(CanExecute = nameof(CanChangeTreeExpansion))]
    private async Task ExpandToLevelAsync()
    {
        var operationVersion = ++_treeOperationVersion;
        IsTreeOperationInProgress = true;
        ExpandToLevel(SelectedExpansionLevel);
        await RebuildRowsAsync(operationVersion);
    }

    /// <summary>异步折叠所有分类节点,保留扫描和选择状态</summary>
    [RelayCommand(CanExecute = nameof(CanChangeTreeExpansion))]
    private async Task CollapseAllAsync()
    {
        var operationVersion = ++_treeOperationVersion;
        IsTreeOperationInProgress = true;
        _expandedCategories.Clear();
        _expandedExtensions.Clear();
        await RebuildRowsAsync(operationVersion);
    }

    /// <summary>判断是否允许执行批量展开或收缩</summary>
    private bool CanChangeTreeExpansion() => !IsTreeOperationInProgress && _files.Count > 0;

    partial void OnIsTreeOperationInProgressChanged(bool value)
    {
        ExpandToLevelCommand.NotifyCanExecuteChanged();
        CollapseAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>切换单个文件或分组文件的选择状态</summary>
    [RelayCommand]
    private void ToggleRowSelection(ExplorerRow row)
    {
        if (row.IsCategory)
        {
            SetSelection(_files.Where(file => file.Category == row.Category));
        }
        else if (row.IsExtensionGroup)
        {
            SetSelection(_files.Where(file => file.Category == row.Category && string.Equals(file.Extension, row.GroupExtension, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            SetSelection([row.File!]);
        }

        SynchronizeVisibleSelection();
    }

    /// <summary>按当前表头状态选择或取消选择可见文件</summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        SetSelection(GetVisibleFiles(), !IsAllSelected);
        SynchronizeVisibleSelection();
    }

    /// <summary>打开复制对话框</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopySelectedAsync() => await ShowTransferDialogAsync(false);

    /// <summary>打开移动对话框</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task MoveSelectedAsync() => await ShowTransferDialogAsync(true);

    /// <summary>打开删除对话框</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteSelectedAsync() => await ShowDeleteDialogAsync();

    /// <summary>判断是否至少选择了一个文件</summary>
    private bool HasSelection() => SelectedFileCount > 0;

    /// <summary>仅允许对实际文件行执行打开操作</summary>
    private static bool CanOpenFile(ExplorerRow? row) => row?.File is not null;

    /// <summary>使用 Windows 默认关联打开文件</summary>
    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    private async Task OpenFileAsync(ExplorerRow? row)
    {
        if (row?.File is null)
        {
            return;
        }

        try
        {
            await previewService.OpenAsync(row.File);
        }
        catch (Exception exception)
        {
            ShowOpenError(row.File, exception);
        }
    }

    /// <summary>显示 Windows 打开方式选择器</summary>
    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    private async Task OpenWithFileAsync(ExplorerRow? row)
    {
        if (row?.File is null)
        {
            return;
        }

        try
        {
            await previewService.OpenWithApplicationAsync(row.File);
        }
        catch (Exception exception)
        {
            ShowOpenError(row.File, exception);
        }
    }

    /// <summary>在 Windows 文件资源管理器中选中本地源文件</summary>
    [RelayCommand(CanExecute = nameof(CanOpenLocalFile))]
    private async Task OpenFileLocationAsync(ExplorerRow? row)
    {
        if (row?.File is null)
        {
            return;
        }

        try
        {
            await previewService.OpenFileLocationAsync(row.File);
        }
        catch (Exception exception)
        {
            ShowOpenError(row.File, exception);
        }
    }

    /// <summary>仅本地文件支持在资源管理器中定位</summary>
    private static bool CanOpenLocalFile(ExplorerRow? row) =>
        row?.File?.SourceKind == StorageSourceKind.LocalFileSystem;

    /// <summary>确保右键命中的文件行已加入当前选择</summary>
    public void EnsureContextRowSelected(ExplorerRow row)
    {
        if (row.File is null)
        {
            return;
        }

        if (_selectedPaths.Contains(row.File.FullPath))
        {
            return;
        }

        _selectedPaths.Add(row.File.FullPath);
        SynchronizeVisibleSelection();
    }

    /// <summary>为图片文件创建悬停预览数据</summary>
    public async Task<ImageHoverPreview?> CreateImageHoverPreviewAsync(ExplorerRow row, CancellationToken cancellationToken = default)
    {
        if (row.File is null || row.File.Category != FileCategory.Images)
        {
            return null;
        }

        if (string.Equals(row.File.Extension, ".svg", StringComparison.OrdinalIgnoreCase))
        {
            return new ImageHoverPreview(null, "矢量图像", SizeFormatter.Format(row.File.Size), "SVG", false);
        }

        try
        {
            var preview = await previewService.CreatePreviewAsync(row.File, cancellationToken);
            if (preview?.ImageSource is not ImageSource imageSource)
            {
                return new ImageHoverPreview(null, "未知", SizeFormatter.Format(row.File.Size), row.File.Extension.ToUpperInvariant(), true);
            }

            var resolution = preview.ImageSource is System.Windows.Media.Imaging.BitmapSource bitmap
                ? $"{bitmap.PixelWidth} × {bitmap.PixelHeight}"
                : "未知";
            return new ImageHoverPreview(
                imageSource,
                resolution,
                SizeFormatter.Format(row.File.Size),
                string.IsNullOrWhiteSpace(row.File.Extension) ? "未知" : row.File.Extension.ToUpperInvariant(),
                false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new ImageHoverPreview(null, "未知", SizeFormatter.Format(row.File.Size), row.File.Extension.ToUpperInvariant(), true);
        }
    }

    /// <summary>打开复制或移动对话框, 完成后同步文件树和缓存</summary>
    private async Task ShowTransferDialogAsync(bool moveFiles)
    {
        var selectedFiles = _files.Where(file => _selectedPaths.Contains(file.FullPath)).ToList();
        var dialogViewModel = new FileTransferDialogViewModel(transferService, mtpDeviceService, pathHistoryStore, selectedFiles, moveFiles);
        var dialog = new FileTransferDialog(dialogViewModel)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        dialog.ShowDialog();
        if (dialogViewModel.LastResult is { } result)
        {
            ApplyTransferResult(moveFiles, result);
            InvalidateCurrentLocalScanCache();
            foreach (var sourceDeviceId in selectedFiles.Where(file => file.SourceKind == StorageSourceKind.MtpDevice)
                         .Select(file => file.SourceId)
                         .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
                         .Distinct(StringComparer.Ordinal)
                         .Cast<string>())
            {
                mtpDeviceService.InvalidateScanCache(sourceDeviceId);
            }

            if (dialogViewModel.DestinationMtpDevice is { } destinationMtp)
            {
                mtpDeviceService.InvalidateScanCache(destinationMtp.DeviceId);
            }

            ShowOperationResultMessage(moveFiles ? "移动" : "复制", result.Succeeded, result.Skipped, result.Failures.Count);
            RequestBackgroundRefresh();
        }

        await Task.CompletedTask;
    }

    /// <summary>打开删除对话框, 完成后同步文件树和缓存</summary>
    private async Task ShowDeleteDialogAsync()
    {
        var selectedFiles = _files.Where(file => _selectedPaths.Contains(file.FullPath)).ToList();
        var dialogViewModel = new FileDeleteDialogViewModel(transferService, recoveryService, selectedFiles);
        var dialog = new FileDeleteDialog(dialogViewModel)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        dialog.ShowDialog();
        if (dialogViewModel.LastResult is { } result)
        {
            RemoveFilesBySourcePaths(result.SuccessfulSourcePaths);
            InvalidateCurrentLocalScanCache();
            BuildRows();
            foreach (var deviceId in selectedFiles.Where(file => file.SourceKind == StorageSourceKind.MtpDevice)
                         .Select(file => file.SourceId)
                         .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
                         .Distinct(StringComparer.Ordinal)
                         .Cast<string>())
            {
                mtpDeviceService.InvalidateScanCache(deviceId);
            }

            NotifyFilesChanged();
            ShowOperationResultMessage("删除", result.Succeeded, result.Skipped, result.Failures.Count);
            RequestBackgroundRefresh();
        }

        await Task.CompletedTask;
    }

    /// <summary>根据复制或移动结果更新当前文件集合</summary>
    private void ApplyTransferResult(bool moveFiles, FileTransferResult result)
    {
        if (moveFiles)
        {
            RemoveFilesBySourcePaths(result.SuccessfulSourcePaths);
        }

        var successfulTransfers = result.SuccessfulTransfers ?? [];
        if (successfulTransfers.Count > 0)
        {
            AddLocalDestinationFiles(successfulTransfers);
        }

        BuildRows();
        NotifyFilesChanged();
    }

    /// <summary>将批量操作结果显示在当前页面</summary>
    private void ShowOperationResultMessage(string operationName, int succeeded, int skipped, int failed)
    {
        OperationStatus = $"{operationName}完成：成功 {succeeded:N0}，跳过 {skipped:N0}，失败 {failed:N0}";
    }

    partial void OnOperationStatusChanged(string value) => OnPropertyChanged(nameof(HasOperationStatus));

    /// <summary>从当前文件集合中移除指定源路径</summary>
    private void RemoveFilesBySourcePaths(IEnumerable<string> sourcePaths)
    {
        var removedPathSet = sourcePaths.Select(NormalizeSourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (removedPathSet.Count == 0)
        {
            return;
        }

        _files.RemoveAll(file => removedPathSet.Contains(NormalizeSourcePath(file.FullPath)));
        _selectedPaths.RemoveWhere(path => removedPathSet.Contains(NormalizeSourcePath(path)));
        _searchMatchedPaths?.RemoveWhere(path => removedPathSet.Contains(NormalizeSourcePath(path)));
        OnPropertyChanged(nameof(InvalidImageCount));
    }

    /// <summary>补充移动或复制到当前本地扫描范围内的新文件</summary>
    private void AddLocalDestinationFiles(IReadOnlyList<FileTransferSuccess> successfulTransfers)
    {
        if (!_isLocalScan || string.IsNullOrWhiteSpace(_currentScanPath))
        {
            return;
        }

        var currentRoot = NormalizeSourcePath(_currentScanPath);
        foreach (var transfer in successfulTransfers)
        {
            if (string.IsNullOrWhiteSpace(transfer.DestinationPath))
            {
                continue;
            }

            var destinationPath = NormalizeSourcePath(transfer.DestinationPath);
            if (!destinationPath.StartsWith(currentRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(destinationPath))
            {
                continue;
            }

            if (_files.Any(file => string.Equals(NormalizeSourcePath(file.FullPath), destinationPath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var info = new FileInfo(destinationPath);
            var category = FileCategoryCatalog.GetCategory(info.Extension);
            _files.Add(new FileItem(info.Name, info.FullName, info.Extension, info.Length, info.LastWriteTime, category));
        }

        OnPropertyChanged(nameof(InvalidImageCount));
    }

    /// <summary>通知概览页使用当前文件集合重新计算统计信息</summary>
    private void NotifyFilesChanged()
    {
        if (string.IsNullOrWhiteSpace(_currentScanPath))
        {
            return;
        }

        FilesChanged?.Invoke(this, new FolderScanResult(_currentScanPath, _currentFolderCount, _files.ToList(), _currentSkippedItemCount));
    }

    private void InvalidateCurrentLocalScanCache()
    {
        if (_isLocalScan && !string.IsNullOrWhiteSpace(_currentScanPath))
        {
            var sourceId = Path.GetPathRoot(Path.GetFullPath(_currentScanPath)) ?? _currentScanPath;
            scanCacheStore.InvalidateScan(StorageSourceKind.LocalFileSystem, sourceId, _currentScanPath);
        }
    }

    private void ConfigureLocalFileWatcher()
    {
        DisposeLocalFileWatcher();
        if (!_isLocalScan || string.IsNullOrWhiteSpace(_currentScanPath) || !Directory.Exists(_currentScanPath))
        {
            return;
        }

        _localFileWatcher = new FileSystemWatcher(_currentScanPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.Size
                | NotifyFilters.LastWrite,
            Filter = "*",
            EnableRaisingEvents = true
        };
        _localFileWatcher.Created += LocalFileWatcher_OnChanged;
        _localFileWatcher.Deleted += LocalFileWatcher_OnChanged;
        _localFileWatcher.Changed += LocalFileWatcher_OnChanged;
        _localFileWatcher.Renamed += LocalFileWatcher_OnRenamed;
        _localFileWatcher.Error += LocalFileWatcher_OnError;
        _localWatcherStartedAt = DateTimeOffset.UtcNow;
        _hasNotifiedExternalChange = false;
    }

    private void LocalFileWatcher_OnChanged(object sender, FileSystemEventArgs e) => ScheduleExternalChangeNotice();

    private void LocalFileWatcher_OnRenamed(object sender, RenamedEventArgs e) => ScheduleExternalChangeNotice();

    private void LocalFileWatcher_OnError(object sender, ErrorEventArgs e)
    {
        if (_localFileWatcher is not null)
        {
            _localFileWatcher.EnableRaisingEvents = false;
        }
        NotifyManualRefreshRequired("目录变化过于频繁，已暂停自动监听，请点击刷新更新结果");
    }

    private void ScheduleExternalChangeNotice()
    {
        if (DateTimeOffset.UtcNow - _localWatcherStartedAt < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _fileChangeRefreshCancellationTokenSource?.Cancel();
        _fileChangeRefreshCancellationTokenSource?.Dispose();
        var cancellationTokenSource = new CancellationTokenSource();
        _fileChangeRefreshCancellationTokenSource = cancellationTokenSource;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000, cancellationTokenSource.Token);
                if (!cancellationTokenSource.IsCancellationRequested)
                {
                    NotifyManualRefreshRequired("检测到外部文件变化，请点击刷新更新缓存和显示结果");
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void NotifyManualRefreshRequired(string message)
    {
        if (_hasNotifiedExternalChange)
        {
            return;
        }

        _hasNotifiedExternalChange = true;
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ExternalChangeDetected?.Invoke(this, message));
    }

    private void DisposeLocalFileWatcher()
    {
        _fileChangeRefreshCancellationTokenSource?.Cancel();
        _fileChangeRefreshCancellationTokenSource?.Dispose();
        _fileChangeRefreshCancellationTokenSource = null;
        _hasNotifiedExternalChange = false;
        if (_localFileWatcher is null)
        {
            return;
        }

        _localFileWatcher.EnableRaisingEvents = false;
        _localFileWatcher.Created -= LocalFileWatcher_OnChanged;
        _localFileWatcher.Deleted -= LocalFileWatcher_OnChanged;
        _localFileWatcher.Changed -= LocalFileWatcher_OnChanged;
        _localFileWatcher.Renamed -= LocalFileWatcher_OnRenamed;
        _localFileWatcher.Error -= LocalFileWatcher_OnError;
        _localFileWatcher.Dispose();
        _localFileWatcher = null;
    }

    private void RequestBackgroundRefresh()
    {
        if (_currentScanPath.Length > 0)
        {
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnSearchQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchQuery));
        OnPropertyChanged(nameof(SearchResultText));
        _ = ApplySearchAsync(value);
    }

    /// <summary>清除搜索关键字并恢复当前分类下的全部文件</summary>
    [RelayCommand]
    private void ClearSearch() => SearchQuery = string.Empty;

    /// <summary>防抖后在线程池中匹配名称、扩展名和位置,避免大量文件搜索阻塞 UI</summary>
    private async Task ApplySearchAsync(string query)
    {
        _searchCancellationTokenSource?.Cancel();
        _searchCancellationTokenSource?.Dispose();
        var cancellationTokenSource = new CancellationTokenSource();
        _searchCancellationTokenSource = cancellationTokenSource;

        try
        {
            await Task.Delay(200, cancellationTokenSource.Token);
            var normalizedQuery = query.Trim();
            if (string.IsNullOrEmpty(normalizedQuery))
            {
                _searchMatchedPaths = null;
                OnPropertyChanged(nameof(SearchResultText));
                BuildRows();
                return;
            }

            var fileSnapshot = _files.ToArray();
            var matches = await Task.Run(() => fileSnapshot
                .Where(file => IsSimilarMatch(file, normalizedQuery))
                .Select(file => file.FullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase), cancellationTokenSource.Token);
            cancellationTokenSource.Token.ThrowIfCancellationRequested();
            _searchMatchedPaths = matches;
            OnPropertyChanged(nameof(SearchResultText));
            BuildRows();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>优先按文件名进行分词与字符顺序匹配, 扩展名和路径仅作为补充匹配</summary>
    private static bool IsSimilarMatch(FileItem file, string query)
    {
        var normalizedTerms = query.Split([' ', '\t', '_', '-', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (normalizedTerms.Length == 0)
        {
            return false;
        }

        if (normalizedTerms.All(term => file.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return IsSubsequence(file.Name, query)
            || file.Extension.Contains(query, StringComparison.OrdinalIgnoreCase)
            || file.FullPath.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>判断查询字符是否按顺序出现在文件名中, 支持快速缩写检索</summary>
    private static bool IsSubsequence(string text, string query)
    {
        var queryIndex = 0;
        foreach (var character in text)
        {
            if (char.ToUpperInvariant(character) == char.ToUpperInvariant(query[queryIndex]) && ++queryIndex == query.Length)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>打开应用内文本或图片预览</summary>
    public async Task PreviewAsync(ExplorerRow row)
    {
        if (row.File is null)
        {
            return;
        }

        try
        {
            var preview = await previewService.CreatePreviewAsync(row.File);
            if (preview is null)
            {
                await previewService.OpenWithDefaultApplicationAsync(row.File);
                return;
            }

            var dialog = new FilePreviewDialog(new FilePreviewViewModel(preview, previewService))
            {
                Owner = System.Windows.Application.Current.MainWindow
            };
            dialog.ShowDialog();
        }
        catch (Exception exception)
        {
            HandyControl.Controls.MessageBox.Warning($"无法预览“{row.File.Name}”：{exception.Message}", "文件预览");
        }
    }

    /// <summary>显示打开文件失败提示</summary>
    private static void ShowOpenError(FileItem file, Exception exception) =>
        HandyControl.Controls.MessageBox.Warning($"无法打开“{file.Name}”：{exception.Message}", "打开文件");

    /// <summary>根据当前可见行重新计算选择摘要</summary>
    public void RefreshSelectionSummary()
    {
        _selectedPaths.Clear();
        foreach (var row in Rows.Where(row => row.File is not null && row.IsSelected))
        {
            _selectedPaths.Add(row.File!.FullPath);
        }

        UpdateSelectionSummary();
    }

    /// <summary>根据复选框的新状态设置一个分类、扩展名分组或文件行的选择状态</summary>
    /// <param name="row">复选框所属的表格行</param>
    /// <param name="selected">复选框的新选中状态</param>
    public void SetRowSelection(ExplorerRow row, bool selected)
    {
        if (row.IsCategory)
        {
            SetSelection(_files.Where(file => file.Category == row.Category), selected);
        }
        else if (row.IsExtensionGroup)
        {
            SetSelection(_files.Where(file => file.Category == row.Category && BelongsToExtensionGroup(file, row.GroupExtension)), selected);
        }
        else if (row.File is not null)
        {
            SetSelection([row.File], selected);
        }

        SynchronizeVisibleSelection();
    }

    /// <summary>根据表头复选框的新状态设置全部扫描文件的选择状态</summary>
    /// <param name="selected">是否选中所有文件</param>
    public void SetAllSelection(bool selected)
    {
        SetSelection(GetVisibleFiles(), selected);
        SynchronizeVisibleSelection();
    }

    /// <summary>重新计算选择数量、全选状态,并通知相关命令更新</summary>
    private void UpdateSelectionSummary()
    {
        SelectedFileCount = _selectedPaths.Count;
        var visibleFiles = GetVisibleFiles();
        IsAllSelected = visibleFiles.Count > 0 && visibleFiles.All(file => _selectedPaths.Contains(file.FullPath));
        CopySelectedCommand.NotifyCanExecuteChanged();
        MoveSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>供拖拽框选调用, 批量更新命中文件行的选择状态</summary>
    public void SelectRows(IEnumerable<ExplorerRow> rows, bool selected)
    {
        foreach (var row in rows.Where(row => row.File is not null))
        {
            if (selected)
            {
                _selectedPaths.Add(row.File!.FullPath);
            }
            else
            {
                _selectedPaths.Remove(row.File!.FullPath);
            }
        }

        SynchronizeVisibleSelection();
    }

    /// <summary>异步构建当前筛选和展开状态下的树行</summary>
    private void BuildRows()
    {
        var operationVersion = ++_treeOperationVersion;
        IsTreeOperationInProgress = true;
        _ = RebuildRowsAsync(operationVersion);
    }

    /// <summary>在后台构建完整树行快照</summary>
    private List<ExplorerRow> CreateRows()
    {
        var rows = new List<ExplorerRow>();
        var visibleFiles = GetVisibleFiles();
        IEnumerable<FileCategory> categories = _categoryFilter is { } filter ? [filter] : FileCategoryCatalog.DisplayOrder;
        foreach (var category in categories)
        {
            var group = visibleFiles.Where(file => file.Category == category).ToList();
            if (_categoryFilter is not null && group.Count == 0)
            {
                continue;
            }

            rows.Add(new ExplorerRow
            {
                IsCategory = true,
                IsExpanded = _expandedCategories.Contains(category),
                Category = category,
                Name = GetCategoryName(category),
                ChildCount = group.Count,
                Size = SizeFormatter.Format(group.Sum(file => file.Size)),
                IsSelected = group.Count > 0 && group.All(file => _selectedPaths.Contains(file.FullPath))
            });
            if (_expandedCategories.Contains(category))
            {
                group.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
                foreach (var extensionGroup in group.GroupBy(file => string.IsNullOrWhiteSpace(file.Extension) ? "[无扩展名]" : file.Extension.ToUpperInvariant()).OrderBy(item => item.Key))
                {
                    var extensionKey = GetExtensionKey(category, extensionGroup.Key);
                    var extensionFiles = extensionGroup.ToList();
                    rows.Add(CreateExtensionRow(category, extensionGroup.Key, extensionFiles, _expandedExtensions.Contains(extensionKey)));

                    if (_expandedExtensions.Contains(extensionKey))
                    {
                        rows.AddRange(extensionFiles.Select(CreateFileRow));
                    }
                }
            }
        }

        return rows;
    }

    /// <summary>获取同时满足筛选和搜索条件的文件</summary>
    private List<FileItem> GetVisibleFiles() => _files.Where(file =>
        (!_showInvalidImagesOnly || file.IsInvalidImage == true) &&
        (_searchMatchedPaths is null || _searchMatchedPaths.Contains(file.FullPath))).ToList();

    /// <summary>仅构建当前节点的直接可见子项</summary>
    private List<ExplorerRow> CreateDirectChildren(ExplorerRow row)
    {
        var files = GetVisibleFiles();
        if (row.IsExtensionGroup)
        {
            return files.Where(file => file.Category == row.Category && BelongsToExtensionGroup(file, row.GroupExtension))
                .OrderBy(file => file.Name)
                .Select(CreateFileRow)
                .ToList();
        }

        return files.Where(file => file.Category == row.Category)
            .GroupBy(file => string.IsNullOrWhiteSpace(file.Extension) ? "[无扩展名]" : file.Extension.ToUpperInvariant())
            .OrderBy(group => group.Key)
            .SelectMany(group =>
            {
                var groupFiles = group.OrderBy(file => file.Name).ToList();
                var isExpanded = _expandedExtensions.Contains(GetExtensionKey(row.Category, group.Key));
                var children = new List<ExplorerRow> { CreateExtensionRow(row.Category, group.Key, groupFiles, isExpanded) };
                if (isExpanded)
                {
                    children.AddRange(groupFiles.Select(CreateFileRow));
                }

                return children;
            })
            .ToList();
    }

    /// <summary>计算折叠节点时需要移除的连续子项数量</summary>
    private int GetVisibleDescendantCount(int rowIndex, ExplorerRow row)
    {
        var count = 0;
        for (var index = rowIndex + 1; index < Rows.Count; index++)
        {
            var candidate = Rows[index];
            if (row.IsCategory ? candidate.IsCategory : candidate.IsCategory || candidate.IsExtensionGroup)
            {
                break;
            }

            count++;
        }

        return count;
    }

    /// <summary>创建扩展名分组行</summary>
    private ExplorerRow CreateExtensionRow(FileCategory category, string extension, IReadOnlyCollection<FileItem> files, bool isExpanded) => new()
    {
        Category = category,
        IsExtensionGroup = true,
        IsExpanded = isExpanded,
        GroupExtension = extension,
        Name = extension,
        ChildCount = files.Count,
        Size = SizeFormatter.Format(files.Sum(file => file.Size)),
        IsSelected = files.All(file => _selectedPaths.Contains(file.FullPath))
    };

    /// <summary>创建文件行</summary>
    private ExplorerRow CreateFileRow(FileItem file) => new()
    {
        Category = file.Category,
        Name = file.Name,
        Extension = file.Extension.ToUpperInvariant(),
        Location = file.FullPath,
        Modified = file.SourceKind == StorageSourceKind.MtpDevice && file.LastModified == DateTime.MinValue
            ? "未读取"
            : file.LastModified.ToString("yyyy-MM-dd HH:mm"),
        Size = SizeFormatter.Format(file.Size),
        File = file,
        IsSelected = _selectedPaths.Contains(file.FullPath)
    };

    /// <summary>将行集合逐项应用到界面</summary>
    private void ApplyRows(IEnumerable<ExplorerRow> rows)
    {
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        UpdateSelectionSummary();
    }

    /// <summary>后台生成行快照并通过单次重置通知更新界面</summary>
    private async Task RebuildRowsAsync(int operationVersion)
    {
        try
        {
            var rows = await Task.Run(CreateRows);
            if (operationVersion != _treeOperationVersion)
            {
                return;
            }

            Rows.ReplaceWith(rows);
            UpdateSelectionSummary();
        }
        finally
        {
            if (operationVersion == _treeOperationVersion)
            {
                IsTreeOperationInProgress = false;
            }
        }
    }

    /// <summary>按界面层级约定批量更新展开状态集合</summary>
    private void ExpandToLevel(int level)
    {
        _expandedCategories.Clear();
        _expandedExtensions.Clear();

        if (level >= 2)
        {
            _expandedCategories.UnionWith(Enum.GetValues<FileCategory>());
        }

        if (level >= 3)
        {
            foreach (var extensionGroup in _files.GroupBy(file => new
            {
                file.Category,
                Extension = string.IsNullOrWhiteSpace(file.Extension) ? "[无扩展名]" : file.Extension.ToUpperInvariant()
            }))
            {
                _expandedExtensions.Add(GetExtensionKey(extensionGroup.Key.Category, extensionGroup.Key.Extension));
            }
        }
    }

    /// <summary>同步可见分组行与文件行的选择状态</summary>
    private void SynchronizeVisibleSelection()
    {
        var categoryTotals = new Dictionary<FileCategory, int>();
        var categorySelected = new Dictionary<FileCategory, int>();
        var extensionTotals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var extensionSelected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in _files)
        {
            categoryTotals[file.Category] = categoryTotals.GetValueOrDefault(file.Category) + 1;
            if (_selectedPaths.Contains(file.FullPath))
            {
                categorySelected[file.Category] = categorySelected.GetValueOrDefault(file.Category) + 1;
            }

            var extensionKey = GetExtensionKey(file.Category, GetExtensionGroup(file.Extension));
            extensionTotals[extensionKey] = extensionTotals.GetValueOrDefault(extensionKey) + 1;
            if (_selectedPaths.Contains(file.FullPath))
            {
                extensionSelected[extensionKey] = extensionSelected.GetValueOrDefault(extensionKey) + 1;
            }
        }

        foreach (var row in Rows)
        {
            if (row.IsCategory)
            {
                row.IsSelected = categoryTotals.GetValueOrDefault(row.Category) > 0
                    && categorySelected.GetValueOrDefault(row.Category) == categoryTotals[row.Category];
            }
            else if (row.IsExtensionGroup)
            {
                var extensionKey = GetExtensionKey(row.Category, row.GroupExtension);
                row.IsSelected = extensionTotals.GetValueOrDefault(extensionKey) > 0
                    && extensionSelected.GetValueOrDefault(extensionKey) == extensionTotals[extensionKey];
            }
            else if (row.File is not null)
            {
                row.IsSelected = _selectedPaths.Contains(row.File.FullPath);
            }
        }

        UpdateSelectionSummary();
    }

    /// <summary>返回分类的中文显示名称</summary>
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

    /// <summary>生成分类和扩展名组合键</summary>
    private static string GetExtensionKey(FileCategory category, string extension) => $"{category}|{extension}";

    /// <summary>将空扩展名映射为界面分组名称</summary>
    private static string GetExtensionGroup(string extension) =>
        string.IsNullOrWhiteSpace(extension) ? "[无扩展名]" : extension.ToUpperInvariant();

    /// <summary>规范化路径以便比较来源文件</summary>
    private static string NormalizeSourcePath(string path) =>
        path.Trim().Replace('/', '\\').TrimEnd('\\');

    /// <summary>判断文件是否属于界面显示的扩展名分组,兼容无扩展名文件</summary>
    private static bool BelongsToExtensionGroup(FileItem file, string groupExtension) =>
        string.IsNullOrWhiteSpace(file.Extension)
            ? string.Equals(groupExtension, "[无扩展名]", StringComparison.Ordinal)
            : string.Equals(file.Extension, groupExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>设置指定文件集合的选择状态</summary>
    private void SetSelection(IEnumerable<FileItem> files, bool? selected = null)
    {
        var shouldSelect = selected ?? files.Any(file => !_selectedPaths.Contains(file.FullPath));
        foreach (var file in files)
        {
            if (shouldSelect)
            {
                _selectedPaths.Add(file.FullPath);
            }
            else
            {
                _selectedPaths.Remove(file.FullPath);
            }
        }
    }
}
