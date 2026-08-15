using System.Collections.Concurrent;
using System.IO;
using FileGroupy.Models;

namespace FileGroupy.Services;

public sealed class FileTransferService(IMtpDeviceService mtpDeviceService) : IFileTransferService
{
    private const int BufferSize = 1024 * 1024;
    private static readonly object DuplicateNameLock = new();
    /// <summary>同时运行的最大文件任务数,按 CPU 核数估算并限制在合理范围</summary>
    private readonly int _maxParallelism = Math.Clamp(Environment.ProcessorCount * 2, 2, 8);

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
        var successfulTransfers = new ConcurrentBag<FileTransferSuccess>();

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
                    successfulTransfers.Add(new FileTransferSuccess(file.FullPath, destination));
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

        return new FileTransferResult(
            succeeded,
            skipped,
            failures.OrderBy(failure => failure.FileName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            successfulSourcePaths.ToList(),
            successfulTransfers.ToList());
    }

    /// <inheritdoc />
    public async Task<FileTransferResult> DeleteAsync(
        IReadOnlyCollection<FileItem> sourceFiles,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (sourceFiles.Count == 0)
        {
            return new FileTransferResult(0, 0, [], [], []);
        }

        var mtpFiles = sourceFiles.Where(file => file.SourceKind == StorageSourceKind.MtpDevice).ToList();
        var localFiles = sourceFiles.Where(file => file.SourceKind == StorageSourceKind.LocalFileSystem).ToList();
        var totalBytes = sourceFiles.Sum(file => file.Size);
        var transferredBytes = 0L;
        var completed = 0;
        var succeeded = 0;
        var skipped = 0;
        var failures = new List<FileTransferFailure>();
        var successfulSourcePaths = new List<string>();
        var successfulTransfers = new List<FileTransferSuccess>();

        void MergeResult(FileTransferResult result, IReadOnlyCollection<FileItem> batchFiles)
        {
            succeeded += result.Succeeded;
            skipped += result.Skipped;
            failures.AddRange(result.Failures);
            successfulSourcePaths.AddRange(result.SuccessfulSourcePaths);
            successfulTransfers.AddRange(result.SuccessfulTransfers ?? []);
            completed += batchFiles.Count;
            transferredBytes += batchFiles.Sum(file => file.Size);
            progress?.Report(new FileTransferProgress(completed, sourceFiles.Count, transferredBytes, totalBytes));
        }

        if (localFiles.Count > 0)
        {
            var localResult = await DeleteLocalAsync(localFiles, cancellationToken);
            MergeResult(localResult, localFiles);
        }

        if (mtpFiles.Count > 0)
        {
            var mtpResult = await mtpDeviceService.DeleteFilesAsync(mtpFiles, null, cancellationToken);
            MergeResult(mtpResult, mtpFiles);
        }

        return new FileTransferResult(succeeded, skipped, failures, successfulSourcePaths, successfulTransfers);
    }

    private static Task<FileTransferResult> DeleteLocalAsync(IReadOnlyCollection<FileItem> sourceFiles, CancellationToken cancellationToken)
    {
        var succeeded = 0;
        var failures = new List<FileTransferFailure>();
        var successfulSourcePaths = new List<string>();
        var successfulTransfers = new List<FileTransferSuccess>();

        foreach (var file in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(file.FullPath))
                {
                    throw new FileNotFoundException("源文件已不存在", file.FullPath);
                }

                File.Delete(file.FullPath);
                if (File.Exists(file.FullPath))
                {
                    throw new IOException("文件删除后仍存在，可能被占用");
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
        }

        return Task.FromResult(new FileTransferResult(succeeded, 0, failures, successfulSourcePaths, successfulTransfers));
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
                // 另一并行任务恰好保留了相同候选名,继续寻找下一个后缀
            }
        }
    }

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

    /// <summary>移动任务仅在源文件已实际删除后才可被报告为成功</summary>
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

    private enum TransferOutcome
    {
        /// <summary>文件已成功复制或移动</summary>
        Succeeded,
        Skipped
    }
}
