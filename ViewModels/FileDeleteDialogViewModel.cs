using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileGroupy.Models;
using FileGroupy.Services;
using FileGroupy.Views;

namespace FileGroupy.ViewModels;

/// <summary>批量删除对话框视图模型,统一管理开始、取消、进度与失败明细</summary>
public partial class FileDeleteDialogViewModel : ObservableObject
{
    /// <summary>执行本地与 MTP 删除的服务</summary>
    private readonly IFileTransferService _transferService;
    /// <summary>评估本地文件写入恢复库所需磁盘空间的服务</summary>
    private readonly IDeletedFileRecoveryService _recoveryService;
    /// <summary>打开对话框时冻结的待删除文件集合</summary>
    private readonly IReadOnlyCollection<FileItem> _sourceFiles;
    /// <summary>当前删除任务的取消源</summary>
    private CancellationTokenSource? _cancellationTokenSource;
    /// <summary>取消完成后是否自动关闭对话框</summary>
    private bool _closeAfterCancellation;

    /// <summary>创建批量删除对话框状态</summary>
    public FileDeleteDialogViewModel(
        IFileTransferService transferService,
        IDeletedFileRecoveryService recoveryService,
        IReadOnlyCollection<FileItem> sourceFiles)
    {
        _transferService = transferService;
        _recoveryService = recoveryService;
        _sourceFiles = sourceFiles;
        SelectionSummary = $"已选择 {sourceFiles.Count:N0} 个文件，共 {SizeFormatter.Format(sourceFiles.Sum(file => file.Size))}";
        SourceHint = sourceFiles.All(file => file.SourceKind == StorageSourceKind.LocalFileSystem)
            ? "本地文件会转入 FileGroupy 删除找回库，可在删除找回中恢复"
            : sourceFiles.All(file => file.SourceKind == StorageSourceKind.MtpDevice)
                ? "MTP/PTP 设备文件会永久删除，设备通常不支持应用内找回"
                : "本地文件可从删除找回恢复；MTP/PTP 设备文件会永久删除";
        var localFiles = sourceFiles.Where(file => file.SourceKind == StorageSourceKind.LocalFileSystem).ToArray();
        if (localFiles.Length > 0)
        {
            CapacityAssessment = _recoveryService.AssessCapacity(localFiles);
            RecoveryCapacityText = $"删除找回库需要 {SizeFormatter.Format(CapacityAssessment.RequiredBytes)}，可用 {SizeFormatter.Format(CapacityAssessment.AvailableBytes)}";
        }
    }

    /// <summary>已选文件的数量和大小摘要</summary>
    public string SelectionSummary { get; }
    /// <summary>删除来源限制提示,本地删除时为空</summary>
    public string SourceHint { get; }
    /// <summary>删除成功的源路径集合,供外部页面同步移除节点</summary>
    public IReadOnlyList<string> DeletedSourcePaths { get; private set; } = [];
    /// <summary>最近一次删除结果, 供调用页同步文件树</summary>
    public FileTransferResult? LastResult { get; private set; }
    /// <summary>请求对话框在取消完成后关闭.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>是否正在执行删除</summary>
    [ObservableProperty] private bool _isDeleting;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _statusText = "准备就绪，点击“开始删除”执行";
    /// <summary>用户是否已确认删除源文件</summary>
    [ObservableProperty] private bool _hasConfirmedSourceDeletion;
    /// <summary>是否显式要求永久删除本地文件而不创建恢复快照</summary>
    [ObservableProperty] private bool _permanentlyDeleteLocalFiles;
    /// <summary>本次本地文件软删除的恢复库容量评估</summary>
    public RecoveryCapacityAssessment? CapacityAssessment { get; }
    /// <summary>恢复库空间提示文本</summary>
    public string RecoveryCapacityText { get; } = string.Empty;
    /// <summary>当前软删除是否因空间不足而不可用</summary>
    public bool IsRecoverySpaceInsufficient => CapacityAssessment is { HasEnoughSpace: false };
    /// <summary>需要显示恢复库空间预估的场景</summary>
    public bool HasRecoveryCapacityAssessment => CapacityAssessment is not null;
    /// <summary>本次删除中的失败记录集合</summary>
    public ObservableCollection<FileTransferFailure> Failures { get; } = [];
    /// <summary>是否存在可查看的失败项</summary>
    public bool HasFailures => Failures.Count > 0;

    /// <summary>开始执行批量删除</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartDeleteAsync()
    {
        IsDeleting = true;
        _cancellationTokenSource = new CancellationTokenSource();
        var progress = new Progress<FileTransferProgress>(value =>
        {
            ProgressPercent = value.TotalFiles == 0 ? 0 : (double)value.CompletedFiles / value.TotalFiles * 100;
            StatusText = $"正在删除 {value.CompletedFiles:N0}/{value.TotalFiles:N0} 个文件，{SizeFormatter.Format(value.TransferredBytes)} / {SizeFormatter.Format(value.TotalBytes)}";
        });

        try
        {
            Failures.Clear();
            OnPropertyChanged(nameof(HasFailures));
            ShowFailuresCommand.NotifyCanExecuteChanged();
            var result = await _transferService.DeleteAsync(_sourceFiles, PermanentlyDeleteLocalFiles, progress, _cancellationTokenSource.Token);
            LastResult = result;
            DeletedSourcePaths = result.SuccessfulSourcePaths;
            foreach (var failure in result.Failures)
            {
                Failures.Add(failure);
            }

            OnPropertyChanged(nameof(HasFailures));
            ShowFailuresCommand.NotifyCanExecuteChanged();
            StatusText = $"完成：成功 {result.Succeeded:N0}，失败 {result.Failures.Count:N0}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "操作已取消";
        }
        catch (Exception exception)
        {
            StatusText = $"删除失败：{exception.Message}";
        }
        finally
        {
            IsDeleting = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            if (_closeAfterCancellation)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>取消正在执行的删除任务</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (_cancellationTokenSource is null)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _closeAfterCancellation = true;
        _cancellationTokenSource.Cancel();
    }

    /// <summary>展示失败明细窗口</summary>
    [RelayCommand(CanExecute = nameof(HasFailures))]
    private void ShowFailures()
    {
        var dialog = new FileTransferFailuresDialog(Failures)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        dialog.ShowDialog();
    }

    private bool CanStart() => !IsDeleting
        && HasConfirmedSourceDeletion
        && (!IsRecoverySpaceInsufficient || PermanentlyDeleteLocalFiles);

    /// <summary>删除状态变化后刷新开始命令</summary>
    partial void OnIsDeletingChanged(bool value)
    {
        StartDeleteCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasConfirmedSourceDeletionChanged(bool value) => StartDeleteCommand.NotifyCanExecuteChanged();
    partial void OnPermanentlyDeleteLocalFilesChanged(bool value) => StartDeleteCommand.NotifyCanExecuteChanged();
}
