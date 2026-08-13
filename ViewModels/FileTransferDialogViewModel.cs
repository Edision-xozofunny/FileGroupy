using System.Windows.Forms;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileGroupy.Models;
using FileGroupy.Services;
using FileGroupy.Views;

namespace FileGroupy.ViewModels;

/// <summary>复制或移动对话框的视图模型，将传输执行逻辑与 WPF 视图分离</summary>
public partial class FileTransferDialogViewModel : ObservableObject
{
    /// <summary>执行实际 I/O 操作的批量传输服务</summary>
    private readonly IFileTransferService _transferService;
    /// <summary>用于枚举并选择手机目标设备的 MTP 服务</summary>
    private readonly IMtpDeviceService _mtpDeviceService;
    /// <summary>打开对话框时冻结的源文件集合，避免用户后续勾选变化影响本次任务</summary>
    private readonly IReadOnlyCollection<FileItem> _sourceFiles;
    /// <summary>当前正在执行任务的取消源；空值表示没有活动传输</summary>
    private CancellationTokenSource? _cancellationTokenSource;

    /// <summary>创建指定操作模式的传输对话框状态</summary>
    /// <param name="transferService">批量文件传输服务</param>
    /// <param name="sourceFiles">本次操作的已选源文件</param>
    /// <param name="moveFiles"><see langword="true"/> 为移动模式；否则为复制模式</param>
    public FileTransferDialogViewModel(
        IFileTransferService transferService,
        IMtpDeviceService mtpDeviceService,
        IReadOnlyCollection<FileItem> sourceFiles,
        bool moveFiles)
    {
        _transferService = transferService;
        _mtpDeviceService = mtpDeviceService;
        _sourceFiles = sourceFiles;
        MoveFiles = moveFiles;
        Title = moveFiles ? "移动文件" : "复制文件";
        SelectionSummary = $"已选择 {sourceFiles.Count:N0} 个文件，共 {SizeFormatter.Format(sourceFiles.Sum(file => file.Size))}";
        SourceHint = sourceFiles.All(file => file.SourceKind == StorageSourceKind.MtpDevice)
            ? "便携设备文件将通过 Windows WPD 顺序传输到本地目标目录；为保证设备稳定性不会并行读取"
            : string.Empty;
    }

    /// <summary>对话框标题，随复制或移动模式变化</summary>
    public string Title { get; }
    /// <summary>指示本次任务是否在复制完成后删除源文件</summary>
    public bool MoveFiles { get; }
    /// <summary>用于界面展示的已选文件数量与总大小</summary>
    public string SelectionSummary { get; }
    /// <summary>针对 MTP 来源显示的传输限制提示；本地文件时为空</summary>
    public string SourceHint { get; }
    /// <summary>移动操作中已实际完成的源路径，供关闭对话框后的列表同步使用</summary>
    public IReadOnlyList<string> MovedSourcePaths { get; private set; } = [];

    /// <summary>由工具生成的目标目录公开绑定属性</summary>
    [ObservableProperty] private string _destinationPath = string.Empty;
    /// <summary>由工具生成的选中手机目标设备公开绑定属性；为空时目标为本地目录</summary>
    [ObservableProperty] private MtpDeviceInfo? _destinationMtpDevice;
    /// <summary>由工具生成的目标类型说明公开绑定属性</summary>
    [ObservableProperty] private string _destinationDescription = "目标：本地文件夹";
    /// <summary>由工具生成的保留源目录结构公开绑定属性</summary>
    [ObservableProperty] private bool _preserveSourceStructure;
    /// <summary>由工具生成的发生冲突时全部覆盖公开绑定属性</summary>
    [ObservableProperty] private bool _overwriteAll;
    /// <summary>由工具生成的发生冲突时全部跳过公开绑定属性</summary>
    [ObservableProperty] private bool _skipAllConflicts;
    /// <summary>由工具生成的重名文件自动加数字后缀公开绑定属性</summary>
    [ObservableProperty] private bool _renameDuplicates;
    /// <summary>不保留目录结构时才允许按重名规则生成新名称</summary>
    public bool CanRenameDuplicates => !PreserveSourceStructure;
    /// <summary>由工具生成的传输执行状态公开绑定属性</summary>
    [ObservableProperty] private bool _isTransferring;
    /// <summary>由工具生成的界面状态提示公开绑定属性</summary>
    [ObservableProperty] private string _statusText = "选择目标文件夹后开始操作";
    /// <summary>由工具生成的百分比进度公开绑定属性</summary>
    [ObservableProperty] private double _progressPercent;
    /// <summary>本次传输会话内的失败记录；新建传输对话框时自然清空</summary>
    public ObservableCollection<FileTransferFailure> Failures { get; } = [];
    /// <summary>是否存在可查看的失败文件明细</summary>
    public bool HasFailures => Failures.Count > 0;

