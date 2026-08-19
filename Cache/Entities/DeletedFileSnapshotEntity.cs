namespace FileGroupy.Cache.Entities;

/// <summary>recovery_snapshots 表中一次受管删除操作的元数据</summary>
public sealed class DeletedFileSnapshotEntity
{
    /// <summary>recovery_snapshots.snapshot_id, 删除快照的 GUID 字符串</summary>
    public required string SnapshotId { get; set; }
    /// <summary>recovery_snapshots.created_at, 创建时刻的 Unix 毫秒时间戳</summary>
    public long CreatedAt { get; set; }
    /// <summary>recovery_snapshots.file_count, 当前仍可恢复的文件数量</summary>
    public int FileCount { get; set; }
    /// <summary>recovery_snapshots.total_size, 当前仍可恢复文件的字节总数</summary>
    public long TotalSize { get; set; }
    /// <summary>recovery_snapshots.state, 0 为转移中, 1 为可供用户恢复的已完成快照</summary>
    public int State { get; set; }
    /// <summary>关联的 recovery_files 明细</summary>
    public List<DeletedFileSnapshotItemEntity> Files { get; } = [];
}