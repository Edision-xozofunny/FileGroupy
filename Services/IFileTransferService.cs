using FileGroupy.Models;

namespace FileGroupy.Services;

/// <summary>定义可并行执行的批量文件复制和移动能力</summary>
public interface IFileTransferService
{
    /// <summary>按照指定策略将文件集合复制或移动到目标目录</summary>
    /// <param name="sourceFiles">需要处理的源文件集合</param>
    /// <param name="options">目标目录、重名冲突和移动策略</param>
    /// <param name="progress">可选的聚合进度接收器</param>
    /// <param name="cancellationToken">用于中止尚未完成任务的取消标记</param>
    /// <returns>本次操作的成功、跳过和失败汇总</returns>
    Task<FileTransferResult> TransferAsync(
        IReadOnlyCollection<FileItem> sourceFiles,
        FileTransferOptions options,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>按来源类型批量删除文件，并返回成功、跳过和失败明细</summary>
    /// <param name="sourceFiles">待删除文件集合</param>
    /// <param name="progress">可选的聚合进度接收器</param>
    /// <param name="cancellationToken">用于中止尚未开始项的取消标记</param>
    /// <returns>删除操作结果汇总</returns>
    Task<FileTransferResult> DeleteAsync(
        IReadOnlyCollection<FileItem> sourceFiles,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
}