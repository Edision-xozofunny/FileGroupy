using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileGroupy.Models;
using FileGroupy.Services;

namespace FileGroupy.ViewModels;

/// <summary>删除找回窗口的状态与快照恢复操作</summary>
public partial class DeletedFileRecoveryViewModel(
    IDeletedFileRecoveryService recoveryService,
    IPathHistoryStore pathHistoryStore) : ObservableObject
{
    /// <summary>删除快照列表, 按创建时间倒序显示</summary>
    public ObservableCollection<DeletedFileSnapshot> Snapshots { get; } = [];
    /// <summary>按搜索条件筛选后的快照列表</summary>
    public ObservableCollection<DeletedFileSnapshot> VisibleSnapshots { get; } = [];
    /// <summary>当前快照的文件明细, 支持多选恢复</summary>
    public ObservableCollection<RecoveryFileRow> Files { get; } = [];
    /// <summary>快照创建历史</summary>
    public ObservableCollection<RecoverySnapshotCreationHistory> CreationHistory { get; } = [];
    /// <summary>快照恢复历史</summary>
    public ObservableCollection<RecoverySnapshotRestoreHistory> RestoreHistory { get; } = [];

    /// <summary>当前选中的删除快照</summary>
    [ObservableProperty] private DeletedFileSnapshot? _selectedSnapshot;
    /// <summary>按时间, 快照编号, 文件名或原始路径搜索</summary>
    [ObservableProperty] private string _searchQuery = string.Empty;
    /// <summary>当前打开详情抽屉的文件</summary>
    [ObservableProperty] private DeletedFileSnapshotItem? _selectedFileDetail;
    /// <summary>是否打开文件详情抽屉</summary>
    [ObservableProperty] private bool _isFileDetailsOpen;
    /// <summary>当前选中的恢复文件明细</summary>
    public ObservableCollection<DeletedFileSnapshotItem> SelectedFiles { get; } = [];
    /// <summary>是否正在读取、恢复或清除快照</summary>
    [ObservableProperty] private bool _isBusy;
    /// <summary>窗口内的操作状态提示</summary>
    [ObservableProperty] private string _statusText = "正在读取删除找回记录...";
    /// <summary>是否恢复到每个文件的原始路径</summary>
    [ObservableProperty] private bool _restoreToOriginalPath = true;
    /// <summary>是否恢复到用户指定目录</summary>
    [ObservableProperty] private bool _restoreToSpecifiedPath;
    /// <summary>用户选择的统一恢复目录</summary>
    [ObservableProperty] private string _restoreDestinationPath = string.Empty;

    /// <summary>文件恢复完成后通知外层刷新当前扫描来源</summary>
    public event EventHandler? RecoveryCompleted;
    /// <summary>请求视图显示主题化提示</summary>
    public event EventHandler<string>? AlertRequested;

    [RelayCommand]
    private void ChooseRestoreDestination()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择恢复目标文件夹",
            InitialDirectory = pathHistoryStore.GetLastPath(PathHistoryKind.RecoveryDestination)
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        RestoreDestinationPath = dialog.FolderName;
        RestoreToSpecifiedPath = true;
        pathHistoryStore.SaveLastPath(PathHistoryKind.RecoveryDestination, dialog.FolderName);
        NotifyRestoreCommands();
    }

    partial void OnRestoreToOriginalPathChanged(bool value)
    {
        if (value) RestoreToSpecifiedPath = false;
        NotifyRestoreCommands();
    }

    partial void OnRestoreToSpecifiedPathChanged(bool value)
    {
        if (value) RestoreToOriginalPath = false;
        NotifyRestoreCommands();
    }
    partial void OnRestoreDestinationPathChanged(string value) => NotifyRestoreCommands();

    /// <summary>异步加载全部删除快照</summary>
    public async Task LoadAsync()
    {
        RestoreDestinationPath = pathHistoryStore.GetLastPath(PathHistoryKind.RecoveryDestination) ?? string.Empty;
        IsBusy = true;
        try
        {
            var snapshots = await recoveryService.GetSnapshotsAsync();
            Snapshots.Clear();
            foreach (var snapshot in snapshots)
            {
                Snapshots.Add(snapshot);
            }

            ApplySnapshotFilter();
            SelectedSnapshot = Snapshots.FirstOrDefault();
            StatusText = snapshots.Count == 0 ? "没有可恢复的本地删除文件" : $"共 {snapshots.Count:N0} 个删除快照";
        }
        catch (Exception exception)
        {
            StatusText = $"无法读取删除快照：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>加载可供用户查询的快照创建和恢复历史</summary>
    public async Task LoadHistoryAsync()
    {
        var creations = await recoveryService.GetSnapshotCreationHistoryAsync();
        var restores = await recoveryService.GetSnapshotRestoreHistoryAsync();
        CreationHistory.Clear();
        RestoreHistory.Clear();
        foreach (var creation in creations)
        {
            CreationHistory.Add(creation);
        }

        foreach (var restore in restores)
        {
            RestoreHistory.Add(restore);
        }
    }

    partial void OnSearchQueryChanged(string value) => ApplySnapshotFilter();

    /// <summary>显式搜索按钮使用的命令, 与输入框实时筛选保持一致</summary>
    [RelayCommand]
    private void Search() => ApplySnapshotFilter();

    private void ApplySnapshotFilter()
    {
        var query = SearchQuery.Trim();
        var selectedId = SelectedSnapshot?.SnapshotId;
        VisibleSnapshots.Clear();
        foreach (var snapshot in Snapshots.Where(snapshot => Matches(snapshot, query)))
        {
            VisibleSnapshots.Add(snapshot);
        }

        if (selectedId is not null)
        {
            SelectedSnapshot = VisibleSnapshots.FirstOrDefault(snapshot => snapshot.SnapshotId == selectedId)
                ?? VisibleSnapshots.FirstOrDefault();
        }
        else if (VisibleSnapshots.Count > 0)
        {
            SelectedSnapshot = VisibleSnapshots[0];
        }
    }

    private static bool Matches(DeletedFileSnapshot snapshot, string query)
    {
        if (query.Length == 0)
        {
            return true;
        }

        return snapshot.SnapshotId.Contains(query, StringComparison.OrdinalIgnoreCase)
            || snapshot.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss").Contains(query, StringComparison.OrdinalIgnoreCase)
            || snapshot.Files.Any(file => file.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || file.OriginalPath.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>打开指定文件的完整路径详情</summary>
    public void OpenFileDetails(DeletedFileSnapshotItem? file)
    {
        SelectedFileDetail = file;
        IsFileDetailsOpen = file is not null;
    }

    [RelayCommand]
    private void CloseFileDetails() => IsFileDetailsOpen = false;

    partial void OnSelectedSnapshotChanged(DeletedFileSnapshot? value)
    {
        Files.Clear();
        SelectedFiles.Clear();
        if (value is not null)
        {
            foreach (var item in value.Files.Where(item => !item.IsRestored))
            {
                Files.Add(new RecoveryFileRow(item));
            }
        }

        RestoreSelectedCommand.NotifyCanExecuteChanged();
        RestoreAllCommand.NotifyCanExecuteChanged();
        PermanentlyDeleteSnapshotCommand.NotifyCanExecuteChanged();
    }

    /// <summary>独立更新文件复选框状态, 不改变 DataGrid 行选择</summary>
    public void SetFileSelection(RecoveryFileRow file, bool isSelected)
    {
        SelectedFiles.Clear();
        file.IsSelected = isSelected;
        foreach (var selectedFile in Files.Where(file => file.IsSelected))
        {
            SelectedFiles.Add(selectedFile.Item);
        }

        RestoreSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>全选或取消选择当前快照中的文件</summary>
    public void SetAllFileSelection(bool isSelected)
    {
        foreach (var file in Files)
        {
            file.IsSelected = isSelected;
        }

        SelectedFiles.Clear();
        if (isSelected)
        {
            foreach (var file in Files)
            {
                SelectedFiles.Add(file.Item);
            }
        }

        RestoreSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>恢复当前快照中的已选文件</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreSelected))]
    private async Task RestoreSelectedAsync()
    {
        if (SelectedSnapshot is null)
        {
            return;
        }

        await RestoreAsync(SelectedFiles.Select(item => item.ItemId).ToArray());
    }

    /// <summary>恢复当前快照中的全部可恢复文件</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreAll))]
    private async Task RestoreAllAsync()
    {
        if (SelectedSnapshot is null)
        {
            return;
        }

        await RestoreAsync(null);
    }

    /// <summary>永久清除当前快照, 调用方需先完成用户确认</summary>
    [RelayCommand(CanExecute = nameof(CanPermanentlyDeleteSnapshot))]
    private async Task PermanentlyDeleteSnapshotAsync()
    {
        if (SelectedSnapshot is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await recoveryService.PermanentlyDeleteSnapshotAsync(SelectedSnapshot.SnapshotId);
            StatusText = "删除快照已永久清除，文件无法再找回";
            await LoadAsync();
        }
        catch (Exception exception)
        {
            StatusText = $"永久清除失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreAsync(IReadOnlyCollection<string>? itemIds)
    {
        if (SelectedSnapshot is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var destination = RestoreToOriginalPath ? null : RestoreDestinationPath;
            if (!RestoreToOriginalPath && string.IsNullOrWhiteSpace(destination))
            {
                AlertRequested?.Invoke(this, "请先选择恢复目标文件夹");
                return;
            }

            var result = await recoveryService.RestoreAsync(SelectedSnapshot.SnapshotId, itemIds, destination);
            if (destination is { } destinationPath && Directory.Exists(destinationPath))
            {
                pathHistoryStore.SaveLastPath(PathHistoryKind.RecoveryDestination, destinationPath);
            }
            StatusText = $"恢复完成：成功 {result.Succeeded:N0}，失败 {result.Failures.Count:N0}";
            if (result.Failures.Count > 0)
            {
                AlertRequested?.Invoke(this, $"部分文件恢复失败：{result.Failures[0].Reason}");
            }
            await LoadAsync();
            if (result.Succeeded > 0)
            {
                RecoveryCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception)
        {
            StatusText = $"恢复失败：{exception.Message}";
            AlertRequested?.Invoke(this, StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool HasValidRestoreTarget() => RestoreToOriginalPath || !string.IsNullOrWhiteSpace(RestoreDestinationPath);
    private bool CanRestoreSelected() => !IsBusy && HasValidRestoreTarget() && SelectedSnapshot is not null && SelectedFiles.Count > 0;
    private bool CanRestoreAll() => !IsBusy && HasValidRestoreTarget() && SelectedSnapshot is not null && Files.Count > 0;
    private bool CanPermanentlyDeleteSnapshot() => !IsBusy && SelectedSnapshot is not null;

    partial void OnIsBusyChanged(bool value)
    {
        RestoreSelectedCommand.NotifyCanExecuteChanged();
        RestoreAllCommand.NotifyCanExecuteChanged();
        PermanentlyDeleteSnapshotCommand.NotifyCanExecuteChanged();
    }

    private void NotifyRestoreCommands()
    {
        RestoreSelectedCommand.NotifyCanExecuteChanged();
        RestoreAllCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>恢复明细行的独立勾选状态, 与 DataGrid 行高亮选择分离</summary>
public sealed partial class RecoveryFileRow(DeletedFileSnapshotItem item) : ObservableObject
{
    public DeletedFileSnapshotItem Item { get; } = item;

    [ObservableProperty]
    private bool _isSelected;
}