using FileGroupy.Cache;
using FileGroupy.Configuration;
using FileGroupy.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.IO;

namespace FileGroupy.Services;

/// <summary>混合使用 EF Core 查询与原生 SQLite 批量写入的扫描缓存服务</summary>
public sealed class SqliteScanCacheStore : IScanCacheStore
{
    private const string ScanCacheFormatVersion = "v6";
    private readonly IDbContextFactory<ScanCacheDbContext> _contextFactory;
    private readonly string _connectionString;
    private readonly object _initializationLock = new();
    private bool _isInitialized;

    /// <summary>接收 EF Core 上下文工厂, 并复用统一的缓存数据库配置</summary>
    public SqliteScanCacheStore(IDbContextFactory<ScanCacheDbContext> contextFactory, ApplicationOptions options)
    {
        _contextFactory = contextFactory;
        _connectionString = CacheDatabaseOptions.GetConnectionString(options);
    }

    /// <inheritdoc />
    public FolderScanResult? TryGetScan(StorageSourceKind sourceKind, string sourceId, string rootPath, TimeSpan maximumAge)
    {
        try
        {
            EnsureInitialized();
            using var context = _contextFactory.CreateDbContext();
            var cacheKey = CreateScanCacheKey(sourceKind, sourceId, rootPath);
            var minimumCreatedAt = maximumAge == TimeSpan.MaxValue
                ? long.MinValue
                : ToUnixMilliseconds(DateTimeOffset.UtcNow - maximumAge);
            var entity = context.ScanCaches.AsNoTracking()
                .Include(item => item.Files)
                .SingleOrDefault(item => item.CacheKey == cacheKey
                    && item.CreatedAt >= minimumCreatedAt);
            return entity is null ? null : ToScanResult(entity);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            // 缓存不可用时降级为真实扫描, 不阻断用户工作流.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<FolderScanResult?> TryGetScanAsync(
        StorageSourceKind sourceKind,
        string sourceId,
        string rootPath,
        TimeSpan maximumAge,
        IProgress<FileScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureInitialized();
            var expectedCacheKey = CreateScanCacheKey(sourceKind, sourceId, rootPath);
            var minimumCreatedAt = maximumAge == TimeSpan.MaxValue
                ? long.MinValue
                : ToUnixMilliseconds(DateTimeOffset.UtcNow - maximumAge);
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var metadataCommand = connection.CreateCommand();
            metadataCommand.CommandText = """
                SELECT cache_key, display_path, folder_count, skipped_item_count
                FROM scan_cache
                WHERE cache_key = $cacheKey AND created_at >= $minimumCreatedAt
                LIMIT 1;
                """;
            metadataCommand.Parameters.AddWithValue("$cacheKey", expectedCacheKey);
            metadataCommand.Parameters.AddWithValue("$minimumCreatedAt", minimumCreatedAt);
            await using var metadataReader = await metadataCommand.ExecuteReaderAsync(cancellationToken);
            if (!await metadataReader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var cacheKey = metadataReader.GetString(0);
            var displayPath = metadataReader.GetString(1);
            var folderCount = metadataReader.GetInt32(2);
            var skippedItemCount = metadataReader.GetInt32(3);
            await metadataReader.DisposeAsync();

            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM scan_files WHERE cache_key = $cacheKey;";
            countCommand.Parameters.AddWithValue("$cacheKey", cacheKey);
            var totalFiles = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
            var files = new List<FileItem>(totalFiles);
            var categories = Enum.GetValues<FileCategory>()
                .ToDictionary(category => category, _ => new CategoryScanSummary(0, 0));
            var totalBytes = 0L;
            var progressTimer = System.Diagnostics.Stopwatch.StartNew();

            await using var filesCommand = connection.CreateCommand();
            filesCommand.CommandText = """
                SELECT name, full_path, extension, size, last_modified, category, source_kind, source_id
                FROM scan_files WHERE cache_key = $cacheKey;
                """;
            filesCommand.Parameters.AddWithValue("$cacheKey", cacheKey);
            await using var reader = await filesCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var file = new FileItem(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                    new DateTime(reader.GetInt64(4)), (FileCategory)reader.GetInt32(5),
                    (StorageSourceKind)reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7));
                files.Add(file);
                totalBytes += file.Size;
                var summary = categories[file.Category];
                categories[file.Category] = new CategoryScanSummary(summary.FileCount + 1, summary.TotalSize + file.Size);
                if (progressTimer.ElapsedMilliseconds >= 250)
                {
                    progress?.Report(new FileScanProgress(folderCount, files.Count, totalBytes,
                        new Dictionary<FileCategory, CategoryScanSummary>(categories), FileScanPhase.ReadingCache, totalFiles));
                    progressTimer.Restart();
                }
            }

            var result = new FolderScanResult(displayPath, folderCount, files, skippedItemCount);
            progress?.Report(new FileScanProgress(folderCount, files.Count, totalBytes, categories,
                FileScanPhase.ReadingCache, totalFiles, result));
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void StoreScan(StorageSourceKind sourceKind, string sourceId, string rootPath, FolderScanResult result, CancellationToken cancellationToken = default)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureInitialized();
                var cacheKey = CreateScanCacheKey(sourceKind, sourceId, rootPath);
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                DeleteScanFiles(connection, transaction, cacheKey);
                UpsertScanMetadata(connection, transaction, cacheKey, sourceKind, sourceId, rootPath, result);
                InsertScanFiles(connection, transaction, cacheKey, result.Files, cancellationToken);
                transaction.Commit();
                return;
            }
            catch (SqliteException exception) when (attempt < maxAttempts && IsTransient(exception))
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
            }
            catch (SqliteException exception)
            {
                System.Diagnostics.Trace.WriteLine($"FileGroupy cache write skipped: {exception.Message}");
                return;
            }
            catch (IOException exception)
            {
                System.Diagnostics.Trace.WriteLine($"FileGroupy cache write skipped: {exception.Message}");
                return;
            }
        }
    }

    private static bool IsTransient(SqliteException exception) =>
        exception.SqliteErrorCode == 5
        || exception.SqliteErrorCode == 6
        || exception.Message.Contains("busy", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("locked", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void InvalidateSource(string sourceId)
    {
        try
        {
            EnsureInitialized();
            using var context = _contextFactory.CreateDbContext();
            using var transaction = context.Database.BeginTransaction();
            context.ImageValidations.Where(item => item.SourceId == sourceId).ExecuteDelete();
            context.ScanFiles.Where(item => item.SourceId == sourceId).ExecuteDelete();
            context.ScanCaches.Where(item => item.SourceId == sourceId).ExecuteDelete();
            transaction.Commit();
        }
        catch (SqliteException)
        {
            // 失效失败最多导致短期命中旧缓存, 不应中断文件操作。
        }
    }

    /// <inheritdoc />
    public void InvalidateScan(StorageSourceKind sourceKind, string sourceId, string rootPath)
    {
        try
        {
            EnsureInitialized();
            using var context = _contextFactory.CreateDbContext();
            var cacheKeys = context.ScanCaches
                .Where(item => item.SourceKind == (int)sourceKind
                    && item.SourceId == sourceId
                    && item.RootPath == rootPath)
                .Select(item => item.CacheKey)
                .ToList();
            if (cacheKeys.Count == 0)
            {
                return;
            }

            using var transaction = context.Database.BeginTransaction();
            context.ScanFiles.Where(item => cacheKeys.Contains(item.CacheKey)).ExecuteDelete();
            context.ScanCaches.Where(item => cacheKeys.Contains(item.CacheKey)).ExecuteDelete();
            transaction.Commit();
        }
        catch (SqliteException)
        {
            // 缓存失效失败不影响已完成的文件删除.
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<FileItem, bool> GetImageValidationStates(IReadOnlyCollection<FileItem> files, TimeSpan maximumAge)
    {
        if (files.Count == 0)
        {
            return new Dictionary<FileItem, bool>();
        }

        try
        {
            EnsureInitialized();
            using var context = _contextFactory.CreateDbContext();
            var minimumValidatedAt = ToUnixMilliseconds(DateTimeOffset.UtcNow - maximumAge);
            var states = new Dictionary<FileItem, bool>();
            foreach (var file in files)
            {
                var sourceId = file.SourceId ?? string.Empty;
                var entity = context.ImageValidations.AsNoTracking().SingleOrDefault(item =>
                    item.SourceKind == (int)file.SourceKind
                    && item.SourceId == sourceId
                    && item.FullPath == file.FullPath
                    && item.Size == file.Size
                    && item.LastModified == file.LastModified.Ticks
                    && item.ValidatedAt >= minimumValidatedAt);
                if (entity is not null)
                {
                    states[file] = entity.IsInvalid;
                }
            }

            return states;
        }
        catch (SqliteException)
        {
            return new Dictionary<FileItem, bool>();
        }
    }

    /// <inheritdoc />
    public void StoreImageValidationStates(IReadOnlyDictionary<FileItem, bool> states)
    {
        if (states.Count == 0)
        {
            return;
        }

        try
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO image_validation(source_kind, source_id, full_path, size, last_modified, is_invalid, validated_at)
                VALUES($sourceKind, $sourceId, $fullPath, $size, $lastModified, $isInvalid, $validatedAt)
                ON CONFLICT(source_kind, source_id, full_path, size, last_modified) DO UPDATE SET
                    is_invalid = excluded.is_invalid,
                    validated_at = excluded.validated_at;
                """;
            var sourceKind = command.Parameters.Add("$sourceKind", SqliteType.Integer);
            var sourceId = command.Parameters.Add("$sourceId", SqliteType.Text);
            var fullPath = command.Parameters.Add("$fullPath", SqliteType.Text);
            var size = command.Parameters.Add("$size", SqliteType.Integer);
            var lastModified = command.Parameters.Add("$lastModified", SqliteType.Integer);
            var isInvalid = command.Parameters.Add("$isInvalid", SqliteType.Integer);
            var validatedAt = command.Parameters.Add("$validatedAt", SqliteType.Integer);
            validatedAt.Value = ToUnixMilliseconds(DateTimeOffset.UtcNow);
            foreach (var (file, invalid) in states)
            {
                sourceKind.Value = (int)file.SourceKind;
                sourceId.Value = file.SourceId ?? string.Empty;
                fullPath.Value = file.FullPath;
                size.Value = file.Size;
                lastModified.Value = file.LastModified.Ticks;
                isInvalid.Value = invalid ? 1 : 0;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (SqliteException)
        {
            // 校验状态缓存失败时, 下次按需重新校验即可.
        }
    }

    /// <summary>由 EF Core 创建结构, 再配置原生批量写路径需要的 SQLite 行为</summary>
    private void EnsureInitialized()
    {
        if (_isInitialized)
        {
            return;
        }

        lock (_initializationLock)
        {
            if (_isInitialized)
            {
                return;
            }

            using var context = _contextFactory.CreateDbContext();
            context.Database.EnsureCreated();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;

                """;
            command.ExecuteNonQuery();
            _isInitialized = true;
        }
    }

    /// <summary>删除当前缓存键的文件索引, 为批量写新扫描快照做准备</summary>
    private static void DeleteScanFiles(SqliteConnection connection, SqliteTransaction transaction, string cacheKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM scan_files WHERE cache_key = $cacheKey;";
        command.Parameters.AddWithValue("$cacheKey", cacheKey);
        command.ExecuteNonQuery();
    }

    /// <summary>使用 UPSERT 写入扫描元数据, 唯一缓存键保证幂等性</summary>
    private static void UpsertScanMetadata(SqliteConnection connection, SqliteTransaction transaction, string cacheKey, StorageSourceKind sourceKind, string sourceId, string rootPath, FolderScanResult result)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO scan_cache(cache_key, source_kind, source_id, root_path, display_path, folder_count, skipped_item_count, created_at)
            VALUES($cacheKey, $sourceKind, $sourceId, $rootPath, $displayPath, $folderCount, $skippedItemCount, $createdAt)
            ON CONFLICT(cache_key) DO UPDATE SET
                source_kind = excluded.source_kind,
                display_path = excluded.display_path,
                folder_count = excluded.folder_count,
                skipped_item_count = excluded.skipped_item_count,
                created_at = excluded.created_at;
            """;
        command.Parameters.AddWithValue("$cacheKey", cacheKey);
        command.Parameters.AddWithValue("$sourceKind", (int)sourceKind);
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.Parameters.AddWithValue("$rootPath", rootPath);
        command.Parameters.AddWithValue("$displayPath", result.Path);
        command.Parameters.AddWithValue("$folderCount", result.FolderCount);
        command.Parameters.AddWithValue("$skippedItemCount", result.SkippedItemCount);
        command.Parameters.AddWithValue("$createdAt", ToUnixMilliseconds(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    /// <summary>在单事务中复用参数化命令写入大量文件索引</summary>
    private static void InsertScanFiles(SqliteConnection connection, SqliteTransaction transaction, string cacheKey, IReadOnlyList<FileItem> files, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO scan_files(cache_key, name, full_path, extension, size, last_modified, category, source_kind, source_id)
            VALUES($cacheKey, $name, $fullPath, $extension, $size, $lastModified, $category, $sourceKind, $sourceId);
            """;
        var cacheKeyParameter = command.Parameters.Add("$cacheKey", SqliteType.Text);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var fullPath = command.Parameters.Add("$fullPath", SqliteType.Text);
        var extension = command.Parameters.Add("$extension", SqliteType.Text);
        var size = command.Parameters.Add("$size", SqliteType.Integer);
        var lastModified = command.Parameters.Add("$lastModified", SqliteType.Integer);
        var category = command.Parameters.Add("$category", SqliteType.Integer);
        var sourceKind = command.Parameters.Add("$sourceKind", SqliteType.Integer);
        var sourceId = command.Parameters.Add("$sourceId", SqliteType.Text);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cacheKeyParameter.Value = cacheKey;
            name.Value = file.Name;
            fullPath.Value = file.FullPath;
            extension.Value = file.Extension;
            size.Value = file.Size;
            lastModified.Value = file.LastModified.Ticks;
            category.Value = (int)file.Category;
            sourceKind.Value = (int)file.SourceKind;
            sourceId.Value = (object?)file.SourceId ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static FolderScanResult ToScanResult(Cache.Entities.ScanCacheEntity entity)
    {
        var files = entity.Files.Select(file => new FileItem(
            file.Name,
            file.FullPath,
            file.Extension,
            file.Size,
            new DateTime(file.LastModified),
            (FileCategory)file.Category,
            (StorageSourceKind)file.SourceKind,
            file.SourceId)).ToList();
        return new FolderScanResult(entity.DisplayPath, entity.FolderCount, files, entity.SkippedItemCount);
    }

    private static string CreateScanCacheKey(StorageSourceKind sourceKind, string sourceId, string rootPath) =>
        $"{ScanCacheFormatVersion}\n{(int)sourceKind}\n{sourceId}\n{rootPath}";

    private static long ToUnixMilliseconds(DateTimeOffset value) => value.ToUnixTimeMilliseconds();
}