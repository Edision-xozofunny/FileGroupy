using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileGroupy.Models;
using FileGroupy.Services;
using FileGroupy.Views;

namespace FileGroupy.ViewModels;

/// <summary>批量删除对话框视图模型，统一管理开始、取消、进度与失败明细</summary>
public partial class FileDeleteDialogViewModel : ObservableObject
{
    /// <summary>执行本地与 MTP 删除的服务</summary>
    private readonly IFileTransferService _transferService;
    /// <summary>打开对话框时冻结的源文件集合，避免二次勾选改变当前任务</summary>
    private readonly IReadOnlyCollection<FileItem> _sourceFiles;
    /// <summary>当前删除任务的取消源，空值表示未在执行</summary>
    private CancellationTokenSource? _cancellationTokenSource;

    /// <summary>使用给定的文件集合构建删除对话框状态</summary>
    /// <param name="transferService">文件操作服务</param>
    /// <param name="sourceFiles">待删除文件集合</param>
    public FileDeleteDialogViewModel(IFileTransferService transferService, IReadOnlyCollection<FileItem> sourceFiles)
    {
        _transferService = transferService;
        _sourceFiles = sourceFiles;
        SelectionSummary = $"已选择 {sourceFiles.Count:N0} 个文件，共 {SizeFormatter.Format(sourceFiles.Sum(file => file.Size))}";
        SourceHint = sourceFiles.All(file => file.SourceKind == StorageSourceKind.MtpDevice)
            ? "便携设备删除将通过 Windows WPD 顺序执行，过程可能较慢"
            : string.Empty;
    }

    /// <summary>本次删除文件数量和体积摘要</summary>
    public string SelectionSummary { get; }
    /// <summary>删除来源限制提示，本地删除时为空</summary>
    public string SourceHint { get; }
    /// <summary>删除成功的源路径集合，供外部页面同步移除节点</summary>
    public IReadOnlyList<string> DeletedSourcePaths { get; private set; } = [];
    /// <summary>最近一次删除结果，供调用方刷新节点和统计信息</summary>
    public FileTransferResult? LastResult { get; private set; }

    /// <summary>由工具生成的删除执行状态公开绑定属性</summary>
    [ObservableProperty] private bool _isDeleting;
    /// <summary>由工具生成的删除进度百分比公开绑定属性</summary>
    [ObservableProperty] private double _progressPercent;
    /// <summary>由工具生成的状态文本公开绑定属性</summary>
    [ObservableProperty] private string _statusText = "准备就绪，点击“开始删除”执行";
    /// <summary>本次删除中的失败记录集合</summary>
    public ObservableCollection<FileTransferFailure> Failures { get; } = [];
    /// <summary>是否存在失败项，用于控制“查看失败详情”按钮</summary>
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
            var result = await _transferService.DeleteAsync(_sourceFiles, progress, _cancellationTokenSource.Token);
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
        }
    }

    /// <summary>取消正在执行的删除任务</summary>
    [RelayCommand]
    private void Cancel() => _cancellationTokenSource?.Cancel();

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

    /// <summary>判断“开始删除”按钮是否可用</summary>
    /// <returns>未在删除中时返回 <see langword="true"/></returns>
    private bool CanStart() => !IsDeleting;

    /// <summary>删除状态变化后刷新命令可用性</summary>
    /// <param name="value">新的删除执行状态</param>
    partial void OnIsDeletingChanged(bool value)
    {
        StartDeleteCommand.NotifyCanExecuteChanged();
    }
}
