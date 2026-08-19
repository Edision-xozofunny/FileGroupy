using FileGroupy.Cache.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileGroupy.Cache;

/// <summary>扫描缓存 SQLite 数据库的 EF Core 上下文与模型映射</summary>
public sealed class ScanCacheDbContext(DbContextOptions<ScanCacheDbContext> options) : DbContext(options)
{
    /// <summary>scan_cache 表的 EF 查询入口</summary>
    public DbSet<ScanCacheEntity> ScanCaches => Set<ScanCacheEntity>();
    /// <summary>scan_files 表的 EF 查询入口</summary>
    public DbSet<ScanFileEntity> ScanFiles => Set<ScanFileEntity>();
    /// <summary>image_validation 表的 EF 查询入口</summary>
    public DbSet<ImageValidationEntity> ImageValidations => Set<ImageValidationEntity>();
    /// <summary>recovery_snapshots 表的 EF 查询入口</summary>
    public DbSet<DeletedFileSnapshotEntity> DeletedFileSnapshots => Set<DeletedFileSnapshotEntity>();
    /// <summary>recovery_files 表的 EF 查询入口</summary>
    public DbSet<DeletedFileSnapshotItemEntity> DeletedFileSnapshotItems => Set<DeletedFileSnapshotItemEntity>();
    public DbSet<RecoverySnapshotCreationEntity> RecoverySnapshotCreations => Set<RecoverySnapshotCreationEntity>();
    public DbSet<RecoverySnapshotRestoreEntity> RecoverySnapshotRestores => Set<RecoverySnapshotRestoreEntity>();
    public DbSet<RecoveryObjectEntity> RecoveryObjects => Set<RecoveryObjectEntity>();
    public DbSet<RecoveryItemChunkEntity> RecoveryItemChunks => Set<RecoveryItemChunkEntity>();

