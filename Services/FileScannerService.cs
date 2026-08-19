using System.IO;
using System.Collections.Concurrent;
using System.Security.Principal;
using System.Threading.Channels;
using FileGroupy.Models;
using Microsoft.Win32.SafeHandles;

namespace FileGroupy.Services;

/// <summary>基于扩展名递归扫描本地目录的默认实现</summary>
public sealed class FileScannerService(IScanCacheStore cacheStore) : IFileScannerService
{
    /// <summary>本地目录缓存保持较短时长, 减少重复打开大目录时的等待并限制陈旧结果窗口</summary>
    private static readonly TimeSpan LocalScanCacheLifetime = TimeSpan.FromMinutes(10);
    /// <inheritdoc />
    public Task<FolderScanResult> ScanAsync(
        string folderPath,
        IProgress<FileScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ScanCoreAsync(folderPath, progress, cancellationToken, useCache: true, storeResult: true);

    private async Task<FolderScanResult> ScanCoreAsync(
        string folderPath,
        IProgress<FileScanProgress>? progress,
        CancellationToken cancellationToken,
        bool useCache,
        bool storeResult,
        FileScanPhase phase = FileScanPhase.Scanning,
        int estimatedTotalFiles = 0)
    {
        var sourceId = GetLocalSourceId(folderPath);
        if (useCache && await TryGetCachedScanAsync(sourceId, folderPath, progress, cancellationToken).ConfigureAwait(false) is { } cachedResult)
        {
            return cachedResult;
        }

        // 目录生产者保持单线程, 避免在 HDD 或移动磁盘上并发遍历目录造成随机 I/O.
        // 有界通道负责背压, 防止文件发现速度过快时无限积累待处理对象.
        var channel = Channel.CreateBounded<FileInfo>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false
        });
        var files = new ConcurrentBag<FileItem>();
        var categoryTotals = Enum.GetValues<FileCategory>().ToDictionary(category => category, _ => new CategoryScanSummary(0, 0));
        var syncRoot = new object();
        // 资源管理器“包含的文件夹”不计算用户选择的根目录本身.
        var folderCount = -1;
        var totalBytes = 0L;
        var progressTimer = System.Diagnostics.Stopwatch.StartNew();

