namespace FileGroupy.Models;

/// <summary>一次可恢复本地删除操作的快照摘要</summary>
public sealed record DeletedFileSnapshot(
    string SnapshotId,
    DateTimeOffset CreatedAt,
    int FileCount,
    long TotalSize,
    IReadOnlyList<DeletedFileSnapshotItem> Files)
{
    public string TotalSizeText => FileGroupy.ViewModels.SizeFormatter.Format(TotalSize);
}

/// <summary>删除快照中单个文件的原始位置与恢复库位置</summary>
public sealed record DeletedFileSnapshotItem(
    string ItemId,
    string FileName,
    string OriginalPath,
    string RecoveryPath,
    long Size,
    DateTime LastModified,
    bool IsRestored)
{
    public string SizeText => FileGroupy.ViewModels.SizeFormatter.Format(Size);
}

/// <summary>软删除写入恢复库前的磁盘容量评估结果</summary>
public sealed record RecoveryCapacityAssessment(
    string RecoveryRoot,
    long RequiredBytes,
    long AvailableBytes,
    bool HasEnoughSpace);