using FileGroupy.Models;

namespace FileGroupy.Services;

/// <summary>定义文件夹递归扫描能力，供界面层通过依赖注入调用</summary>
public interface IFileScannerService
{
    /// <summary>异步扫描指定目录及其可访问的子目录</summary>
    /// <param name="folderPath">待扫描根目录的绝对路径</param>
    /// <param name="progress">可选的进度接收器；使用单遍扫描，因此报告已发现数据而非不可靠的伪百分比</param>
    /// <param name="cancellationToken">用于中止扫描的取消标记</param>
    /// <returns>包含文件夹数量和文件元数据的扫描结果</returns>
    Task<FolderScanResult> ScanAsync(
        string folderPath,
        IProgress<FileScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}