    /// <summary>显式保持与原生批量 SQL 相同的表名、键和索引</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ScanCacheEntity>(entity =>
        {
            entity.ToTable("scan_cache");
            entity.HasKey(item => item.CacheKey);
            entity.HasIndex(item => new { item.SourceKind, item.SourceId, item.RootPath, item.CreatedAt })
                .HasDatabaseName("ix_scan_cache_lookup");
            entity.Property(item => item.CacheKey).HasColumnName("cache_key");
            entity.Property(item => item.SourceKind).HasColumnName("source_kind");
            entity.Property(item => item.SourceId).HasColumnName("source_id");
            entity.Property(item => item.RootPath).HasColumnName("root_path");
            entity.Property(item => item.DisplayPath).HasColumnName("display_path");
            entity.Property(item => item.FolderCount).HasColumnName("folder_count");
            entity.Property(item => item.SkippedItemCount).HasColumnName("skipped_item_count");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.HasMany(item => item.Files).WithOne(item => item.ScanCache)
                .HasForeignKey(item => item.CacheKey).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ScanFileEntity>(entity =>
        {
            entity.ToTable("scan_files");
            entity.HasKey(item => new { item.CacheKey, item.FullPath });
            entity.HasIndex(item => item.SourceId).HasDatabaseName("ix_scan_files_source");
            entity.Property(item => item.CacheKey).HasColumnName("cache_key");
            entity.Property(item => item.Name).HasColumnName("name");
            entity.Property(item => item.FullPath).HasColumnName("full_path");
            entity.Property(item => item.Extension).HasColumnName("extension");
            entity.Property(item => item.Size).HasColumnName("size");
            entity.Property(item => item.LastModified).HasColumnName("last_modified");
            entity.Property(item => item.Category).HasColumnName("category");
            entity.Property(item => item.SourceKind).HasColumnName("source_kind");
            entity.Property(item => item.SourceId).HasColumnName("source_id");
        });

        modelBuilder.Entity<ImageValidationEntity>(entity =>
        {
            entity.ToTable("image_validation");
            entity.HasKey(item => new { item.SourceKind, item.SourceId, item.FullPath, item.Size, item.LastModified });
            entity.Property(item => item.SourceKind).HasColumnName("source_kind");
            entity.Property(item => item.SourceId).HasColumnName("source_id");
            entity.Property(item => item.FullPath).HasColumnName("full_path");
            entity.Property(item => item.Size).HasColumnName("size");
            entity.Property(item => item.LastModified).HasColumnName("last_modified");
            entity.Property(item => item.IsInvalid).HasColumnName("is_invalid");
            entity.Property(item => item.ValidatedAt).HasColumnName("validated_at");
        });

        modelBuilder.Entity<DeletedFileSnapshotEntity>(entity =>
        {
            entity.ToTable("recovery_snapshots");
            entity.HasKey(item => item.SnapshotId);
            entity.Property(item => item.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.Property(item => item.FileCount).HasColumnName("file_count");
            entity.Property(item => item.TotalSize).HasColumnName("total_size");
            entity.Property(item => item.State).HasColumnName("state");
            entity.HasMany(item => item.Files).WithOne(item => item.Snapshot)
                .HasForeignKey(item => item.SnapshotId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeletedFileSnapshotItemEntity>(entity =>
        {
            entity.ToTable("recovery_files");
            entity.HasKey(item => item.ItemId);
            entity.HasIndex(item => item.SnapshotId).HasDatabaseName("ix_recovery_files_snapshot");
            entity.Property(item => item.ItemId).HasColumnName("item_id");
            entity.Property(item => item.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(item => item.FileName).HasColumnName("file_name");
            entity.Property(item => item.OriginalPath).HasColumnName("original_path");
            entity.Property(item => item.RecoveryPath).HasColumnName("recovery_path");
            entity.Property(item => item.Size).HasColumnName("size");
            entity.Property(item => item.LastModified).HasColumnName("last_modified");
            entity.Property(item => item.IsRestored).HasColumnName("is_restored");
            entity.Property(item => item.IsMoved).HasColumnName("is_moved");
        });

        modelBuilder.Entity<RecoverySnapshotCreationEntity>(entity =>
        {
            entity.ToTable("recovery_snapshot_creations");
            entity.HasKey(item => item.CreationId);
            entity.HasIndex(item => item.SnapshotId).HasDatabaseName("ix_recovery_creation_snapshot");
            entity.HasIndex(item => item.CreatedAt).HasDatabaseName("ix_recovery_creation_created_at");
            entity.Property(item => item.CreationId).HasColumnName("creation_id");
            entity.Property(item => item.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.Property(item => item.FileCount).HasColumnName("file_count");
            entity.Property(item => item.TotalSize).HasColumnName("total_size");
        });

        modelBuilder.Entity<RecoverySnapshotRestoreEntity>(entity =>
        {
            entity.ToTable("recovery_snapshot_restores");
            entity.HasKey(item => item.RestoreId);
            entity.HasIndex(item => item.SnapshotId).HasDatabaseName("ix_recovery_restore_snapshot");
            entity.HasIndex(item => item.RestoredAt).HasDatabaseName("ix_recovery_restore_restored_at");
            entity.Property(item => item.RestoreId).HasColumnName("restore_id");
            entity.Property(item => item.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(item => item.RestoredAt).HasColumnName("restored_at");
            entity.Property(item => item.RequestedFileCount).HasColumnName("requested_file_count");
            entity.Property(item => item.SucceededFileCount).HasColumnName("succeeded_file_count");
            entity.Property(item => item.RestoredSize).HasColumnName("restored_size");
            entity.Property(item => item.FailureCount).HasColumnName("failure_count");
            entity.Property(item => item.RestoreAll).HasColumnName("restore_all");
        });

        modelBuilder.Entity<RecoveryObjectEntity>(entity =>
        {
            entity.ToTable("recovery_objects");
            entity.HasKey(item => item.ObjectHash);
            entity.Property(item => item.ObjectHash).HasColumnName("object_hash");
            entity.Property(item => item.StoragePath).HasColumnName("storage_path");
            entity.Property(item => item.Compression).HasColumnName("compression");
            entity.Property(item => item.OriginalSize).HasColumnName("original_size");
            entity.Property(item => item.StoredSize).HasColumnName("stored_size");
            entity.Property(item => item.RefCount).HasColumnName("ref_count");
        });

        modelBuilder.Entity<RecoveryItemChunkEntity>(entity =>
        {
            entity.ToTable("recovery_item_chunks");
            entity.HasKey(item => new { item.ItemId, item.Ordinal });
            entity.HasIndex(item => item.ObjectHash).HasDatabaseName("ix_recovery_item_chunks_object");
            entity.Property(item => item.ItemId).HasColumnName("item_id");
            entity.Property(item => item.Ordinal).HasColumnName("ordinal");
            entity.Property(item => item.ObjectHash).HasColumnName("object_hash");
            entity.Property(item => item.OriginalSize).HasColumnName("original_size");
            entity.HasOne(item => item.RecoveryObject).WithMany()
                .HasForeignKey(item => item.ObjectHash).OnDelete(DeleteBehavior.Restrict);
        });
    }
}