        // 生产者只负责发现路径, 不在目录遍历阶段执行图片解码等重操作.
        var producer = Task.Run(() =>
        {
            try
            {
                // 明确还原到进程用户令牌, 避免线程池残留的 WPD/系统模拟令牌扩大扫描范围.
                WindowsIdentity.RunImpersonated(SafeAccessTokenHandle.InvalidHandle, () =>
                {
                    var folders = new Stack<string>();
                    folders.Push(folderPath);
                    while (folders.Count > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var currentFolder = folders.Pop();
                        Interlocked.Increment(ref folderCount);
                        try
                        {
                            foreach (var entry in new DirectoryInfo(currentFolder).EnumerateFileSystemInfos())
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                                {
                                    continue;
                                }

                                if ((entry.Attributes & FileAttributes.Directory) != 0)
                                {
                                    folders.Push(entry.FullName);
                                }
                                else if (entry is FileInfo file)
                                {
                                    channel.Writer.WriteAsync(file, cancellationToken).AsTask().GetAwaiter().GetResult();
                                }
                            }
                        }
                        catch (UnauthorizedAccessException) { }
                        catch (IOException) { }
                    }
                });
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        // 工作线程只处理文件元数据, 并发度根据磁盘类型限制在较小范围.
        var workerCount = GetFileProcessingConcurrency(folderPath);
        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
        {
            await foreach (var info in channel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    var category = FileCategoryCatalog.GetCategory(info.Extension);
                    files.Add(new FileItem(info.Name, info.FullName, info.Extension, info.Length, info.LastWriteTime, category));
                    lock (syncRoot)
                    {
                        totalBytes += info.Length;
                        var current = categoryTotals[category];
                        categoryTotals[category] = new CategoryScanSummary(current.FileCount + 1, current.TotalSize + info.Length);
                        if (progressTimer.ElapsedMilliseconds >= 300)
                        {
                            ReportProgress(progress, folderCount, files.Count, totalBytes, categoryTotals, phase, estimatedTotalFiles);
                            progressTimer.Restart();
                        }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }, cancellationToken)).ToArray();

        await Task.WhenAll(workers.Prepend(producer)).ConfigureAwait(false);
        ReportProgress(progress, folderCount, files.Count, totalBytes, categoryTotals, phase, estimatedTotalFiles);
        var result = new FolderScanResult(folderPath, folderCount, files.ToList());
        if (storeResult)
        {
            cacheStore.StoreScan(StorageSourceKind.LocalFileSystem, sourceId, folderPath, result, cancellationToken);
        }
        return result;
    }

    /// <inheritdoc />
    public Task<FolderScanResult> RefreshAsync(
        string folderPath,
        IProgress<FileScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RefreshCoreAsync(folderPath, progress, cancellationToken);
    }

    private async Task<FolderScanResult> RefreshCoreAsync(
        string folderPath,
        IProgress<FileScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourceId = GetLocalSourceId(folderPath);
        var cachedResult = await TryGetCachedScanAsync(sourceId, folderPath, progress, cancellationToken, TimeSpan.MaxValue).ConfigureAwait(false);
        if (cachedResult is null)
        {
            return await ScanCoreAsync(folderPath, progress, cancellationToken, useCache: false, storeResult: true).ConfigureAwait(false);
        }

        var sourceResult = await ScanCoreAsync(
            folderPath,
            progress,
            cancellationToken,
            useCache: false,
            storeResult: false,
            phase: FileScanPhase.RefreshingSource,
            estimatedTotalFiles: cachedResult.Files.Count).ConfigureAwait(false);
        if (HasSameFileMetadata(cachedResult, sourceResult))
        {
            return cachedResult;
        }

        cacheStore.StoreScan(StorageSourceKind.LocalFileSystem, sourceId, folderPath, sourceResult, cancellationToken);
        return sourceResult;
    }

    private Task<FolderScanResult?> TryGetCachedScanAsync(
        string sourceId,
        string folderPath,
        IProgress<FileScanProgress>? progress,
        CancellationToken cancellationToken,
        TimeSpan? maximumAge = null) =>
        Task.Run(async () => await cacheStore.TryGetScanAsync(
            StorageSourceKind.LocalFileSystem,
            sourceId,
            folderPath,
            maximumAge ?? LocalScanCacheLifetime,
            progress,
            cancellationToken).ConfigureAwait(false), cancellationToken);

    private static bool HasSameFileMetadata(FolderScanResult cachedResult, FolderScanResult sourceResult)
    {
        if (cachedResult.FolderCount != sourceResult.FolderCount || cachedResult.Files.Count != sourceResult.Files.Count)
        {
            return false;
        }

        var cachedFiles = cachedResult.Files.ToDictionary(file => file.FullPath, StringComparer.OrdinalIgnoreCase);
        return sourceResult.Files.All(file => cachedFiles.TryGetValue(file.FullPath, out var cachedFile)
            && cachedFile.Size == file.Size
            && cachedFile.LastModified == file.LastModified);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> FindInvalidImagePathsAsync(IReadOnlyCollection<FileItem> files, CancellationToken cancellationToken = default)
    {
        // 图片完整解码只在用户主动筛选无效图像时执行, 避免拖慢普通扫描.
        // SVG 是矢量格式, WPF BitmapDecoder 不负责解析 SVG, 因此必须明确排除.
        var localRasterImages = files.Where(file => file.SourceKind == StorageSourceKind.LocalFileSystem
                                                    && file.Category == FileCategory.Images
                                && !ImageValidation.IsSvg(file.Extension))
                                     .ToArray();
        var validationStates = new ConcurrentDictionary<FileItem, bool>(cacheStore.GetImageValidationStates(localRasterImages, TimeSpan.FromHours(24)));
        var uncachedImages = localRasterImages.Where(file => !validationStates.ContainsKey(file)).ToArray();
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = GetImageValidationConcurrency(uncachedImages.FirstOrDefault()?.FullPath)
        };
        // 图片验证允许有限并发, 兼顾 SSD 吞吐和移动磁盘稳定性.
        await Parallel.ForEachAsync(uncachedImages, parallelOptions, (file, token) =>
        {
            validationStates[file] = !CanDecodeImage(file.FullPath);
            return ValueTask.CompletedTask;
        });
        cacheStore.StoreImageValidationStates(validationStates);
        return validationStates.Where(pair => pair.Value)
                               .Select(pair => pair.Key.FullPath)
                               .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>复制分类统计后再上报,避免 UI 线程读取后台扫描中的可变集合</summary>
    private static void ReportProgress(
        IProgress<FileScanProgress>? progress,
        int folders,
        int files,
        long bytes,
        IReadOnlyDictionary<FileCategory, CategoryScanSummary> categories,
        FileScanPhase phase = FileScanPhase.Scanning,
        int totalFiles = 0,
        FolderScanResult? cachedResult = null)
    {
        progress?.Report(new FileScanProgress(folders, files, bytes, new Dictionary<FileCategory, CategoryScanSummary>(categories), phase, totalFiles, cachedResult));
    }

    /// <summary>以卷根路径作为本地来源标识, 使同一磁盘下的目录缓存能够稳定分区</summary>
    private static string GetLocalSourceId(string folderPath) => Path.GetPathRoot(Path.GetFullPath(folderPath)) ?? folderPath;

    /// <summary>命中本地缓存时重建固定结构的进度快照, 保持概览页显示一致</summary>
    private static void ReportCachedProgress(IProgress<FileScanProgress>? progress, FolderScanResult result, bool includeResult = false)
    {
        var categories = Enum.GetValues<FileCategory>().ToDictionary(category => category, _ => new CategoryScanSummary(0, 0));
        var totalBytes = 0L;
        foreach (var file in result.Files)
        {
            totalBytes += file.Size;
            var current = categories[file.Category];
            categories[file.Category] = new CategoryScanSummary(current.FileCount + 1, current.TotalSize + file.Size);
        }

        ReportProgress(progress, result.FolderCount, result.Files.Count, totalBytes, categories, FileScanPhase.ReadingCache, result.Files.Count, includeResult ? result : null);
    }

    /// <summary>使用顺序读取和延迟像素加载快速验证本地图像头</summary>
    private static bool CanDecodeImage(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                64 * 1024, FileOptions.SequentialScan);
            return ImageValidation.CanReadRasterImage(stream);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>本地文件处理并发度根据磁盘类型保持在较小范围</summary>
    private static int GetFileProcessingConcurrency(string folderPath)
    {
        var root = Path.GetPathRoot(folderPath);
        var driveType = !string.IsNullOrWhiteSpace(root) ? new DriveInfo(root).DriveType : DriveType.Unknown;
        return driveType == DriveType.Fixed ? Math.Min(4, Math.Max(2, Environment.ProcessorCount / 2)) : 2;
    }

    /// <summary>图像解码并发度对移动设备和网络位置保持保守</summary>
    private static int GetImageValidationConcurrency(string? path) =>
        !string.IsNullOrWhiteSpace(path) && GetFileProcessingConcurrency(path) > 2 ? 4 : 2;

}
