using FileGroupy.Models;

namespace FileGroupy.Services;

/// <summary>定义通过 Windows WPD 协议发现并扫描 MTP/PTP 便携设备的能力</summary>
public interface IMtpDeviceService
{
    /// <summary>获取当前 Windows 可访问的 MTP 或 PTP 设备</summary>
    /// <param name="cancellationToken">取消操作的标记</param>
    /// <returns>可供用户选择的便携设备列表</returns>
    Task<IReadOnlyList<MtpDeviceInfo>> GetAvailablePortableDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>获取设备的虚拟根目录</summary>
    /// <param name="deviceInfo">目标设备</param>
    /// <param name="cancellationToken">取消操作的标记</param>
    /// <returns>设备根目录信息</returns>
    Task<MtpFolderInfo> GetRootFolderAsync(MtpDeviceInfo deviceInfo, CancellationToken cancellationToken = default);

    /// <summary>读取设备中指定目录的直属子目录</summary>
    /// <param name="deviceInfo">目标设备</param>
    /// <param name="parentPath">父目录的虚拟路径</param>
    /// <param name="cancellationToken">取消操作的标记</param>
    /// <returns>直属子目录列表</returns>
    Task<IReadOnlyList<MtpFolderInfo>> GetChildFoldersAsync(MtpDeviceInfo deviceInfo, string parentPath, CancellationToken cancellationToken = default);

    /// <summary>扫描设备中指定目录及其可访问的子目录</summary>
    /// <param name="deviceInfo">目标设备</param>
    /// <param name="rootPath">扫描根目录的虚拟路径</param>
    /// <param name="progress">可选的扫描进度接收器</param>
    /// <param name="cancellationToken">取消操作的标记</param>
    /// <returns>包含文件列表与统计信息的扫描结果</returns>
    Task<FolderScanResult> ScanAsync(
        MtpDeviceInfo deviceInfo,
        string rootPath,
        IProgress<FileScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>忽略缓存, 直接从 MTP/PTP 设备重新扫描指定目录</summary>
    Task<FolderScanResult> RefreshAsync(
        MtpDeviceInfo deviceInfo,
        string rootPath,
        IProgress<FileScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>将设备文件复制或移动到本地目录</summary>
    /// <param name="sourceFiles">设备中的源文件集合</param>
    /// <param name="options">本地目标与冲突处理选项</param>
    /// <param name="progress">可选的传输进度接收器</param>
    /// <param name="cancellationToken">取消操作的标记</param>
    /// <returns>成功, 跳过和失败项的聚合结果</returns>
    Task<FileTransferResult> TransferToLocalAsync(
        IReadOnlyCollection<FileItem> sourceFiles,
        FileTransferOptions options,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>将本地文件复制或移动到设备目录</summary>
    /// <param name="sourceFiles">本地源文件集合</param>
    /// <param name="options">设备目标与冲突处理选项</param>
    /// <param name="progress">可选的传输进度接收器</param>
    /// <param name="cancellationToken">取消操作的标记</param>
    /// <returns>成功, 跳过和失败项的聚合结果</returns>
    Task<FileTransferResult> TransferFromLocalAsync(
        IReadOnlyCollection<FileItem> sourceFiles,
        FileTransferOptions options,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>删除设备中的文件</summary>
    /// <param name="sourceFiles">待删除的设备文件集合</param>
    /// <param name="progress">可选的删除进度接收器</param>
    /// <param name="cancellationToken">取消操作的标记</param>
    /// <returns>成功, 跳过和失败项的聚合结果</returns>
    Task<FileTransferResult> DeleteFilesAsync(
        IReadOnlyCollection<FileItem> sourceFiles,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>使指定设备的扫描缓存失效</summary>
    /// <param name="deviceId">设备标识</param>
    void InvalidateScanCache(string deviceId);

    /// <summary>批量验证设备中的栅格图像, 每个设备使用单一顺序会话以避免协议断连</summary>
    /// <param name="files">待验证的设备图像文件</param>
    /// <param name="cancellationToken">取消操作的标记</param>
    /// <returns>可下载但无法由 WPF 读取的图像路径集合</returns>
    Task<IReadOnlySet<string>> FindInvalidImagePathsAsync(
        IReadOnlyCollection<FileItem> files,
        CancellationToken cancellationToken = default);

    /// <summary>下载单个设备文件到临时目录, 供预览或 Shell 打开</summary>
    /// <param name="file">需要下载的设备文件</param>
    /// <param name="cancellationToken">取消操作的标记</param>
    /// <returns>可由本机 API 读取的临时文件路径</returns>
    Task<string> DownloadPreviewFileAsync(FileItem file, CancellationToken cancellationToken = default);
}
