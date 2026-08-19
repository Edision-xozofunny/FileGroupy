using FileGroupy.Cache;
using FileGroupy.Cache.Entities;
using FileGroupy.Configuration;
using FileGroupy.Models;
using Microsoft.EntityFrameworkCore;
using System.IO;

namespace FileGroupy.Services;

/// <summary>将本地删除文件转移至受管恢复库, 并通过 SQLite 快照支持后续恢复</summary>
public sealed class DeletedFileRecoveryService(
    IDbContextFactory<ScanCacheDbContext> contextFactory,
    ApplicationOptions options,
    RecoveryObjectStore objectStore) : IDeletedFileRecoveryService
{
    private const int BufferSize = 1024 * 1024;
    private const int PendingState = 0;
    private const int CompletedState = 1;
    private readonly string _recoveryRootDirectory = options.Recovery.LibraryPath;

    /// <inheritdoc />
    public async Task RecoverInterruptedOperationsAsync(CancellationToken cancellationToken = default)
    {
        RecoverySchemaInitializer.EnsureCreated(options);
        await objectStore.ReconcileAsync(cancellationToken);
        using var context = contextFactory.CreateDbContext();
        var pendingSnapshots = await context.DeletedFileSnapshots.Include(item => item.Files)
            .Where(item => item.State == PendingState)
            .ToListAsync(cancellationToken);
        foreach (var snapshot in pendingSnapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in snapshot.Files)
            {
                try
                {
                    if (await objectStore.HasManifestAsync(item.ItemId, cancellationToken))
                    {
                        if (!File.Exists(item.OriginalPath))
                        {
                            await objectStore.RestoreFileAsync(item.ItemId, GetAvailableRestorePath(item.OriginalPath), item.Size, cancellationToken);
                        }
                        await DeleteReleasedObjectsAsync(await objectStore.ReleaseFileAsync(item.ItemId, cancellationToken));
                    }
                    else if (item.IsMoved && File.Exists(item.RecoveryPath))
                    {
                        await MoveToRecoveryLibraryAsync(item.RecoveryPath, GetAvailableRestorePath(item.OriginalPath), cancellationToken);
                    }
                }
                catch (Exception)
                {
                    // 无法回滚的文件保留在恢复库, 快照改为已完成以便用户在 UI 中处理.
                    snapshot.State = CompletedState;
                }
            }

            if (snapshot.State == PendingState)
            {
                context.DeletedFileSnapshots.Remove(snapshot);
                TryDeleteEmptySnapshotDirectory(snapshot.SnapshotId);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        RemoveOrphanRecoveryDirectories(await context.DeletedFileSnapshots.Select(item => item.SnapshotId).ToListAsync(cancellationToken));
    }

    /// <inheritdoc />
    public RecoveryCapacityAssessment AssessCapacity(IReadOnlyCollection<FileItem> files)
    {
        var recoveryRoot = _recoveryRootDirectory;
        Directory.CreateDirectory(recoveryRoot);
        var requiredBytes = files.Sum(file => Math.Max(0, file.Size));
        var root = Path.GetPathRoot(Path.GetFullPath(recoveryRoot))
            ?? throw new IOException("无法确定恢复库所在磁盘");
        var availableBytes = new DriveInfo(root).AvailableFreeSpace;
        return new RecoveryCapacityAssessment(recoveryRoot, requiredBytes, availableBytes, availableBytes >= requiredBytes);
    }

    /// <inheritdoc />
    public async Task<FileTransferResult> SoftDeleteAsync(
        IReadOnlyCollection<FileItem> files,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var snapshotId = Guid.NewGuid().ToString("N");
        var recoveryDirectory = GetSnapshotDirectory(snapshotId);
        Directory.CreateDirectory(recoveryDirectory);
        var snapshot = CreatePendingSnapshot(snapshotId, files, recoveryDirectory);
        SaveSnapshotEntity(snapshot);
        var succeeded = new List<(FileItem Original, string ItemId)>();
        var failures = new List<FileTransferFailure>();
        var successfulPaths = new List<string>();
        var completed = 0;
        var transferredBytes = 0L;
        var totalBytes = files.Sum(file => file.Size);

        foreach (var file in files)
        {
            string? storedItemId = null;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(file.FullPath))
                {
                    throw new FileNotFoundException("源文件已不存在", file.FullPath);
                }

                var itemId = snapshot.Files.Single(item => item.OriginalPath == file.FullPath).ItemId;
                await objectStore.StoreFileAsync(itemId, file.FullPath, cancellationToken);
                storedItemId = itemId;
                File.Delete(file.FullPath);
                if (File.Exists(file.FullPath))
                {
                    throw new IOException("源文件写入恢复对象库后仍无法删除");
                }
                var recoveryPath = $"object://{itemId}";
                succeeded.Add((file, itemId));
                successfulPaths.Add(file.FullPath);
                MarkItemMoved(snapshotId, file.FullPath, recoveryPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (storedItemId is not null)
                {
                    await DeleteReleasedObjectsAsync(await objectStore.ReleaseFileAsync(storedItemId, CancellationToken.None));
                }
                failures.Add(new FileTransferFailure(file.Name, file.FullPath, string.Empty, file.Size, file.SourceKind, exception.Message));
            }
            finally
            {
                completed++;
                transferredBytes += file.Size;
                progress?.Report(new FileTransferProgress(completed, files.Count, transferredBytes, totalBytes));
            }
        }

        if (succeeded.Count > 0)
        {
            try
            {
                MarkSnapshotCompleted(snapshotId, succeeded.Count, succeeded.Sum(item => item.Original.Size));
                SaveSnapshotCreationHistory(snapshotId, succeeded.Count, succeeded.Sum(item => item.Original.Size));
            }
            catch (Exception exception)
            {
                // 快照记录失败时将文件补偿回原位置, 避免恢复库出现无法在 UI 中管理的孤儿文件.
                foreach (var (original, itemId) in succeeded)
                {
                    try
                    {
                        await objectStore.RestoreFileAsync(itemId, GetAvailableRestorePath(original.FullPath), original.Size, cancellationToken);
                        await DeleteReleasedObjectsAsync(await objectStore.ReleaseFileAsync(itemId, cancellationToken));
                        successfulPaths.Remove(original.FullPath);
                    }
                    catch (Exception rollbackException)
                    {
                        failures.Add(new FileTransferFailure(original.Name, $"object://{itemId}", original.FullPath, original.Size, StorageSourceKind.LocalFileSystem, $"快照记录失败且无法自动还原：{rollbackException.Message}"));
                    }
                }

                TryDeleteEmptySnapshotDirectory(snapshotId);
                succeeded.Clear();
                RemoveSnapshot(snapshotId);
                failures.Add(new FileTransferFailure("删除快照", string.Empty, recoveryDirectory, 0, StorageSourceKind.LocalFileSystem, $"删除已取消，快照记录失败：{exception.Message}"));
            }
        }

        return new FileTransferResult(succeeded.Count, 0, failures, successfulPaths, []);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeletedFileSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var context = contextFactory.CreateDbContext();
        var snapshots = await context.DeletedFileSnapshots.AsNoTracking()
            .Include(item => item.Files)
            .Where(item => item.State == CompletedState && item.FileCount > 0)
            .OrderByDescending(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        return snapshots.Select(ToModel).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecoverySnapshotCreationHistory>> GetSnapshotCreationHistoryAsync(CancellationToken cancellationToken = default)
    {
        using var context = contextFactory.CreateDbContext();
        return await context.RecoverySnapshotCreations.AsNoTracking()
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new RecoverySnapshotCreationHistory(
                item.CreationId,
                item.SnapshotId,
                DateTimeOffset.FromUnixTimeMilliseconds(item.CreatedAt),
                item.FileCount,
                item.TotalSize))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecoverySnapshotRestoreHistory>> GetSnapshotRestoreHistoryAsync(CancellationToken cancellationToken = default)
    {
        using var context = contextFactory.CreateDbContext();
        return await context.RecoverySnapshotRestores.AsNoTracking()
            .OrderByDescending(item => item.RestoredAt)
            .Select(item => new RecoverySnapshotRestoreHistory(
                item.RestoreId,
                item.SnapshotId,
                DateTimeOffset.FromUnixTimeMilliseconds(item.RestoredAt),
                item.RequestedFileCount,
                item.SucceededFileCount,
                item.RestoredSize,
                item.FailureCount,
                item.RestoreAll))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<FileTransferResult> RestoreAsync(
        string snapshotId,
        IReadOnlyCollection<string>? itemIds = null,
        string? destinationRoot = null,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(destinationRoot))
        {
            try
            {
                Directory.CreateDirectory(destinationRoot);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                throw new InvalidOperationException($"无法创建指定恢复目录：{destinationRoot}", exception);
            }
        }

        using var context = contextFactory.CreateDbContext();
        var snapshot = await context.DeletedFileSnapshots.Include(item => item.Files)
            .SingleOrDefaultAsync(item => item.SnapshotId == snapshotId, cancellationToken)
            ?? throw new InvalidOperationException("删除快照不存在或已被永久清除");
        var selectedItems = snapshot.Files.Where(item => !item.IsRestored && (itemIds is null || itemIds.Contains(item.ItemId))).ToList();
        var failures = new List<FileTransferFailure>();
        var successfulPaths = new List<string>();
        var completed = 0;
        var transferredBytes = 0L;
        var restoredSize = 0L;
        var totalBytes = selectedItems.Sum(item => item.Size);

        foreach (var item in selectedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var hasManifest = await objectStore.HasManifestAsync(item.ItemId, cancellationToken);
                if (!hasManifest && !File.Exists(item.RecoveryPath))
                {
                    throw new FileNotFoundException("恢复库中的文件已不存在", item.RecoveryPath);
                }

                var requestedPath = string.IsNullOrWhiteSpace(destinationRoot)
                    ? item.OriginalPath
                    : Path.Combine(destinationRoot, item.FileName);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(requestedPath)!);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    throw new InvalidOperationException($"无法创建恢复目录：{Path.GetDirectoryName(requestedPath)}", exception);
                }
                var destinationPath = GetAvailableRestorePath(requestedPath);
                if (hasManifest)
                {
                    await objectStore.RestoreFileAsync(item.ItemId, destinationPath, item.Size, cancellationToken);
                }
                else
                {
                    await MoveToRecoveryLibraryAsync(item.RecoveryPath, destinationPath, cancellationToken);
                }
                item.IsRestored = true;
                successfulPaths.Add(destinationPath);
                restoredSize += item.Size;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add(new FileTransferFailure(item.FileName, item.RecoveryPath, item.OriginalPath, item.Size, StorageSourceKind.LocalFileSystem, exception.Message));
            }
            finally
            {
                completed++;
                transferredBytes += item.Size;
                progress?.Report(new FileTransferProgress(completed, selectedItems.Count, transferredBytes, totalBytes));
            }
        }

        snapshot.FileCount = snapshot.Files.Count(item => !item.IsRestored);
        snapshot.TotalSize = snapshot.Files.Where(item => !item.IsRestored).Sum(item => item.Size);
        await context.SaveChangesAsync(cancellationToken);
        foreach (var item in selectedItems.Where(item => item.IsRestored && item.RecoveryPath.StartsWith("object://", StringComparison.Ordinal)))
        {
            await DeleteReleasedObjectsAsync(await objectStore.ReleaseFileAsync(item.ItemId, cancellationToken));
        }
        await context.RecoverySnapshotRestores.AddAsync(new RecoverySnapshotRestoreEntity
        {
            RestoreId = Guid.NewGuid().ToString("N"),
            SnapshotId = snapshotId,
            RestoredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RequestedFileCount = selectedItems.Count,
            SucceededFileCount = successfulPaths.Count,
            RestoredSize = restoredSize,
            FailureCount = failures.Count,
            RestoreAll = itemIds is null
        }, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return new FileTransferResult(successfulPaths.Count, 0, failures, successfulPaths, []);
    }

    /// <inheritdoc />
    public async Task PermanentlyDeleteSnapshotAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        using var context = contextFactory.CreateDbContext();
        var snapshot = await context.DeletedFileSnapshots.Include(item => item.Files)
            .SingleOrDefaultAsync(item => item.SnapshotId == snapshotId, cancellationToken)
            ?? throw new InvalidOperationException("删除快照不存在或已被永久清除");
        var items = snapshot.Files.Where(item => !item.IsRestored).ToList();
        context.DeletedFileSnapshots.Remove(snapshot);
        await context.SaveChangesAsync(cancellationToken);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.RecoveryPath.StartsWith("object://", StringComparison.Ordinal))
            {
                await DeleteReleasedObjectsAsync(await objectStore.ReleaseFileAsync(item.ItemId, cancellationToken));
            }
            else if (File.Exists(item.RecoveryPath))
            {
                File.Delete(item.RecoveryPath);
            }
        }
        TryDeleteEmptySnapshotDirectory(snapshotId);
    }

    private DeletedFileSnapshotEntity CreatePendingSnapshot(string snapshotId, IReadOnlyCollection<FileItem> files, string recoveryDirectory)
    {
        var snapshot = new DeletedFileSnapshotEntity
        {
            SnapshotId = snapshotId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            FileCount = 0,
            TotalSize = 0,
            State = PendingState
        };
        foreach (var file in files)
        {
            snapshot.Files.Add(new DeletedFileSnapshotItemEntity
            {
                ItemId = Guid.NewGuid().ToString("N"),
                SnapshotId = snapshotId,
                FileName = file.Name,
                OriginalPath = file.FullPath,
                RecoveryPath = Path.Combine(recoveryDirectory, $"pending-{Guid.NewGuid():N}{file.Extension}"),
                Size = file.Size,
                LastModified = file.LastModified.Ticks,
                IsRestored = false,
                IsMoved = false
            });
        }

        return snapshot;
    }

    private void SaveSnapshotEntity(DeletedFileSnapshotEntity snapshot)
    {
        using var context = contextFactory.CreateDbContext();
        context.DeletedFileSnapshots.Add(snapshot);
        context.SaveChanges();
    }

    private void SaveSnapshotCreationHistory(string snapshotId, int fileCount, long totalSize)
    {
        using var context = contextFactory.CreateDbContext();
        context.RecoverySnapshotCreations.Add(new RecoverySnapshotCreationEntity
        {
            CreationId = Guid.NewGuid().ToString("N"),
            SnapshotId = snapshotId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            FileCount = fileCount,
            TotalSize = totalSize
        });
        context.SaveChanges();
    }

    private void MarkItemMoved(string snapshotId, string originalPath, string recoveryPath)
    {
        using var context = contextFactory.CreateDbContext();
        var item = context.DeletedFileSnapshotItems.Single(item => item.SnapshotId == snapshotId && item.OriginalPath == originalPath);
        item.RecoveryPath = recoveryPath;
        item.IsMoved = true;
        context.SaveChanges();
    }

    private void MarkSnapshotCompleted(string snapshotId, int fileCount, long totalSize)
    {
        using var context = contextFactory.CreateDbContext();
        var snapshot = context.DeletedFileSnapshots.Single(item => item.SnapshotId == snapshotId);
        // 单个文件转移失败时不将未移动的计划明细暴露到可恢复快照中。
        context.DeletedFileSnapshotItems
            .Where(item => item.SnapshotId == snapshotId && !item.IsMoved)
            .ExecuteDelete();
        snapshot.FileCount = fileCount;
        snapshot.TotalSize = totalSize;
        snapshot.State = CompletedState;
        context.SaveChanges();
    }

    private void RemoveSnapshot(string snapshotId)
    {
        using var context = contextFactory.CreateDbContext();
        var snapshot = context.DeletedFileSnapshots.SingleOrDefault(item => item.SnapshotId == snapshotId);
        if (snapshot is not null)
        {
            context.DeletedFileSnapshots.Remove(snapshot);
            context.SaveChanges();
        }
    }

    private static DeletedFileSnapshot ToModel(DeletedFileSnapshotEntity entity) => new(
        entity.SnapshotId,
        DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAt),
        entity.FileCount,
        entity.TotalSize,
        entity.Files.Select(item => new DeletedFileSnapshotItem(
            item.ItemId,
            item.FileName,
            item.OriginalPath,
            item.RecoveryPath,
            item.Size,
            new DateTime(item.LastModified),
            item.IsRestored)).ToList());

    private static async Task MoveToRecoveryLibraryAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (string.Equals(Path.GetPathRoot(sourcePath), Path.GetPathRoot(destinationPath), StringComparison.OrdinalIgnoreCase))
        {
            File.Move(sourcePath, destinationPath);
            return;
        }

        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(destination, BufferSize, cancellationToken);
        }

        File.Delete(sourcePath);
    }

    private static string GetAvailableRestorePath(string originalPath)
    {
        if (!File.Exists(originalPath))
        {
            return originalPath;
        }

        var directory = Path.GetDirectoryName(originalPath)!;
        var name = Path.GetFileNameWithoutExtension(originalPath);
        var extension = Path.GetExtension(originalPath);
        for (var suffix = 1; ; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name} (恢复 {suffix}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private string GetSnapshotDirectory(string snapshotId) =>
        Path.Combine(_recoveryRootDirectory, snapshotId);

    private void TryDeleteEmptySnapshotDirectory(string snapshotId)
    {
        var directory = GetSnapshotDirectory(snapshotId);
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }

    private static Task DeleteReleasedObjectsAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return Task.CompletedTask;
    }

    /// <summary>清理空孤儿目录, 并隔离非空孤儿目录以保留待人工核查的文件</summary>
    private void RemoveOrphanRecoveryDirectories(IReadOnlyCollection<string> knownSnapshotIds)
    {
        var recoveryRoot = _recoveryRootDirectory;
        if (!Directory.Exists(recoveryRoot))
        {
            return;
        }

        var knownIds = knownSnapshotIds.ToHashSet(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(recoveryRoot))
        {
            if (knownIds.Contains(Path.GetFileName(directory))
                || string.Equals(Path.GetFileName(directory), "Orphans", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(directory), "objects", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
                continue;
            }

            var orphanRoot = Path.Combine(recoveryRoot, "Orphans");
            Directory.CreateDirectory(orphanRoot);
            var quarantinePath = Path.Combine(orphanRoot, $"{Path.GetFileName(directory)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
            Directory.Move(directory, quarantinePath);
        }
    }
}