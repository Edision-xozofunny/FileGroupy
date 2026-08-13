using System.Collections.Concurrent;
using System.IO;
using FileGroupy.Models;

namespace FileGroupy.Services;

/// <summary>并发执行独立文件操作；并行度受到限制，以平衡磁盘吞吐与文件句柄竞争</summary>
public sealed class FileTransferService(IMtpDeviceService mtpDeviceService) : IFileTransferService
{
    /// <summary>单个复制流的缓冲区大小，1 MiB 可减少大文件复制的系统调用次数</summary>
    private const int BufferSize = 1024 * 1024;
    /// <summary>为并行本地传输分配同名后缀时防止线程同时选择同一个目标名称</summary>
    private static readonly object DuplicateNameLock = new();
    /// <summary>同时运行的最大文件任务数，按 CPU 核数估算并限制在合理范围</summary>
    private readonly int _maxParallelism = Math.Clamp(Environment.ProcessorCount * 2, 2, 8);

    /// <inheritdoc />
    /// <summary>执行一个文件任务，并根据冲突策略复制、移动或跳过该文件</summary>
    /// <param name="sourcePath">源文件绝对路径</param>
    /// <param name="destinationPath">目标文件绝对路径</param>
    /// <param name="options">批量传输策略</param>
    /// <param name="cancellationToken">异步复制的取消标记</param>
    /// <returns>当前文件是否成功或被跳过</returns>
    /// <summary>通过异步流以顺序扫描方式复制一个文件</summary>
    /// <param name="sourcePath">源文件绝对路径</param>
    /// <param name="destinationPath">尚不存在的目标文件绝对路径</param>
    /// <param name="cancellationToken">异步读取和写入的取消标记</param>
    /// <summary>按是否保留目录结构生成某源文件的目标路径</summary>
    /// <param name="file">源文件元数据</param>
    /// <param name="destinationRoot">用户选择的目标根目录</param>
    /// <param name="sourceRoot">全部源文件的共同父目录</param>
    /// <param name="preserveStructure">是否保留共同父目录下的相对路径</param>
    /// <returns>目标文件绝对路径</returns>
    /// <summary>查找多个文件所在目录的最长共同父目录</summary>
    /// <param name="paths">源文件绝对路径集合</param>
    /// <returns>共同父目录；无可用路径时为空字符串</returns>
    public async Task<FileTransferResult> TransferAsync(
        IReadOnlyCollection<FileItem> sourceFiles,
        FileTransferOptions options,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(options.DestinationMtpDeviceId))
        {
            if (sourceFiles.Any(file => file.SourceKind != StorageSourceKind.LocalFileSystem))
            {
                throw new NotSupportedException("暂不支持将手机文件直接传输到另一台手机设备请先复制到本地电脑");
            }

            return await mtpDeviceService.TransferFromLocalAsync(sourceFiles, options, progress, cancellationToken);
        }

        if (sourceFiles.Any(file => file.SourceKind == StorageSourceKind.MtpDevice))
        {
            if (sourceFiles.Any(file => file.SourceKind != StorageSourceKind.MtpDevice))
            {
                throw new NotSupportedException("不能将本地文件和手机文件混合在同一个批量任务中");
            }

            return await mtpDeviceService.TransferToLocalAsync(sourceFiles, options, progress, cancellationToken);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.DestinationPath);
        if (!Directory.Exists(options.DestinationPath))
        {
            throw new DirectoryNotFoundException("目标文件夹不存在");
        }

        var destinationRoot = Path.GetFullPath(options.DestinationPath);
        var sourceRoot = FindCommonDirectory(sourceFiles.Select(file => file.FullPath));
        var totalBytes = sourceFiles.Sum(file => file.Size);
        var transferredBytes = 0L;
        var completedFiles = 0;
        var succeeded = 0;
        var skipped = 0;
        var failures = new ConcurrentBag<FileTransferFailure>();
        var successfulSourcePaths = new ConcurrentBag<string>();

