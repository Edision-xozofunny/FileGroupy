using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaDevices;
using FileGroupy.Models;

namespace FileGroupy.Services;

/// <summary>通过 Windows WPD 协议访问 MTP 和 PTP 设备</summary>
public sealed class MtpDeviceService : IMtpDeviceService
{
    /// <summary>保护进程内设备扫描缓存的锁</summary>
    private static readonly object ScanCacheLock = new();
    /// <summary>按设备和路径保存的扫描结果缓存</summary>
    private static readonly Dictionary<string, CachedScan> ScanCache = new(StringComparer.Ordinal);
    /// <summary>扫描缓存的有效期</summary>
    private static readonly TimeSpan ScanCacheLifetime = TimeSpan.FromHours(1);
    /// <summary>设备扫描时跳过的低价值目录</summary>
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".thumbnails", "cache", "code_cache", "databases", "node_modules"
    };
    /// <summary>单个设备目录允许读取的最大条目数</summary>
    private const int MaximumEntriesPerDirectory = 20_000;

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
                        // iPhone 在 Windows 中通常通过 Apple Mobile Device 驱动以 PTP 设备出现,
                        // 设备类型可能被报告为 Camera、MediaPlayer 或 Generic
                        var protocol = ResolvePortableProtocol(device);
                        if (protocol is null)
                        {
                            continue;
                        }

                        // 仅验证可访问根目录,不再要求根目录下必须可枚举到子项,
                        _ = device.GetRootDirectory();

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
    public Task<FileTransferResult> DeleteFilesAsync(
        IReadOnlyCollection<FileItem> sourceFiles,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => DeleteFiles(sourceFiles, progress, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public void InvalidateScanCache(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        InvalidateCachedScans(deviceId);
    }

    /// <inheritdoc />
    public Task<IReadOnlySet<string>> FindInvalidImagePathsAsync(
        IReadOnlyCollection<FileItem> files,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => FindInvalidImagePaths(files, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<string> DownloadPreviewFileAsync(FileItem file, CancellationToken cancellationToken = default) =>
        Task.Run(() => DownloadPreviewFile(file, cancellationToken), cancellationToken);

    /// <summary>按设备顺序下载并快速验证图像, 防止多会话并发导致 MTP/PTP 设备断连</summary>
    private static IReadOnlySet<string> FindInvalidImagePaths(IReadOnlyCollection<FileItem> files, CancellationToken cancellationToken)
    {
        var invalidPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deviceGroups = files.Where(file => file.SourceKind == StorageSourceKind.MtpDevice
                                               && file.Category == FileCategory.Images
                                               && !ImageValidation.IsSvg(file.Extension)
                                       && !string.IsNullOrWhiteSpace(file.SourceId))
                                .GroupBy(file => file.SourceId!, StringComparer.Ordinal);

        foreach (var deviceGroup in deviceGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var device = MediaDevice.GetDevices().FirstOrDefault(candidate => candidate.DeviceId == deviceGroup.Key);
            if (device is null)
            {
                continue;
            }

            device.Connect();
            try
            {
                foreach (var file in deviceGroup)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        // DeleteOnClose 避免大图常驻临时目录, 单会话也避免每张图重复握手.
                        using var stream = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite,
                            FileShare.None, 256 * 1024, FileOptions.SequentialScan | FileOptions.DeleteOnClose);
                        device.DownloadFile(file.FullPath, stream);
                        stream.Position = 0;
                        if (!ImageValidation.CanReadRasterImage(stream))
                        {
                            invalidPaths.Add(file.FullPath);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // 传输失败通常来自连接或权限问题, 不将其错误标注为损坏图像.
                    }
                }
            }
            finally
            {
                device.Disconnect();
            }
        }

        return invalidPaths;
    }

    private static FolderScanResult Scan(MtpDeviceInfo deviceInfo, string rootPath, IProgress<FileScanProgress>? progress, CancellationToken cancellationToken)
    {
        var cacheKey = GetScanCacheKey(deviceInfo.DeviceId, rootPath);
        if (TryGetCachedScan(cacheKey, deviceInfo.DeviceId, out var cachedResult))
        {
            ReportCachedProgress(progress, cachedResult);
            return cachedResult;
        }

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
                    var entriesScanned = 0;
                    foreach (var entry in currentFolder.EnumerateFileSystemInfos())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++entriesScanned > MaximumEntriesPerDirectory)
                        {
                            skippedItemCount++;
                            break;
                        }

                        try
                        {
                            if (entry is MediaDirectoryInfo directory)
                            {
                                if (ShouldScanDirectory(directory.Name))
                                {
                                    folders.Push(directory);
                                }
                                else
                                {
                                    skippedItemCount++;
                                }

                                continue;
                            }

                            if (entry is not MediaFileInfo file)
                            {
                                continue;
                            }

                            var extension = Path.GetExtension(file.Name);
                            var category = GetCategory(extension);
                            var fileLength = file.Length > long.MaxValue ? long.MaxValue : (long)file.Length;
                            files.Add(new FileItem(file.Name, file.FullName, extension, fileLength, DateTime.MinValue, category, StorageSourceKind.MtpDevice, deviceInfo.DeviceId));
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
                    skippedItemCount++;
                    if (progressTimer.ElapsedMilliseconds >= 300)
                    {
                        ReportProgress(progress, folderCount, files.Count, totalBytes, categoryTotals);
                        progressTimer.Restart();
                    }
                }
            }

            ReportProgress(progress, folderCount, files.Count, totalBytes, categoryTotals);
            var result = new FolderScanResult($"{deviceInfo.DisplayName}（{GetProtocolName(deviceInfo.Protocol)}）/{rootPath}", folderCount, files, skippedItemCount);
            StoreCachedScan(cacheKey, deviceInfo.DeviceId, result);
            return result;
        }
        finally
        {
            device.Disconnect();
        }
    }

    /// <summary>在一个设备会话中顺序下载文件;MTP 设备通常无法从并行数据流中获得更高吞吐</summary>
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
            var successfulTransfers = new List<FileTransferSuccess>();
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
                        destinationPath = GetAvailableLocalPath(destinationPath);
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
                    successfulTransfers.Add(new FileTransferSuccess(file.FullPath, destinationPath));
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

            var result = new FileTransferResult(succeeded, skipped, failures, successfulSourcePaths, successfulTransfers);
            if (options.MoveFiles && succeeded > 0)
            {
                InvalidateCachedScans(deviceId);
            }

            return result;
        }
        finally
        {
            device.Disconnect();
        }
    }

    /// <summary>在临时目录下载单个 MTP 文件,供预览或 Shell 默认程序访问</summary>
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

    /// <summary>在单一 MTP 会话中顺序上传本地文件;成功上传后才删除本地源文件以实现移动</summary>
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
            var successfulTransfers = new List<FileTransferSuccess>();
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
                    successfulTransfers.Add(new FileTransferSuccess(file.FullPath, destinationPath));
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

            var result = new FileTransferResult(succeeded, skipped, failures, successfulSourcePaths, successfulTransfers);
            if (succeeded > 0)
            {
                InvalidateCachedScans(deviceId);
            }

            return result;
        }
        finally
        {
            device.Disconnect();
        }
    }

    private static FileTransferResult DeleteFiles(
        IReadOnlyCollection<FileItem> sourceFiles,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
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
            var successfulSourcePaths = new List<string>();
            var successfulTransfers = new List<FileTransferSuccess>();
            var succeeded = 0;
            var completed = 0;
            var transferredBytes = 0L;
            var totalBytes = sourceFiles.Sum(file => file.Size);
            foreach (var file in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!device.FileExists(file.FullPath))
                    {
                        throw new FileNotFoundException("源文件已不存在", file.FullPath);
                    }

                    device.DeleteFile(file.FullPath);
                    if (device.FileExists(file.FullPath))
                    {
                        throw new IOException("文件删除后仍存在");
                    }

                    succeeded++;
                    successfulSourcePaths.Add(file.FullPath);
                    successfulTransfers.Add(new FileTransferSuccess(file.FullPath, string.Empty));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures.Add(new FileTransferFailure(file.Name, file.FullPath, string.Empty, file.Size, file.SourceKind, exception.Message));
                }
                finally
                {
                    completed++;
                    transferredBytes += file.Size;
                    progress?.Report(new FileTransferProgress(completed, sourceFiles.Count, transferredBytes, totalBytes));
                }
            }

            if (succeeded > 0)
            {
                InvalidateCachedScans(deviceId);
            }

            return new FileTransferResult(succeeded, 0, failures, successfulSourcePaths, successfulTransfers);
        }
        finally
        {
            device.Disconnect();
        }
    }

    private static void ReportProgress(IProgress<FileScanProgress>? progress, int folders, int files, long bytes, IReadOnlyDictionary<FileCategory, CategoryScanSummary> categories) =>
        progress?.Report(new FileScanProgress(folders, files, bytes, new Dictionary<FileCategory, CategoryScanSummary>(categories)));

    private static PortableDeviceProtocol? ResolvePortableProtocol(MediaDevice device)
    {
        // 先使用驱动报告的明确类型, 再用 Apple 标识补充 iPhone 的 PTP 识别.
        if (device.DeviceType == DeviceType.Phone)
        {
            return PortableDeviceProtocol.Mtp;
        }

        if (device.DeviceType == DeviceType.Camera)
        {
            return PortableDeviceProtocol.Ptp;
        }

        var manufacturer = device.Manufacturer ?? string.Empty;
        var friendlyName = device.FriendlyName ?? string.Empty;
        if (manufacturer.Contains("Apple", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("iPad", StringComparison.OrdinalIgnoreCase))
        {
            return PortableDeviceProtocol.Ptp;
        }

        // Generic 和 MediaPlayer 设备可能是 USB 存储设备或驱动器桥接对象.
        // 未明确识别为手机, 相机或 Apple 设备时不将其暴露为 PTP 设备.
        return null;
    }

    private static void ReportCachedProgress(IProgress<FileScanProgress>? progress, FolderScanResult result)
    {
        var categories = Enum.GetValues<FileCategory>().ToDictionary(category => category, _ => new CategoryScanSummary(0, 0));
        var totalBytes = 0L;
        foreach (var file in result.Files)
        {
            totalBytes += file.Size;
            var current = categories[file.Category];
            categories[file.Category] = new CategoryScanSummary(current.FileCount + 1, current.TotalSize + file.Size);
        }

        ReportProgress(progress, result.FolderCount, result.Files.Count, totalBytes, categories);
    }

    private static string GetScanCacheKey(string deviceId, string rootPath) => $"{deviceId}\n{rootPath}";

    private static bool TryGetCachedScan(string cacheKey, string deviceId, out FolderScanResult result)
    {
        lock (ScanCacheLock)
        {
            if (ScanCache.TryGetValue(cacheKey, out var cachedScan) && DateTimeOffset.UtcNow - cachedScan.CreatedAt <= ScanCacheLifetime)
            {
                result = cachedScan.Result;
                return true;
            }

            ScanCache.Remove(cacheKey);
        }

        if (TryReadPersistentScan(deviceId, cacheKey, out var persistedCachedScan))
        {
            lock (ScanCacheLock)
            {
                ScanCache[cacheKey] = persistedCachedScan;
            }

            result = persistedCachedScan.Result;
            return true;
        }

        result = null!;
        return false;
    }

    private static void StoreCachedScan(string cacheKey, string deviceId, FolderScanResult result)
    {
        var cachedScan = new CachedScan(result, DateTimeOffset.UtcNow);
        lock (ScanCacheLock)
        {
            ScanCache[cacheKey] = cachedScan;
        }

        WritePersistentScan(deviceId, cacheKey, cachedScan);
    }

    private static void InvalidateCachedScans(string deviceId)
    {
        lock (ScanCacheLock)
        {
            foreach (var cacheKey in ScanCache.Keys.Where(cacheKey => cacheKey.StartsWith($"{deviceId}\n", StringComparison.Ordinal)).ToArray())
            {
                ScanCache.Remove(cacheKey);
            }
        }

        try
        {
            var deviceCacheDirectory = GetDeviceCacheDirectory(deviceId);
            if (Directory.Exists(deviceCacheDirectory))
            {
                Directory.Delete(deviceCacheDirectory, true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool ShouldScanDirectory(string directoryName) => !ExcludedDirectoryNames.Contains(directoryName);

    private static bool TryReadPersistentScan(string deviceId, string cacheKey, out CachedScan cachedScan)
    {
        var cacheFilePath = GetCacheFilePath(deviceId, cacheKey);
        try
        {
            if (File.Exists(cacheFilePath))
            {
                var savedScan = JsonSerializer.Deserialize<CachedScan>(File.ReadAllText(cacheFilePath));
                if (savedScan is not null && DateTimeOffset.UtcNow - savedScan.CreatedAt <= ScanCacheLifetime)
                {
                    cachedScan = savedScan;
                    return true;
                }
            }

            File.Delete(cacheFilePath);
        }
        catch (IOException) { }
        catch (JsonException) { }
        catch (UnauthorizedAccessException) { }

        cachedScan = null!;
        return false;
    }

    private static void WritePersistentScan(string deviceId, string cacheKey, CachedScan cachedScan)
    {
        try
        {
            var cacheFilePath = GetCacheFilePath(deviceId, cacheKey);
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
            var temporaryPath = $"{cacheFilePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(cachedScan));
            File.Move(temporaryPath, cacheFilePath, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string GetCacheFilePath(string deviceId, string cacheKey) =>
        Path.Combine(GetDeviceCacheDirectory(deviceId), $"{GetHash(cacheKey)}.json");

    private static string GetDeviceCacheDirectory(string deviceId) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileGroupy", "MtpScanCache", GetHash(deviceId));

    private static string GetHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record CachedScan(FolderScanResult Result, DateTimeOffset CreatedAt);

    /// <summary>根据扩展名确定内置分类</summary>
    private static FileCategory GetCategory(string extension) => FileCategoryCatalog.GetCategory(extension);

    private static string GetProtocolName(PortableDeviceProtocol protocol) => protocol == PortableDeviceProtocol.Ptp ? "PTP" : "MTP";

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

    /// <summary>移动设备文件后确认源对象已消失,避免将仍存在的文件从列表中过早移除</summary>
    private static void EnsureMtpSourceWasMoved(MediaDevice device, string sourcePath)
    {
        if (device.FileExists(sourcePath))
        {
            throw new IOException("目标复制完成，但无法删除设备中的源文件");
        }
    }

    /// <summary>移动本地文件后确认源文件已删除,避免将失效状态报告为成功</summary>
    private static void EnsureLocalSourceWasMoved(string sourcePath)
    {
        if (File.Exists(sourcePath))
        {
            throw new IOException("目标上传完成，但无法删除本地源文件");
        }
    }

    private static string GetAvailableLocalPath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var suffix = 1; ; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name} ({suffix}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

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
