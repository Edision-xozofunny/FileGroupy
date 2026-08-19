using System.IO;
using Blake3;
using FastCdc.Net;
using FileGroupy.Cache;
using FileGroupy.Cache.Entities;
using Microsoft.EntityFrameworkCore;
using ZstdSharp;

namespace FileGroupy.Services;

/// <summary>基于内容寻址、分块去重和自适应压缩的恢复对象库</summary>
public sealed class RecoveryObjectStore(
    IDbContextFactory<ScanCacheDbContext> contextFactory,
    Configuration.ApplicationOptions options)
{
    private const int SegmentSize = 32 * 1024 * 1024;
    private const uint MinimumChunkSize = 256 * 1024;
    private const uint AverageChunkSize = 1024 * 1024;
    private const uint MaximumChunkSize = 4 * 1024 * 1024;
    private const int CompressionNone = 0;
    private const int CompressionZstd = 1;
    private readonly string _objectRoot = Path.Combine(options.Recovery.LibraryPath, "objects");

    private static readonly HashSet<string> AlreadyCompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".mp3", ".aac", ".flac",
        ".mp4", ".mkv", ".mov", ".avi", ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz",
        ".docx", ".xlsx", ".pptx", ".pdf"
    };

    /// <summary>保存文件块并在同一事务内创建清单与引用</summary>
    public async Task StoreFileAsync(string itemId, string sourcePath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_objectRoot);
        var extension = Path.GetExtension(sourcePath);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, SegmentSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var segment = new byte[SegmentSize];
        var ordinal = 0;
        do
        {
            var read = 0;
            while (read < segment.Length)
            {
                var count = await source.ReadAsync(segment.AsMemory(read, segment.Length - read), cancellationToken);
                if (count == 0) break;
                read += count;
            }

            if (read == 0 && ordinal > 0) break;
            var data = read == segment.Length ? segment : segment[..read];
            IEnumerable<Chunk> chunks = read == 0
                ? [new Chunk(0, 0, 0)]
                : new FastCdc.Net.FastCdc(data, MinimumChunkSize, AverageChunkSize, MaximumChunkSize, true).GetChunks();
            foreach (var descriptor in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = data.AsSpan((int)descriptor.Offset, (int)descriptor.Length).ToArray();
                var hash = HashChunk(chunk);
                var entity = context.RecoveryObjects.Local.FirstOrDefault(item => item.ObjectHash == hash)
                    ?? await context.RecoveryObjects.SingleOrDefaultAsync(item => item.ObjectHash == hash, cancellationToken);
                if (entity is null)
                {
                    var stored = await WriteObjectAsync(hash, chunk, !AlreadyCompressedExtensions.Contains(extension), cancellationToken);
                    entity = new RecoveryObjectEntity
                    {
                        ObjectHash = hash,
                        StoragePath = stored.Path,
                        Compression = stored.Compression,
                        OriginalSize = chunk.Length,
                        StoredSize = stored.StoredSize,
                        RefCount = 0
                    };
                    context.RecoveryObjects.Add(entity);
                }

                entity.RefCount++;
                context.RecoveryItemChunks.Add(new RecoveryItemChunkEntity
                {
                    ItemId = itemId,
                    Ordinal = ordinal++,
                    ObjectHash = hash,
                    OriginalSize = chunk.Length
                });
            }

            if (read < segment.Length) break;
            segment = new byte[SegmentSize];
        } while (true);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>按清单顺序恢复文件并校验最终字节数</summary>
    public async Task RestoreFileAsync(string itemId, string destinationPath, long expectedSize, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var chunks = await context.RecoveryItemChunks.AsNoTracking()
            .Where(item => item.ItemId == itemId)
            .OrderBy(item => item.Ordinal)
            .Join(context.RecoveryObjects.AsNoTracking(), chunk => chunk.ObjectHash, obj => obj.ObjectHash,
                (chunk, obj) => new { chunk.OriginalSize, obj.StoragePath, obj.Compression })
            .ToListAsync(cancellationToken);
        if (chunks.Count == 0)
        {
            throw new InvalidDataException("恢复对象清单不存在");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var source = new FileStream(chunk.StoragePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (chunk.Compression == CompressionZstd)
            {
                await using var decompressor = new DecompressionStream(source, 1024 * 1024, leaveOpen: false, checkEndOfStream: true);
                await decompressor.CopyToAsync(destination, cancellationToken);
            }
            else
            {
                await source.CopyToAsync(destination, cancellationToken);
            }
        }

        await destination.FlushAsync(cancellationToken);
        if (destination.Length != expectedSize)
        {
            throw new InvalidDataException("恢复文件大小校验失败");
        }
    }

    /// <summary>释放文件清单引用并返回不再被使用的对象路径</summary>
    public async Task<IReadOnlyList<string>> ReleaseFileAsync(string itemId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var chunks = await context.RecoveryItemChunks.Where(item => item.ItemId == itemId).ToListAsync(cancellationToken);
        var pathsToDelete = new List<string>();
        foreach (var group in chunks.GroupBy(item => item.ObjectHash))
        {
            var entity = await context.RecoveryObjects.SingleAsync(item => item.ObjectHash == group.Key, cancellationToken);
            entity.RefCount -= group.Count();
            if (entity.RefCount <= 0)
            {
                pathsToDelete.Add(entity.StoragePath);
                context.RecoveryObjects.Remove(entity);
            }
        }

        context.RecoveryItemChunks.RemoveRange(chunks);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return pathsToDelete;
    }

    public async Task<bool> HasManifestAsync(string itemId, CancellationToken cancellationToken) =>
        await WithContextAsync(context => context.RecoveryItemChunks.AnyAsync(item => item.ItemId == itemId, cancellationToken));

    /// <summary>启动时校正引用计数并移除数据库未引用的对象/临时文件</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_objectRoot);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var staleChunks = await context.RecoveryItemChunks
            .Where(chunk => !context.DeletedFileSnapshotItems.Any(item =>
                item.ItemId == chunk.ItemId && !item.IsRestored))
            .ToListAsync(cancellationToken);
        if (staleChunks.Count > 0)
        {
            context.RecoveryItemChunks.RemoveRange(staleChunks);
            await context.SaveChangesAsync(cancellationToken);
        }

        var expectedCounts = await context.RecoveryItemChunks
            .GroupBy(item => item.ObjectHash)
            .Select(group => new { Hash = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(item => item.Hash, item => item.Count, cancellationToken);
        var objects = await context.RecoveryObjects.ToListAsync(cancellationToken);
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in objects)
        {
            if (!expectedCounts.TryGetValue(entity.ObjectHash, out var count) || count == 0)
            {
                context.RecoveryObjects.Remove(entity);
                continue;
            }
            entity.RefCount = count;
            knownPaths.Add(Path.GetFullPath(entity.StoragePath));
        }
        await context.SaveChangesAsync(cancellationToken);

        foreach (var path in Directory.EnumerateFiles(_objectRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path.Contains(".tmp-", StringComparison.OrdinalIgnoreCase) || !knownPaths.Contains(Path.GetFullPath(path)))
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private async Task<T> WithContextAsync<T>(Func<ScanCacheDbContext, Task<T>> operation)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await operation(context);
    }

    private static string HashChunk(byte[] data)
    {
        using var hasher = Hasher.New();
        hasher.Update(data);
        return hasher.Finalize().ToString();
    }

    private async Task<(string Path, int Compression, long StoredSize)> WriteObjectAsync(string hash, byte[] data, bool allowCompression, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_objectRoot, hash[..2], hash.Substring(2, 2));
        Directory.CreateDirectory(directory);
        byte[] storedData = data;
        var compression = CompressionNone;
        if (allowCompression && data.Length >= 4096)
        {
            using var compressor = new Compressor(3);
            var compressed = compressor.Wrap(data).ToArray();
            if (compressed.Length + 64 < data.Length * 0.95)
            {
                storedData = compressed;
                compression = CompressionZstd;
            }
        }

        var path = Path.Combine(directory, hash + (compression == CompressionZstd ? ".zst" : ".raw"));
        if (!File.Exists(path))
        {
            var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllBytesAsync(temporaryPath, storedData, cancellationToken);
            try { File.Move(temporaryPath, path); }
            catch (IOException) when (File.Exists(path)) { File.Delete(temporaryPath); }
        }

        return (path, compression, storedData.Length);
    }
}