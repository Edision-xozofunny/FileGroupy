using System.IO;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Windows.Media.Imaging;
using FileGroupy.Models;

namespace FileGroupy.Services;

/// <summary>基于扩展名递归扫描本地目录的默认实现</summary>
public sealed class FileScannerService : IFileScannerService
{
    /// <summary>目录枚举选项: 忽略无权限位置并跳过联接点</summary>
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    /// <inheritdoc />
    public async Task<FolderScanResult> ScanAsync(
        string folderPath,
        IProgress<FileScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
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
        var folderCount = 0;
        var totalBytes = 0L;
        var progressTimer = System.Diagnostics.Stopwatch.StartNew();

        // 生产者只负责发现路径, 不在目录遍历阶段执行图片解码等重操作.
        var producer = Task.Run(async () =>
        {
            var folders = new Stack<string>();
            folders.Push(folderPath);
            try
            {
                while (folders.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var currentFolder = folders.Pop();
                    Interlocked.Increment(ref folderCount);
                    try
                    {
                        foreach (var entry in new DirectoryInfo(currentFolder).EnumerateFileSystemInfos("*", EnumerationOptions))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if ((entry.Attributes & FileAttributes.Directory) != 0)
                            {
                                folders.Push(entry.FullName);
                            }
                            else if (entry is FileInfo file)
                            {
                                await channel.Writer.WriteAsync(file, cancellationToken);
                            }
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
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
                            ReportProgress(progress, folderCount, files.Count, totalBytes, categoryTotals);
                            progressTimer.Restart();
                        }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }, cancellationToken)).ToArray();

        await Task.WhenAll(workers.Prepend(producer));
        ReportProgress(progress, folderCount, files.Count, totalBytes, categoryTotals);
        return new FolderScanResult(folderPath, folderCount, files.ToList());
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> FindInvalidImagePathsAsync(IReadOnlyCollection<FileItem> files, CancellationToken cancellationToken = default)
    {
        // 图片完整解码只在用户主动筛选无效图像时执行, 避免拖慢普通扫描.
        var invalidPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        // SVG 是矢量格式, WPF BitmapDecoder 不负责解析 SVG, 因此必须明确排除.
        var localRasterImages = files.Where(file => file.SourceKind == StorageSourceKind.LocalFileSystem
                                                    && file.Category == FileCategory.Images
                                                    && !IsSvg(file.Extension))
                                     .ToArray();
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = GetImageValidationConcurrency(localRasterImages.FirstOrDefault()?.FullPath)
        };
        // 图片验证允许有限并发, 兼顾 SSD 吞吐和移动磁盘稳定性.
        await Parallel.ForEachAsync(localRasterImages, parallelOptions, (file, token) =>
        {
            if (!CanDecodeImage(file.FullPath))
            {
                invalidPaths.TryAdd(file.FullPath, 0);
            }

            return ValueTask.CompletedTask;
        });
        return invalidPaths.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>复制分类统计后再上报,避免 UI 线程读取后台扫描中的可变集合</summary>
    private static void ReportProgress(IProgress<FileScanProgress>? progress, int folders, int files, long bytes, IReadOnlyDictionary<FileCategory, CategoryScanSummary> categories)
    {
        progress?.Report(new FileScanProgress(folders, files, bytes, new Dictionary<FileCategory, CategoryScanSummary>(categories)));
    }

    /// <summary>验证图像是否可由 WPF 解码, 损坏图像仍会保留在扫描结果中</summary>
    private static bool CanDecodeImage(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _ = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return true;
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

    /// <summary>SVG 是矢量图, 不交给 WPF 位图解码器验证</summary>
    private static bool IsSvg(string extension) => string.Equals(extension, ".svg", StringComparison.OrdinalIgnoreCase);

}
