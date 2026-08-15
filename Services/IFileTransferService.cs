using FileGroupy.Models;

namespace FileGroupy.Services;

/// <summary>定义可并行执行的批量文件复制和移动能力</summary>
public interface IFileTransferService
{
    /// <summary>按给定选项批量复制或移动文件</summary>
    /// <param name="sourceFiles">源文件集合</param>
    /// <param name="options">目标位置与冲突处理选项</param>
    /// <param name="progress">可选的传输进度接收器</param>
    /// <param name="cancellationToken">取消操作的标记</param>
    /// <returns>成功, 跳过和失败项的聚合结果</returns>
    Task<FileTransferResult> TransferAsync(
        IReadOnlyCollection<FileItem> sourceFiles,
        FileTransferOptions options,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>批量删除指定文件</summary>
    /// <param name="sourceFiles">待删除文件集合</param>
    /// <param name="progress">可选的删除进度接收器</param>
    /// <param name="cancellationToken">取消操作的标记</param>
    /// <returns>成功, 跳过和失败项的聚合结果</returns>
    Task<FileTransferResult> DeleteAsync(
        IReadOnlyCollection<FileItem> sourceFiles,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
