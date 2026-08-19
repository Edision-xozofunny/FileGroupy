namespace FileGroupy.Cache.Entities;

/// <summary>recovery_snapshot_creations 表中的快照创建审计记录</summary>
public sealed class RecoverySnapshotCreationEntity
{
    public required string CreationId { get; set; }
    public required string SnapshotId { get; set; }
    public long CreatedAt { get; set; }
    public int FileCount { get; set; }
    public long TotalSize { get; set; }
}