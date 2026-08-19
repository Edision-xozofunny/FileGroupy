namespace FileGroupy.Cache.Entities;

/// <summary>recovery_files 表中可恢复文件的物理位置与状态</summary>
public sealed class DeletedFileSnapshotItemEntity
{
    /// <summary>recovery_files.item_id, 文件明细的 GUID 字符串</summary>
    public required string ItemId { get; set; }
    /// <summary>recovery_files.snapshot_id, 所属删除快照的外键</summary>
    public required string SnapshotId { get; set; }
    /// <summary>recovery_files.file_name, 原始文件名</summary>
    public required string FileName { get; set; }
    /// <summary>recovery_files.original_path, 恢复时默认写回的位置</summary>
    public required string OriginalPath { get; set; }
    /// <summary>recovery_files.recovery_path, 应用恢复库中的受管文件路径</summary>
    public required string RecoveryPath { get; set; }
    /// <summary>recovery_files.size, 文件大小字节数</summary>
    public long Size { get; set; }
    /// <summary>recovery_files.last_modified, 原文件最后修改时间的 Ticks</summary>
    public long LastModified { get; set; }
    /// <summary>recovery_files.is_restored, 文件是否已经恢复到原始位置</summary>
    public bool IsRestored { get; set; }
    /// <summary>recovery_files.is_moved, 文件是否已经成功转移到恢复库</summary>
    public bool IsMoved { get; set; }
    /// <summary>EF 导航属性, 指向所属删除快照</summary>
    public DeletedFileSnapshotEntity? Snapshot { get; set; }
}