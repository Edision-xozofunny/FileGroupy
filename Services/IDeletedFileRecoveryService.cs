using FileGroupy.Models;

namespace FileGroupy.Services;

/// <summary>定义本地删除文件的恢复库、快照管理和恢复能力</summary>
public interface IDeletedFileRecoveryService
{
    /// <summary>启动时回滚未完成删除并移除无引用的恢复库目录</summary>
    Task RecoverInterruptedOperationsAsync(CancellationToken cancellationToken = default);

    /// <summary>计算本次软删除在恢复库磁盘上需要的额外空间</summary>
    /// <param name="files">待删除的本地文件</param>
    /// <returns>恢复库位置、预估需求和可用空间</returns>
    RecoveryCapacityAssessment AssessCapacity(IReadOnlyCollection<FileItem> files);

    /// <summary>将本地文件转移到应用恢复库并创建 SQLite 删除快照</summary>
    Task<FileTransferResult> SoftDeleteAsync(
        IReadOnlyCollection<FileItem> files,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>读取所有删除快照及其明细</summary>
    Task<IReadOnlyList<DeletedFileSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken = default);

    /// <summary>查询快照创建历史</summary>
    Task<IReadOnlyList<RecoverySnapshotCreationHistory>> GetSnapshotCreationHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>查询快照恢复历史</summary>
    Task<IReadOnlyList<RecoverySnapshotRestoreHistory>> GetSnapshotRestoreHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>恢复指定快照中的全部或部分文件到原始位置</summary>
    Task<FileTransferResult> RestoreAsync(
        string snapshotId,
        IReadOnlyCollection<string>? itemIds = null,
        string? destinationRoot = null,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>永久清除一个删除快照及其恢复库文件, 此操作不可逆</summary>
    Task PermanentlyDeleteSnapshotAsync(string snapshotId, CancellationToken cancellationToken = default);
}