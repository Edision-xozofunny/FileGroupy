using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileGroupy.Models;
using FileGroupy.Services;
using FileGroupy.Views;

namespace FileGroupy.ViewModels;

/// <summary>文件浏览页视图模型，提供分类树、选择状态和批量复制/移动入口</summary>
/// <param name="transferService">执行批量文件操作的服务</param>
public partial class FileExplorerViewModel(
    IFileTransferService transferService,
    IMtpDeviceService mtpDeviceService,
    IFilePreviewService previewService) : ObservableObject
{
    /// <summary>最近一次扫描的全部文件，用于按分类重新构建显示行</summary>
    private readonly List<FileItem> _files = [];
    /// <summary>当前被展开的分类集合，重建行时用于保留用户的展开状态</summary>
    private readonly HashSet<FileCategory> _expandedCategories = Enum.GetValues<FileCategory>().ToHashSet();
    /// <summary>当前已展开的“分类|扩展名”节点集合</summary>
    private readonly HashSet<string> _expandedExtensions = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>跨折叠和视图重建保留的已选文件完整路径集合</summary>
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>可选的单一分类筛选条件；为空时显示所有分类</summary>
    private FileCategory? _categoryFilter;
    /// <summary>当前异步搜索命中的文件路径；为空表示未应用搜索筛选</summary>
    private HashSet<string>? _searchMatchedPaths;
    /// <summary>用于取消被新关键字取代的后台搜索任务</summary>
    private CancellationTokenSource? _searchCancellationTokenSource;
    /// <summary>树批量操作版本，用于丢弃较早异步操作的过期结果</summary>
    private int _treeOperationVersion;
    /// <summary>最近一次扫描结果的展示路径，用于操作后回写概览统计。</summary>
    private string _currentScanPath = string.Empty;
    /// <summary>最近一次扫描结果中的目录数量，用于维持概览页统计结构一致。</summary>
    private int _currentFolderCount;
    /// <summary>最近一次扫描结果中的跳过项数量，用于维持概览页文案。</summary>
    private int _currentSkippedItemCount;
    /// <summary>当前结果是否来自本地目录扫描，用于判定复制或移动后是否可补充新增节点。</summary>
    private bool _isLocalScan;

    /// <summary>绑定到表格的根行和文件子行集合</summary>
    public ObservableCollection<ExplorerRow> Rows { get; } = [];
    /// <summary>文件树可选的最大展开层级</summary>
    public IReadOnlyList<int> ExpansionLevels { get; } = [1, 2, 3, 4, 5];

    /// <summary>由工具生成的页面标题公开绑定属性</summary>
    [ObservableProperty] private string _title = "全部文件";
    /// <summary>由工具生成的页面副标题公开绑定属性</summary>
    [ObservableProperty] private string _subtitle = "选择文件夹后，以文件类型为根节点浏览内容";
    /// <summary>由工具生成的表头全选框状态公开绑定属性</summary>
    [ObservableProperty] private bool _isAllSelected;
    /// <summary>由工具生成的当前已选文件数量公开绑定属性</summary>
    [ObservableProperty] private int _selectedFileCount;
    /// <summary>由工具生成的批量展开目标层级公开绑定属性；默认显示分类和扩展名两层</summary>
    [ObservableProperty] private int _selectedExpansionLevel = 2;
    /// <summary>由工具生成的树批量操作进行中状态公开绑定属性</summary>
    [ObservableProperty] private bool _isTreeOperationInProgress;
    /// <summary>由工具生成的文件模糊检索关键字公开绑定属性</summary>
    [ObservableProperty] private string _searchQuery = string.Empty;

    /// <summary>文件集合发生增删改后触发，供概览页和分类卡片同步刷新。</summary>
    public event EventHandler<FolderScanResult>? FilesChanged;

    /// <summary>接收新扫描结果，清除筛选并显示全部分类</summary>
    /// <param name="result">最新的目录扫描结果</param>
        /// <summary>仅显示指定分类下的文件</summary>
        /// <param name="category">需要筛选的文件分类</param>
        /// <summary>切换一个分类根节点的展开或折叠状态</summary>
        /// <param name="row">用户点击的表格行</param>
        /// <summary>切换文件行，或整个分类下所有已展开文件行的选择状态</summary>
        /// <param name="row">用户操作的表格行</param>
        /// <summary>按表头复选框状态全选或取消选择所有可见文件行</summary>
        /// <summary>打开复制对话框，处理当前选中的文件</summary>
        /// <summary>打开移动对话框，处理当前选中的文件</summary>
        /// <summary>决定批量操作命令是否可用</summary>
        /// <returns>至少选择一个文件时返回 <see langword="true"/></returns>
        /// <summary>创建并显示复制或移动参数对话框</summary>
        /// <param name="moveFiles">是否以移动模式打开</param>
    public void Load(FolderScanResult result)
    {
        _files.Clear();
        _files.AddRange(result.Files);
        _selectedPaths.Clear();
        _searchMatchedPaths = null;
        SearchQuery = string.Empty;
        _categoryFilter = null;
        _currentScanPath = result.Path;
        _currentFolderCount = result.FolderCount;
        _currentSkippedItemCount = result.SkippedItemCount;
        _isLocalScan = result.Files.Count == 0 || result.Files.All(file => file.SourceKind == StorageSourceKind.LocalFileSystem);
        Title = "全部文件";
        Subtitle = result.SkippedItemCount == 0
            ? $"{result.Path}  |  {result.Files.Count:N0} 个文件"
            : $"{result.Path}  |  {result.Files.Count:N0} 个文件  |  已跳过 {result.SkippedItemCount:N0} 个无法读取项";
        BuildRows();
        ExpandToLevelCommand.NotifyCanExecuteChanged();
        CollapseAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>清空上一次扫描的文件、选择和树形行，使浏览页面恢复初始状态</summary>
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
        _currentScanPath = string.Empty;
        _currentFolderCount = 0;
        _currentSkippedItemCount = 0;
        _isLocalScan = false;
        Rows.Clear();
        Title = "全部文件";
        Subtitle = "选择文件夹后，以文件类型为根节点浏览内容";
        UpdateSelectionSummary();
        ExpandToLevelCommand.NotifyCanExecuteChanged();
        CollapseAllCommand.NotifyCanExecuteChanged();
    }

    public void ShowCategory(FileCategory category)
    {
        _categoryFilter = category;
        Title = $"{GetCategoryName(category)}文件";
        Subtitle = "分类卡片筛选结果";
        BuildRows();
    }

    /// <summary>清除分类卡片带来的筛选条件，恢复显示最近扫描结果中的全部文件分类</summary>
    public void ShowAll()
    {
        _categoryFilter = null;
        Title = "全部文件";
        Subtitle = _files.Count == 0 ? "选择文件夹后，以文件类型为根节点浏览内容" : $"共 {_files.Count:N0} 个文件";
        BuildRows();
    }

    [RelayCommand]
    private void ToggleGroup(ExplorerRow row)
    {
        if (IsTreeOperationInProgress)
        {
            return;
        }

        if (row.IsCategory)
        {
            if (!_expandedCategories.Add(row.Category))
            {
                _expandedCategories.Remove(row.Category);
            }
        }
        else if (row.IsExtensionGroup)
        {
            var key = GetExtensionKey(row.Category, row.GroupExtension);
            if (!_expandedExtensions.Add(key))
            {
                _expandedExtensions.Remove(key);
            }
        }
        else
        {
            return;
        }

        BuildRows();
    }

    /// <summary>异步展开至选定深度；本树只有分类、扩展名、文件三层，超过三级等同于完全展开</summary>
    [RelayCommand(CanExecute = nameof(CanChangeTreeExpansion))]
    private async Task ExpandToLevelAsync()
    {
        var operationVersion = ++_treeOperationVersion;
        IsTreeOperationInProgress = true;
        ExpandToLevel(SelectedExpansionLevel);
        await RebuildRowsAsync(operationVersion);
    }

    /// <summary>异步折叠所有分类节点，保留扫描和选择状态</summary>
    [RelayCommand(CanExecute = nameof(CanChangeTreeExpansion))]
    private async Task CollapseAllAsync()
    {
        var operationVersion = ++_treeOperationVersion;
        IsTreeOperationInProgress = true;
        _expandedCategories.Clear();
        _expandedExtensions.Clear();
        await RebuildRowsAsync(operationVersion);
    }

    private bool CanChangeTreeExpansion() => !IsTreeOperationInProgress && _files.Count > 0;

    partial void OnIsTreeOperationInProgressChanged(bool value)
    {
        ExpandToLevelCommand.NotifyCanExecuteChanged();
        CollapseAllCommand.NotifyCanExecuteChanged();
    }

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

    [RelayCommand]
    private void ToggleSelectAll()
    {
        SetSelection(_files, !IsAllSelected);
        SynchronizeVisibleSelection();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopySelectedAsync() => await ShowTransferDialogAsync(false);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task MoveSelectedAsync() => await ShowTransferDialogAsync(true);

    /// <summary>打开删除对话框，执行批量删除并同步刷新树节点和概览统计。</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteSelectedAsync() => await ShowDeleteDialogAsync();

    private bool HasSelection() => SelectedFileCount > 0;

    /// <summary>仅允许对实际文件行执行 Windows Shell 打开操作</summary>
    private static bool CanOpenFile(ExplorerRow? row) => row?.File is not null;

    /// <summary>使用 Windows 默认关联打开文件；未关联时显示系统“打开方式”</summary>
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

    /// <summary>显示 Windows 内置“打开方式”选择器</summary>
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

    /// <summary>在 Windows 文件资源管理器中选中本地源文件。</summary>
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

    private static bool CanOpenLocalFile(ExplorerRow? row) =>
        row?.File?.SourceKind == StorageSourceKind.LocalFileSystem;

    /// <summary>确保右键行至少被加入当前选择集合，便于单文件直接执行复制、移动或删除。</summary>
    /// <param name="row">右键命中的表格行。</param>
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

    /// <summary>为图片文件构建悬停预览数据；无法解码时返回损坏提示。</summary>
    /// <param name="row">当前鼠标所在表格行。</param>
    /// <param name="cancellationToken">用于取消预览加载的标记。</param>
    /// <returns>图片悬停预览数据；非图片行返回 <see langword="null"/>。</returns>
    public async Task<ImageHoverPreview?> CreateImageHoverPreviewAsync(ExplorerRow row, CancellationToken cancellationToken = default)
    {
        if (row.File is null || row.File.Category != FileCategory.Images)
        {
            return null;
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

    /// <summary>打开复制或移动对话框，并在完成后刷新树数据、缓存与提示信息。</summary>
    /// <param name="moveFiles">是否以移动模式执行。</param>
    /// <returns>异步任务。</returns>
    private async Task ShowTransferDialogAsync(bool moveFiles)
    {
        var selectedFiles = _files.Where(file => _selectedPaths.Contains(file.FullPath)).ToList();
        var dialogViewModel = new FileTransferDialogViewModel(transferService, mtpDeviceService, selectedFiles, moveFiles);
        var dialog = new FileTransferDialog(dialogViewModel)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        dialog.ShowDialog();
        if (dialogViewModel.LastResult is { } result)
        {
            ApplyTransferResult(moveFiles, result);
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
        }

        await Task.CompletedTask;
    }

    /// <summary>打开删除对话框并在完成后同步树节点、缓存和结果提示。</summary>
    /// <returns>异步任务。</returns>
    private async Task ShowDeleteDialogAsync()
    {
        var selectedFiles = _files.Where(file => _selectedPaths.Contains(file.FullPath)).ToList();
        var dialogViewModel = new FileDeleteDialogViewModel(transferService, selectedFiles);
        var dialog = new FileDeleteDialog(dialogViewModel)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        dialog.ShowDialog();
        if (dialogViewModel.LastResult is { } result)
        {
            RemoveFilesBySourcePaths(result.SuccessfulSourcePaths);
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
        }

        await Task.CompletedTask;
    }

    /// <summary>根据复制或移动结果更新树节点，并广播统计刷新事件。</summary>
    /// <param name="moveFiles">是否为移动模式。</param>
    /// <param name="result">操作结果。</param>
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

    /// <summary>显示复制、移动或删除的结果提示，便于用户确认任务是否完成。</summary>
    /// <param name="operationName">操作名称。</param>
    /// <param name="succeeded">成功数量。</param>
    /// <param name="skipped">跳过数量。</param>
    /// <param name="failed">失败数量。</param>
    private static void ShowOperationResultMessage(string operationName, int succeeded, int skipped, int failed)
    {
        var message = $"{operationName}完成：成功 {succeeded:N0}，跳过 {skipped:N0}，失败 {failed:N0}";
        System.Windows.MessageBox.Show(
            System.Windows.Application.Current.MainWindow,
            message,
            $"{operationName}结果",
            System.Windows.MessageBoxButton.OK,
            failed == 0 ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
    }

    /// <summary>按成功源路径集合删除当前树中的文件项，并同步选择与搜索状态。</summary>
    /// <param name="sourcePaths">需要移除的源路径集合。</param>
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
    }

    /// <summary>将目标落在当前本地扫描范围内的新文件补充到树数据中。</summary>
    /// <param name="successfulTransfers">成功传输的源目标映射集合。</param>
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
    }

    /// <summary>通知概览页使用当前树数据重新计算卡片、统计和筛选结果。</summary>
    private void NotifyFilesChanged()
    {
        if (string.IsNullOrWhiteSpace(_currentScanPath))
        {
            return;
        }

        FilesChanged?.Invoke(this, new FolderScanResult(_currentScanPath, _currentFolderCount, _files.ToList(), _currentSkippedItemCount));
    }

    partial void OnSearchQueryChanged(string value) => _ = ApplySearchAsync(value);

    /// <summary>防抖后在线程池中匹配名称、扩展名和位置，避免大量文件搜索阻塞 UI</summary>
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
            BuildRows();
        }
        catch (OperationCanceledException)
        {
            // 新关键字会取消旧任务，旧结果不能覆盖当前列表。
        }
    }

    /// <summary>先执行忽略大小写的包含匹配，再支持关键字字符按顺序出现的模糊匹配</summary>
    private static bool IsSimilarMatch(FileItem file, string query)
    {
        var searchableText = $"{file.Name} {file.Extension} {file.FullPath}";
        if (searchableText.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var queryIndex = 0;
        foreach (var character in searchableText)
        {
            if (char.ToUpperInvariant(character) == char.ToUpperInvariant(query[queryIndex]))
            {
                queryIndex++;
                if (queryIndex == query.Length)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>保留应用内轻量文本和图片预览入口，供其它视图按需使用</summary>
    /// <param name="row">被双击的文件表格行</param>
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
            var owner = System.Windows.Application.Current.MainWindow;
            System.Windows.MessageBox.Show(owner, $"无法预览“{row.File.Name}”：{exception.Message}", "文件预览", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>显示 Windows Shell 打开失败的统一错误提示</summary>
    private static void ShowOpenError(FileItem file, Exception exception) =>
        System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow, $"无法打开“{file.Name}”：{exception.Message}", "打开文件", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);

    /// <summary>在视图直接修改复选框绑定后同步文件路径选择集合和命令状态</summary>
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
        SetSelection(_files, selected);
        SynchronizeVisibleSelection();
    }

    /// <summary>重新计算选择数量、全选状态，并通知相关命令更新</summary>
    private void UpdateSelectionSummary()
    {
        SelectedFileCount = _selectedPaths.Count;
        IsAllSelected = _files.Count > 0 && _files.All(file => _selectedPaths.Contains(file.FullPath));
        CopySelectedCommand.NotifyCanExecuteChanged();
        MoveSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>供视图的拖拽框选行为调用，批量改变矩形范围内文件行的选择状态</summary>
    /// <param name="rows">已命中选择矩形的表格行</param>
    /// <param name="selected">要写入的选中状态</param>
        /// <summary>按照当前筛选和展开状态重新构建分类根行及文件子行</summary>
        /// <summary>返回指定分类的中文显示名称</summary>
        /// <param name="category">待转换的文件分类</param>
        /// <returns>用于文件浏览页的中文名称</returns>
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

    /// <summary>构建当前树行数据；万级数据时改为异步分批写入以降低主线程卡顿。</summary>
    private void BuildRows()
    {
        var operationVersion = ++_treeOperationVersion;
        if (_files.Count >= 10_000)
        {
            IsTreeOperationInProgress = true;
            _ = RebuildRowsAsync(operationVersion);
            return;
        }

        IsTreeOperationInProgress = false;
        ApplyRows(CreateRows());
    }

    /// <summary>在后台构建不可变的行快照，避免展开大量文件时占用 UI 线程</summary>
    private List<ExplorerRow> CreateRows()
    {
        var rows = new List<ExplorerRow>();
        var visibleFiles = _searchMatchedPaths is null
            ? _files
            : _files.Where(file => _searchMatchedPaths.Contains(file.FullPath)).ToList();
        var categories = _categoryFilter is { } filter ? [filter] : Enum.GetValues<FileCategory>();
        foreach (var category in categories)
        {
            var group = visibleFiles.Where(file => file.Category == category).OrderBy(file => file.Name).ToList();
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
                foreach (var extensionGroup in group.GroupBy(file => string.IsNullOrWhiteSpace(file.Extension) ? "[无扩展名]" : file.Extension.ToUpperInvariant()).OrderBy(item => item.Key))
                {
                    var extensionKey = GetExtensionKey(category, extensionGroup.Key);
                    var extensionFiles = extensionGroup.ToList();
                    rows.Add(new ExplorerRow
                    {
                        Category = category,
                        IsExtensionGroup = true,
                        IsExpanded = _expandedExtensions.Contains(extensionKey),
                        GroupExtension = extensionGroup.Key,
                        Name = extensionGroup.Key,
                        ChildCount = extensionFiles.Count,
                        Size = SizeFormatter.Format(extensionFiles.Sum(file => file.Size)),
                        IsSelected = extensionFiles.All(file => _selectedPaths.Contains(file.FullPath))
                    });

                    if (_expandedExtensions.Contains(extensionKey))
                    {
                        foreach (var file in extensionFiles)
                        {
                            rows.Add(new ExplorerRow
                            {
                                Category = category,
                                Name = file.Name,
                                Extension = file.Extension.ToUpperInvariant(),
                                Location = file.FullPath,
                                Modified = file.SourceKind == StorageSourceKind.MtpDevice && file.LastModified == DateTime.MinValue
                                    ? "未读取"
                                    : file.LastModified.ToString("yyyy-MM-dd HH:mm"),
                                Size = SizeFormatter.Format(file.Size),
                                File = file,
                                IsSelected = _selectedPaths.Contains(file.FullPath)
                            });
                        }
                    }
                }
            }
        }

        return rows;
    }

    /// <summary>应用行快照并一次更新选择汇总</summary>
    private void ApplyRows(IEnumerable<ExplorerRow> rows)
    {
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        UpdateSelectionSummary();
    }

    /// <summary>后台生成行，再以小批次写入 UI 集合，避免大型展开操作冻结窗口</summary>
    private async Task RebuildRowsAsync(int operationVersion)
    {
        try
        {
            var rows = await Task.Run(CreateRows);
            if (operationVersion != _treeOperationVersion)
            {
                return;
            }

            Rows.Clear();
            foreach (var rowBatch in rows.Chunk(250))
            {
                if (operationVersion != _treeOperationVersion)
                {
                    return;
                }

                foreach (var row in rowBatch)
                {
                    Rows.Add(row);
                }

                await Task.Yield();
            }

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

    /// <summary>
    /// 仅刷新当前已生成表格行的选择状态批量选择不再清空并重建整个树，
    /// 避免大文件夹中触发数千次控件创建与复选框事件
    /// </summary>
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

    /// <summary>生成可唯一定位一个扩展名分组节点的状态键</summary>
    private static string GetExtensionKey(FileCategory category, string extension) => $"{category}|{extension}";

    private static string GetExtensionGroup(string extension) =>
        string.IsNullOrWhiteSpace(extension) ? "[无扩展名]" : extension.ToUpperInvariant();

    private static string NormalizeSourcePath(string path) =>
        path.Trim().Replace('/', '\\').TrimEnd('\\');

    /// <summary>判断文件是否属于界面显示的扩展名分组，兼容无扩展名文件</summary>
    private static bool BelongsToExtensionGroup(FileItem file, string groupExtension) =>
        string.IsNullOrWhiteSpace(file.Extension)
            ? string.Equals(groupExtension, "[无扩展名]", StringComparison.Ordinal)
            : string.Equals(file.Extension, groupExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>按当前状态反选或明确设置指定文件集合的选择状态</summary>
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