    /// <summary>显示系统文件夹选择器并写入目标目录</summary>
        /// <summary>创建取消源并异步执行当前配置的批量传输</summary>
        /// <summary>请求取消正在进行的任务；服务会在可取消位置停止尚未完成的工作</summary>
        /// <summary>判断是否已选择目标目录且当前没有活动传输</summary>
        /// <returns>允许开始时返回 <see langword="true"/></returns>
    [RelayCommand]
    private void ChooseDestination()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择目标文件夹", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            DestinationPath = dialog.SelectedPath;
            DestinationMtpDevice = null;
            DestinationDescription = "目标：本地文件夹";
        }
    }

    /// <summary>显示连接手机列表，并将选中设备设置为本次传输目标</summary>
    [RelayCommand]
    private async Task ChooseMtpDestinationAsync()
    {
        IReadOnlyList<MtpDeviceInfo> devices;
        try
        {
            devices = await _mtpDeviceService.GetAvailablePortableDevicesAsync();
        }
        catch (Exception exception)
        {
            StatusText = $"无法读取手机设备：{exception.Message}";
            return;
        }

        if (devices.Count == 0)
        {
            StatusText = "未发现可访问的 MTP/PTP 设备请解锁设备并选择“文件传输”或“照片传输”后重试";
            return;
        }

        var dialog = new MtpDevicePickerDialog(devices)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (dialog.ShowDialog() == true && dialog.SelectedDevice is { } device)
        {
            DestinationMtpDevice = device;
            DestinationPath = string.Empty;
            DestinationDescription = device.Protocol == PortableDeviceProtocol.Ptp
                ? $"目标：PTP 设备 {device.DisplayName} 的媒体目录（是否允许写入取决于设备）"
                : $"目标：手机 {device.DisplayName} 的根存储目录";
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsTransferring = true;
        _cancellationTokenSource = new CancellationTokenSource();
        var progress = new Progress<FileTransferProgress>(value =>
        {
            ProgressPercent = value.TotalFiles == 0 ? 0 : (double)value.CompletedFiles / value.TotalFiles * 100;
            StatusText = $"正在处理 {value.CompletedFiles:N0}/{value.TotalFiles:N0} 个文件，{SizeFormatter.Format(value.TransferredBytes)} / {SizeFormatter.Format(value.TotalBytes)}";
        });

        try
        {
            Failures.Clear();
            OnPropertyChanged(nameof(HasFailures));
            ShowFailuresCommand.NotifyCanExecuteChanged();
            var result = await _transferService.TransferAsync(_sourceFiles,
                new FileTransferOptions(DestinationPath, PreserveSourceStructure, OverwriteAll, SkipAllConflicts, RenameDuplicates, MoveFiles, DestinationMtpDevice?.DeviceId), progress, _cancellationTokenSource.Token);
            MovedSourcePaths = MoveFiles ? result.SuccessfulSourcePaths : [];
            foreach (var failure in result.Failures)
            {
                Failures.Add(failure);
            }

            OnPropertyChanged(nameof(HasFailures));
            ShowFailuresCommand.NotifyCanExecuteChanged();
            StatusText = $"完成：成功 {result.Succeeded:N0}，跳过 {result.Skipped:N0}，失败 {result.Failures.Count:N0}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "操作已取消";
        }
        catch (Exception exception)
        {
            StatusText = $"操作失败：{exception.Message}";
        }
        finally
        {
            IsTransferring = false;
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cancellationTokenSource?.Cancel();

    /// <summary>打开本次传输失败文件的明细与 CSV 导出窗口</summary>
    [RelayCommand(CanExecute = nameof(HasFailures))]
    private void ShowFailures()
    {
        var dialog = new FileTransferFailuresDialog(Failures)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        dialog.ShowDialog();
    }

    private bool CanStart() => !IsTransferring && (!string.IsNullOrWhiteSpace(DestinationPath) || DestinationMtpDevice is not null);

    /// <summary>目标路径变化时刷新“开始”命令的可用性</summary>
    /// <param name="value">新的目标路径</param>
    partial void OnDestinationPathChanged(string value) => StartCommand.NotifyCanExecuteChanged();
    partial void OnDestinationMtpDeviceChanged(MtpDeviceInfo? value) => StartCommand.NotifyCanExecuteChanged();
    /// <summary>传输状态变化时刷新“开始”命令的可用性</summary>
    /// <param name="value">新的执行状态</param>
    partial void OnIsTransferringChanged(bool value) => StartCommand.NotifyCanExecuteChanged();

    /// <summary>启用覆盖策略时自动关闭跳过策略，保证冲突处理语义唯一</summary>
    /// <param name="value">新的覆盖策略状态</param>
        /// <summary>启用跳过策略时自动关闭覆盖策略，保证冲突处理语义唯一</summary>
        /// <param name="value">新的跳过策略状态</param>
    partial void OnOverwriteAllChanged(bool value)
    {
        if (value)
        {
            SkipAllConflicts = false;
            RenameDuplicates = false;
        }
    }

    partial void OnSkipAllConflictsChanged(bool value)
    {
        if (value)
        {
            OverwriteAll = false;
            RenameDuplicates = false;
        }
    }

    partial void OnRenameDuplicatesChanged(bool value)
    {
        if (value)
        {
            OverwriteAll = false;
            SkipAllConflicts = false;
        }
    }

    partial void OnPreserveSourceStructureChanged(bool value)
    {
        if (value)
        {
            RenameDuplicates = false;
        }

        OnPropertyChanged(nameof(CanRenameDuplicates));
    }
}