        await Parallel.ForEachAsync(sourceFiles, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = _maxParallelism
        }, async (file, token) =>
        {
            try
            {
                var destination = GetDestinationPath(file, destinationRoot, sourceRoot, options.PreserveSourceStructure);
                var outcome = await TransferOneAsync(file.FullPath, destination, options, token);
                if (outcome == TransferOutcome.Succeeded)
                {
                    Interlocked.Increment(ref succeeded);
                    successfulSourcePaths.Add(file.FullPath);
                }
                else
                {
                    Interlocked.Increment(ref skipped);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var destination = GetDestinationPath(file, destinationRoot, sourceRoot, options.PreserveSourceStructure);
                failures.Add(new FileTransferFailure(file.Name, file.FullPath, destination, file.Size, file.SourceKind, exception.Message));
            }
            finally
            {
                var completed = Interlocked.Increment(ref completedFiles);
                var bytes = Interlocked.Add(ref transferredBytes, file.Size);
                progress?.Report(new FileTransferProgress(completed, sourceFiles.Count, bytes, totalBytes));
            }
        });

        return new FileTransferResult(succeeded, skipped, failures.OrderBy(failure => failure.FileName, StringComparer.CurrentCultureIgnoreCase).ToList(), successfulSourcePaths.ToList());
    }

    private static async Task<TransferOutcome> TransferOneAsync(
        string sourcePath,
        string destinationPath,
        FileTransferOptions options,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("源文件已不存在", sourcePath);
        }

        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
        {
            return TransferOutcome.Skipped;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (options.RenameDuplicates && !options.PreserveSourceStructure)
        {
            return await TransferWithRenamedDestinationAsync(sourcePath, destinationPath, options, cancellationToken);
        }

        if (File.Exists(destinationPath))
        {
            if (options.SkipAllConflicts || !options.OverwriteAll)
            {
                return TransferOutcome.Skipped;
            }

            File.Delete(destinationPath);
        }

        if (options.MoveFiles && string.Equals(Path.GetPathRoot(sourcePath), Path.GetPathRoot(destinationPath), StringComparison.OrdinalIgnoreCase))
        {
            File.Move(sourcePath, destinationPath);
            EnsureLocalSourceWasMoved(sourcePath);
            return TransferOutcome.Succeeded;
        }

        await CopyAsync(sourcePath, destinationPath, cancellationToken);
        if (options.MoveFiles)
        {
            File.Delete(sourcePath);
            EnsureLocalSourceWasMoved(sourcePath);
        }

        return TransferOutcome.Succeeded;
    }

    /// <summary>为扁平复制生成“名称 (1).扩展名”形式的可用目标名，并处理并行任务竞争</summary>
    private static async Task<TransferOutcome> TransferWithRenamedDestinationAsync(
        string sourcePath,
        string destinationPath,
        FileTransferOptions options,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            string candidate;
            lock (DuplicateNameLock)
            {
                candidate = GetAvailableLocalPath(destinationPath, attempt);
            }

            try
            {
                if (options.MoveFiles && string.Equals(Path.GetPathRoot(sourcePath), Path.GetPathRoot(candidate), StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(sourcePath, candidate);
                    EnsureLocalSourceWasMoved(sourcePath);
                }
                else
                {
                    await CopyAsync(sourcePath, candidate, cancellationToken);
                    if (options.MoveFiles)
                    {
                        File.Delete(sourcePath);
                        EnsureLocalSourceWasMoved(sourcePath);
                    }
                }

                return TransferOutcome.Succeeded;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // 另一并行任务恰好保留了相同候选名，继续寻找下一个后缀
            }
        }
    }

    /// <summary>返回首次不存在的本地候选路径；零表示原文件名</summary>
    private static string GetAvailableLocalPath(string path, int minimumSuffix)
    {
        if (minimumSuffix == 0 && !File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var suffix = Math.Max(1, minimumSuffix); ; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name} ({suffix}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, BufferSize, cancellationToken);
    }

    /// <summary>移动任务仅在源文件已实际删除后才可被报告为成功。</summary>
    private static void EnsureLocalSourceWasMoved(string sourcePath)
    {
        if (File.Exists(sourcePath))
        {
            throw new IOException("目标复制完成，但无法删除源文件");
        }
    }

    private static string GetDestinationPath(FileItem file, string destinationRoot, string sourceRoot, bool preserveStructure)
    {
        var relativePath = preserveStructure && !string.IsNullOrEmpty(sourceRoot)
            ? Path.GetRelativePath(sourceRoot, file.FullPath)
            : file.Name;
        return Path.Combine(destinationRoot, relativePath);
    }

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

    /// <summary>单文件传输的内部结果，用于累计批处理统计</summary>
    private enum TransferOutcome
    {
        /// <summary>文件已成功复制或移动</summary>
        Succeeded,
        /// <summary>文件因策略或路径相同而未执行操作</summary>
        Skipped
    }
}