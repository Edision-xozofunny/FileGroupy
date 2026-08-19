namespace FileGroupy.Cache.Entities;

/// <summary>recovery_snapshot_restores 表中的快照恢复审计记录</summary>
public sealed class RecoverySnapshotRestoreEntity
{
    public required string RestoreId { get; set; }
    public required string SnapshotId { get; set; }
    public long RestoredAt { get; set; }
    public int RequestedFileCount { get; set; }
    public int SucceededFileCount { get; set; }
    public long RestoredSize { get; set; }
    public int FailureCount { get; set; }
    public bool RestoreAll { get; set; }
}