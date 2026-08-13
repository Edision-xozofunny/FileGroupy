using System.IO;
using MediaDevices;
using FileGroupy.Models;

namespace FileGroupy.Services;

/// <summary>
/// 基于 MediaDevices/WPD 的 MTP/PTP 扫描实现手机端通常只允许低并发请求，
/// 因此采用单会话顺序枚举，避免设备断连和传输协议错误
/// </summary>
public sealed class MtpDeviceService : IMtpDeviceService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<MtpDeviceInfo>> GetAvailablePortableDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var devices = new List<MtpDeviceInfo>();
            foreach (var device in MediaDevice.GetDevices())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    device.Connect();
                    try
                    {
                        // Android 的 PTP 模式会被 WPD 报告为 Camera；U 盘和读卡器不会匹配这两个类型
                        var protocol = device.DeviceType switch
                        {
                            DeviceType.Phone => PortableDeviceProtocol.Mtp,
                            DeviceType.Camera => PortableDeviceProtocol.Ptp,
                            _ => (PortableDeviceProtocol?)null
                        };
                        if (protocol is null)
                        {
                            continue;
                        }

                        var root = device.GetRootDirectory();
                        // PTP 设备可能直接在根节点暴露媒体文件，不能仅以“存在子文件夹”判定可访问性
                        if (!root.EnumerateFileSystemInfos().Any())
                        {
                            continue;
                        }

                        devices.Add(new MtpDeviceInfo(device.DeviceId, device.FriendlyName, device.Manufacturer, protocol.Value));
                    }
                    finally
                    {
                        device.Disconnect();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // 未解锁、非 MTP/PTP 或无法读取存储的 WPD 设备不会显示在列表中
                }
                finally
                {
                    device.Dispose();
                }
            }

            return (IReadOnlyList<MtpDeviceInfo>)devices.OrderBy(device => device.DisplayName).ToList();
        }, cancellationToken);

    /// <inheritdoc />
    public Task<MtpFolderInfo> GetRootFolderAsync(MtpDeviceInfo deviceInfo, CancellationToken cancellationToken = default) =>
        Task.Run(() => WithDevice(deviceInfo, device =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = device.GetRootDirectory();
            return new MtpFolderInfo(string.IsNullOrWhiteSpace(root.Name) ? deviceInfo.DisplayName : root.Name, root.FullName);
        }), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<MtpFolderInfo>> GetChildFoldersAsync(MtpDeviceInfo deviceInfo, string parentPath, CancellationToken cancellationToken = default) =>
        Task.Run(() => WithDevice(deviceInfo, device =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (IReadOnlyList<MtpFolderInfo>)device.GetDirectoryInfo(parentPath)
                .EnumerateDirectories()
                .Select(directory => new MtpFolderInfo(directory.Name, directory.FullName))
                .OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }), cancellationToken);

    /// <inheritdoc />
    public Task<FolderScanResult> ScanAsync(
        MtpDeviceInfo deviceInfo,
        string rootPath,
        IProgress<FileScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(deviceInfo, rootPath, progress, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<FileTransferResult> TransferToLocalAsync(
        IReadOnlyCollection<FileItem> sourceFiles,
        FileTransferOptions options,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => TransferToLocal(sourceFiles, options, progress, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<FileTransferResult> TransferFromLocalAsync(
        IReadOnlyCollection<FileItem> sourceFiles,
        FileTransferOptions options,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => TransferFromLocal(sourceFiles, options, progress, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<string> DownloadPreviewFileAsync(FileItem file, CancellationToken cancellationToken = default) =>
        Task.Run(() => DownloadPreviewFile(file, cancellationToken), cancellationToken);

    /// <summary>在单个 MTP 会话中递归枚举设备根目录，避免设备不支持多会话并发造成的中断</summary>
    private static FolderScanResult Scan(MtpDeviceInfo deviceInfo, string rootPath, IProgress<FileScanProgress>? progress, CancellationToken cancellationToken)
    {
        using var device = MediaDevice.GetDevices().FirstOrDefault(candidate => candidate.DeviceId == deviceInfo.DeviceId)
            ?? throw new IOException("找不到设备请确认手机已解锁且处于文件传输模式");
        device.Connect();

        try
        {
            var files = new List<FileItem>();
            var folders = new Stack<MediaDirectoryInfo>();
            var categoryTotals = Enum.GetValues<FileCategory>().ToDictionary(category => category, _ => new CategoryScanSummary(0, 0));
            var progressTimer = System.Diagnostics.Stopwatch.StartNew();
            folders.Push(device.GetDirectoryInfo(rootPath));
            var folderCount = 0;
            var skippedItemCount = 0;
            var totalBytes = 0L;

            while (folders.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentFolder = folders.Pop();
                folderCount++;

                try
                {
                    foreach (var entry in currentFolder.EnumerateFileSystemInfos())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            if (entry is MediaDirectoryInfo directory)
                            {
                                folders.Push(directory);
                                continue;
                            }

                            if (entry is not MediaFileInfo file)
                            {
                                continue;
                            }

                            var extension = Path.GetExtension(file.Name);
                            var category = GetCategory(extension);
                            var fileLength = file.Length > long.MaxValue ? long.MaxValue : (long)file.Length;
                            var lastWriteTime = file.LastWriteTime ?? DateTime.MinValue;
                            files.Add(new FileItem(file.Name, file.FullName, extension, fileLength, lastWriteTime, category, StorageSourceKind.MtpDevice, deviceInfo.DeviceId));
                            totalBytes += fileLength;
                            var current = categoryTotals[category];
                            categoryTotals[category] = new CategoryScanSummary(current.FileCount + 1, current.TotalSize + fileLength);

                            if (progressTimer.ElapsedMilliseconds >= 300)
                            {
                                ReportProgress(progress, folderCount, files.Count, totalBytes, categoryTotals);
                                progressTimer.Restart();
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception)
                        {
                            // 部分 Android/WPD 对象会返回单项协议错误；跳过该项并继续同一目录的流式枚举
                            skippedItemCount++;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // 0x80042009 等 WPD 枚举错误通常只影响当前目录，不能丢弃此前已扫描的大容量结果
                    skippedItemCount++;
                    if (progressTimer.ElapsedMilliseconds >= 300)
                    {
                        ReportProgress(progress, folderCount, files.Count, totalBytes, categoryTotals);
                        progressTimer.Restart();
                    }
                }
            }

            ReportProgress(progress, folderCount, files.Count, totalBytes, categoryTotals);
            return new FolderScanResult($"{deviceInfo.DisplayName}（{GetProtocolName(deviceInfo.Protocol)}）/{rootPath}", folderCount, files, skippedItemCount);
        }
        finally
        {
            device.Disconnect();
        }
    }

    /// <summary>在一个设备会话中顺序下载文件；MTP 设备通常无法从并行数据流中获得更高吞吐</summary>
    private static FileTransferResult TransferToLocal(
        IReadOnlyCollection<FileItem> sourceFiles,
        FileTransferOptions options,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DestinationPath);
        if (!Directory.Exists(options.DestinationPath))
        {
            throw new DirectoryNotFoundException("目标文件夹不存在");
        }

        var deviceId = sourceFiles.Select(file => file.SourceId).Distinct(StringComparer.Ordinal).SingleOrDefault();
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new InvalidOperationException("MTP 文件缺少设备标识");
        }

        using var device = MediaDevice.GetDevices().FirstOrDefault(candidate => candidate.DeviceId == deviceId)
            ?? throw new IOException("手机已断开连接请重新连接并解锁设备");
        device.Connect();

        try
        {
            var failures = new List<FileTransferFailure>();
            var succeeded = 0;
            var skipped = 0;
            var successfulSourcePaths = new List<string>();
            var completed = 0;
            var transferredBytes = 0L;
            var totalBytes = sourceFiles.Sum(file => file.Size);
            var sourceRoot = FindCommonDirectory(sourceFiles.Select(file => file.FullPath));

            foreach (var file in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var destinationPath = GetDestinationPath(file, options.DestinationPath, sourceRoot, options.PreserveSourceStructure);
                    if (options.RenameDuplicates && !options.PreserveSourceStructure)
                    {
                        destinationPath = GetAvailableMtpPath(device, destinationPath);
                    }
                    if (File.Exists(destinationPath))
                    {
                        if (options.SkipAllConflicts || !options.OverwriteAll)
                        {
                            skipped++;
                            continue;
                        }

                        File.Delete(destinationPath);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                    {
                        device.DownloadFile(file.FullPath, destination);
                    }

                    if (options.MoveFiles)
                    {
                        device.DeleteFile(file.FullPath);
                        EnsureMtpSourceWasMoved(device, file.FullPath);
                    }

                    succeeded++;
                    successfulSourcePaths.Add(file.FullPath);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var destinationPath = GetDestinationPath(file, options.DestinationPath, sourceRoot, options.PreserveSourceStructure);
                    failures.Add(new FileTransferFailure(file.Name, file.FullPath, destinationPath, file.Size, file.SourceKind, exception.Message));
                }
                finally
                {
                    completed++;
                    transferredBytes += file.Size;
                    progress?.Report(new FileTransferProgress(completed, sourceFiles.Count, transferredBytes, totalBytes));
                }
            }

            return new FileTransferResult(succeeded, skipped, failures, successfulSourcePaths);
        }
        finally
        {
            device.Disconnect();
        }
    }

    /// <summary>在临时目录下载单个 MTP 文件，供预览或 Shell 默认程序访问</summary>
    private static string DownloadPreviewFile(FileItem file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(file.SourceId))
        {
            throw new InvalidOperationException("MTP 文件缺少设备标识");
        }

        using var device = MediaDevice.GetDevices().FirstOrDefault(candidate => candidate.DeviceId == file.SourceId)
            ?? throw new IOException("手机已断开连接请重新连接并解锁设备");
        device.Connect();
        try
        {
            var previewDirectory = Path.Combine(Path.GetTempPath(), "FileGroupy", "Preview");
            Directory.CreateDirectory(previewDirectory);
            var previewPath = Path.Combine(previewDirectory, $"{Guid.NewGuid():N}{file.Extension}");
            using var output = new FileStream(previewPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            device.DownloadFile(file.FullPath, output);
            return previewPath;
        }
        finally
        {
            device.Disconnect();
        }
    }

    /// <summary>在单一 MTP 会话中顺序上传本地文件；成功上传后才删除本地源文件以实现移动</summary>
    private static FileTransferResult TransferFromLocal(
        IReadOnlyCollection<FileItem> sourceFiles,
        FileTransferOptions options,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var deviceId = options.DestinationMtpDeviceId ?? throw new InvalidOperationException("未选择手机设备");
        using var device = MediaDevice.GetDevices().FirstOrDefault(candidate => candidate.DeviceId == deviceId)
            ?? throw new IOException("手机已断开连接请重新连接并解锁设备");
        device.Connect();

        try
        {
            var failures = new List<FileTransferFailure>();
            var succeeded = 0;
            var skipped = 0;
            var successfulSourcePaths = new List<string>();
            var completed = 0;
            var transferredBytes = 0L;
            var totalBytes = sourceFiles.Sum(file => file.Size);
            var sourceRoot = FindCommonDirectory(sourceFiles.Select(file => file.FullPath));
            var destinationRoot = device.GetRootDirectory().FullName;

            foreach (var file in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var relativePath = options.PreserveSourceStructure && !string.IsNullOrWhiteSpace(sourceRoot)
                        ? Path.GetRelativePath(sourceRoot, file.FullPath)
                        : file.Name;
                    var destinationPath = Path.Combine(destinationRoot, relativePath);
                    if (options.RenameDuplicates && !options.PreserveSourceStructure)
                    {
                        destinationPath = GetAvailableMtpPath(device, destinationPath);
                    }
                    EnsureMtpDirectory(device, Path.GetDirectoryName(destinationPath));

                    if (device.FileExists(destinationPath))
                    {
                        if (options.SkipAllConflicts || !options.OverwriteAll)
                        {
                            skipped++;
                            continue;
                        }

                        device.DeleteFile(destinationPath);
                    }

                    device.UploadFile(file.FullPath, destinationPath);
                    if (options.MoveFiles)
                    {
                        File.Delete(file.FullPath);
                        EnsureLocalSourceWasMoved(file.FullPath);
                    }

                    succeeded++;
                    successfulSourcePaths.Add(file.FullPath);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var relativePath = options.PreserveSourceStructure && !string.IsNullOrWhiteSpace(sourceRoot)
                        ? Path.GetRelativePath(sourceRoot, file.FullPath)
                        : file.Name;
                    var destinationPath = Path.Combine(destinationRoot, relativePath);
                    failures.Add(new FileTransferFailure(file.Name, file.FullPath, destinationPath, file.Size, file.SourceKind, exception.Message));
                }
                finally
                {
                    completed++;
                    transferredBytes += file.Size;
                    progress?.Report(new FileTransferProgress(completed, sourceFiles.Count, transferredBytes, totalBytes));
                }
            }

            return new FileTransferResult(succeeded, skipped, failures, successfulSourcePaths);
        }
        finally
        {
            device.Disconnect();
        }
    }

    /// <summary>生成分类统计副本并上报给 UI，防止后台集合被跨线程读取</summary>
    private static void ReportProgress(IProgress<FileScanProgress>? progress, int folders, int files, long bytes, IReadOnlyDictionary<FileCategory, CategoryScanSummary> categories) =>
        progress?.Report(new FileScanProgress(folders, files, bytes, new Dictionary<FileCategory, CategoryScanSummary>(categories)));

    /// <summary>根据扩展名确定内置分类</summary>
    private static FileCategory GetCategory(string extension) => FileCategoryCatalog.GetCategory(extension);

    /// <summary>将协议用于扫描结果与界面提示，不影响 WPD 的统一文件 API 调用</summary>
    private static string GetProtocolName(PortableDeviceProtocol protocol) => protocol == PortableDeviceProtocol.Ptp ? "PTP" : "MTP";

    /// <summary>根据选择的目录结构策略生成本地目标路径</summary>
    private static string GetDestinationPath(FileItem file, string destinationRoot, string sourceRoot, bool preserveStructure)
    {
        var relativePath = preserveStructure && !string.IsNullOrWhiteSpace(sourceRoot)
            ? Path.GetRelativePath(sourceRoot, file.FullPath)
            : file.Name;
        return Path.Combine(destinationRoot, relativePath);
    }

    /// <summary>计算多个 MTP 虚拟路径的共同父目录</summary>
    private static string FindCommonDirectory(IEnumerable<string> paths)
    {
        var directories = paths.Select(Path.GetDirectoryName).Where(path => path is not null).Cast<string>().ToArray();
        if (directories.Length == 0)
        {
            return string.Empty;
        }

        var common = directories[0];
        while (directories.Any(directory => !directory.StartsWith(common, StringComparison.OrdinalIgnoreCase)))
        {
            common = Path.GetDirectoryName(common) ?? string.Empty;
            if (string.IsNullOrEmpty(common))
            {
                break;
            }
        }

        return common;
    }

    /// <summary>确保上传目标的各级 MTP 目录存在</summary>
    private static void EnsureMtpDirectory(MediaDevice device, string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || device.DirectoryExists(directoryPath))
        {
            return;
        }

        var parent = Path.GetDirectoryName(directoryPath);
        EnsureMtpDirectory(device, parent);
        device.CreateDirectory(directoryPath);
    }

    /// <summary>移动设备文件后确认源对象已消失，避免将仍存在的文件从列表中过早移除。</summary>
    private static void EnsureMtpSourceWasMoved(MediaDevice device, string sourcePath)
    {
        if (device.FileExists(sourcePath))
        {
            throw new IOException("目标复制完成，但无法删除设备中的源文件");
        }
    }

    /// <summary>移动本地文件后确认源文件已删除，避免将失效状态报告为成功。</summary>
    private static void EnsureLocalSourceWasMoved(string sourcePath)
    {
        if (File.Exists(sourcePath))
        {
            throw new IOException("目标上传完成，但无法删除本地源文件");
        }
    }

    /// <summary>在 MTP 存储中查找可用的原名或“名称 (n).扩展名”目标路径</summary>
    private static string GetAvailableMtpPath(MediaDevice device, string path)
    {
        if (!device.FileExists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var suffix = 1; ; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name} ({suffix}){extension}");
            if (!device.FileExists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>在一次短生命周期会话中访问手机，确保 COM/WPD 连接始终被释放</summary>
    private static T WithDevice<T>(MtpDeviceInfo deviceInfo, Func<MediaDevice, T> action)
    {
        using var device = MediaDevice.GetDevices().FirstOrDefault(candidate => candidate.DeviceId == deviceInfo.DeviceId)
            ?? throw new IOException("手机已断开连接请重新连接并解锁设备");
        device.Connect();
        try
        {
            return action(device);
        }
        finally
        {
            device.Disconnect();
        }
    }
}