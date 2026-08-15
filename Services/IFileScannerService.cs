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

    /// <summary>延迟验证本地图片是否可解码</summary>
    /// <param name="files">待验证的本地图像文件</param>
    /// <param name="cancellationToken">取消操作的标记</param>
    /// <returns>无法解码的图像完整路径集合</returns>
    Task<IReadOnlySet<string>> FindInvalidImagePathsAsync(
        IReadOnlyCollection<FileItem> files,
        CancellationToken cancellationToken = default);
}
