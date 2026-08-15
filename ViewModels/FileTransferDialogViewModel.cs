using System.Windows.Forms;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileGroupy.Models;
using FileGroupy.Services;
using FileGroupy.Views;

namespace FileGroupy.ViewModels;

/// <summary>复制或移动对话框的视图模型, 管理目标位置, 进度与取消状态</summary>
public partial class FileTransferDialogViewModel : ObservableObject
{
    /// <summary>执行实际 I/O 操作的批量传输服务</summary>
    private readonly IFileTransferService _transferService;
    /// <summary>用于选择手机目标设备的 MTP 服务</summary>
    private readonly IMtpDeviceService _mtpDeviceService;
    /// <summary>打开对话框时冻结的源文件集合</summary>
    private readonly IReadOnlyCollection<FileItem> _sourceFiles;
    /// <summary>当前传输任务的取消源</summary>
    private CancellationTokenSource? _cancellationTokenSource;
    /// <summary>取消完成后是否自动关闭对话框</summary>
    private bool _closeAfterCancellation;

    /// <summary>创建指定复制或移动模式的对话框状态</summary>
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

    /// <summary>对话框标题,随复制或移动模式变化</summary>
    public string Title { get; }
    /// <summary>指示本次任务是否在复制完成后删除源文件</summary>
    public bool MoveFiles { get; }
    /// <summary>已选源文件的数量和大小摘要</summary>
    public string SelectionSummary { get; }
    /// <summary>针对 MTP 来源显示的传输限制提示</summary>
    public string SourceHint { get; }
    /// <summary>移动成功的源路径集合</summary>
    public IReadOnlyList<string> MovedSourcePaths { get; private set; } = [];
    /// <summary>最近一次执行结果, 供调用页同步文件树</summary>
    public FileTransferResult? LastResult { get; private set; }
    /// <summary>请求对话框在取消完成后关闭.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>本地目标目录</summary>
    [ObservableProperty] private string _destinationPath = string.Empty;
    /// <summary>选中的 MTP 目标设备</summary>
    [ObservableProperty] private MtpDeviceInfo? _destinationMtpDevice;
    /// <summary>目标位置说明文本</summary>
    [ObservableProperty] private string _destinationDescription = "目标：本地文件夹";
    [ObservableProperty] private bool _preserveSourceStructure;
    [ObservableProperty] private bool _overwriteAll;
    [ObservableProperty] private bool _skipAllConflicts;
    [ObservableProperty] private bool _renameDuplicates;
    /// <summary>不保留目录结构时才允许重命名冲突文件</summary>
    public bool CanRenameDuplicates => !PreserveSourceStructure;
    [ObservableProperty] private bool _isTransferring;
    [ObservableProperty] private string _statusText = "选择目标文件夹后开始操作";
    [ObservableProperty] private double _progressPercent;
    public ObservableCollection<FileTransferFailure> Failures { get; } = [];
    /// <summary>是否存在可查看的失败文件明细</summary>
    public bool HasFailures => Failures.Count > 0;

    /// <summary>显示本地文件夹选择器</summary>
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

    /// <summary>显示设备选择器并设置 MTP 目标</summary>
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

    /// <summary>创建取消源并执行当前配置的批量传输</summary>
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

            if (DestinationMtpDevice?.Protocol == PortableDeviceProtocol.Ptp && _sourceFiles.All(file => file.SourceKind == StorageSourceKind.LocalFileSystem))
            {
                throw new NotSupportedException("当前 PTP 设备通常不支持从电脑写入文件请先从设备复制到本地，或在设备侧执行删除\n若需写入，请改用支持 MTP 写入的设备");
            }

            var result = await _transferService.TransferAsync(_sourceFiles,
                new FileTransferOptions(DestinationPath, PreserveSourceStructure, OverwriteAll, SkipAllConflicts, RenameDuplicates, MoveFiles, DestinationMtpDevice?.DeviceId), progress, _cancellationTokenSource.Token);
            LastResult = result;
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
            if (_closeAfterCancellation)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>取消传输或在未开始时直接关闭对话框</summary>
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

    /// <summary>打开本次传输失败文件的明细窗口</summary>
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

    partial void OnDestinationPathChanged(string value) => StartCommand.NotifyCanExecuteChanged();
    partial void OnDestinationMtpDeviceChanged(MtpDeviceInfo? value) => StartCommand.NotifyCanExecuteChanged();
    partial void OnIsTransferringChanged(bool value) => StartCommand.NotifyCanExecuteChanged();

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
