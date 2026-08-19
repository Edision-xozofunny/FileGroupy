namespace FileGroupy.Models;

/// <summary>删除快照创建历史</summary>
public sealed record RecoverySnapshotCreationHistory(
    string CreationId,
    string SnapshotId,
    DateTimeOffset CreatedAt,
    int FileCount,
    long TotalSize)
{
    public string TotalSizeText => FileGroupy.ViewModels.SizeFormatter.Format(TotalSize);
}

/// <summary>删除快照恢复历史</summary>
public sealed record RecoverySnapshotRestoreHistory(
    string RestoreId,
    string SnapshotId,
    DateTimeOffset RestoredAt,
    int RequestedFileCount,
    int SucceededFileCount,
    long RestoredSize,
    int FailureCount,
    bool RestoreAll)
{
    public string RestoredSizeText => FileGroupy.ViewModels.SizeFormatter.Format(RestoredSize);
}