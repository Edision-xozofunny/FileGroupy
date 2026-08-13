using System.IO;
using FileGroupy.Models;

namespace FileGroupy.Services;

/// <summary>基于扩展名递归扫描本地目录的默认实现</summary>
public sealed class FileScannerService : IFileScannerService
{
    /// <summary>在线程池中执行 I/O 密集型目录扫描，避免阻塞 WPF 界面线程</summary>
    /// <param name="folderPath">待扫描的根目录</param>
    /// <param name="cancellationToken">扫描取消标记</param>
    /// <returns>扫描结果任务</returns>
    /// <summary>使用栈进行深度优先遍历，并忽略没有访问权限或已损坏的目录/文件</summary>
    /// <param name="folderPath">待遍历的根目录</param>
    /// <param name="cancellationToken">循环中定期检查的取消标记</param>
    /// <returns>扫描期间成功读取到的文件及文件夹数量</returns>
    /// <summary>目录枚举选项：忽略无权限位置并跳过联接点，避免大盘符扫描进入循环路径</summary>
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    /// <inheritdoc />
    public Task<FolderScanResult> ScanAsync(
        string folderPath,
        IProgress<FileScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(folderPath, progress, cancellationToken), cancellationToken);

    /// <summary>在后台线程单遍扫描目录，并以节流方式上报已发现数据</summary>
    private static FolderScanResult Scan(string folderPath, IProgress<FileScanProgress>? progress, CancellationToken cancellationToken)
    {
        var files = new List<FileItem>();
        var folders = new Stack<string>();
        var categoryTotals = Enum.GetValues<FileCategory>().ToDictionary(category => category, _ => new CategoryScanSummary(0, 0));
        var progressTimer = System.Diagnostics.Stopwatch.StartNew();
        folders.Push(folderPath);
        var folderCount = 0;
        var totalBytes = 0L;

        while (folders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentFolder = folders.Pop();
            folderCount++;

            try
            {
                // 一次枚举同时取得文件和子目录，避免原实现的两次目录 I/O
                foreach (var entry in new DirectoryInfo(currentFolder).EnumerateFileSystemInfos("*", EnumerationOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if ((entry.Attributes & FileAttributes.Directory) != 0)
                        {
                            folders.Push(entry.FullName);
                            continue;
                        }

                        if (entry is not FileInfo info)
                        {
                            continue;
                        }

                        var category = FileCategoryCatalog.GetCategory(info.Extension);
                        files.Add(new FileItem(info.Name, info.FullName, info.Extension, info.Length, info.LastWriteTime, category));
                        totalBytes += info.Length;
                        var current = categoryTotals[category];
                        categoryTotals[category] = new CategoryScanSummary(current.FileCount + 1, current.TotalSize + info.Length);

                        if (progressTimer.ElapsedMilliseconds >= 180)
                        {
                            ReportProgress(progress, folderCount, files.Count, totalBytes, categoryTotals);
                            progressTimer.Restart();
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        ReportProgress(progress, folderCount, files.Count, totalBytes, categoryTotals);
        return new FolderScanResult(folderPath, folderCount, files);
    }

    /// <summary>复制分类统计后再上报，避免 UI 线程读取后台扫描中的可变集合</summary>
    private static void ReportProgress(IProgress<FileScanProgress>? progress, int folders, int files, long bytes, IReadOnlyDictionary<FileCategory, CategoryScanSummary> categories)
    {
        progress?.Report(new FileScanProgress(folders, files, bytes, new Dictionary<FileCategory, CategoryScanSummary>(categories)));
    }

}