using FileGroupy.Models;

namespace FileGroupy.Services;

/// <summary>定义本地目录递归扫描能力</summary>
public interface IFileScannerService
{
    /// <summary>扫描指定目录及其可访问的子目录</summary>
    /// <param name="folderPath">待扫描的根目录</param>
    /// <param name="progress">可选的扫描进度接收器</param>
    /// <param name="cancellationToken">取消操作的标记</param>
    /// <returns>包含文件列表与统计信息的扫描结果</returns>
    Task<FolderScanResult> ScanAsync(
        string folderPath,
        IProgress<FileScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
