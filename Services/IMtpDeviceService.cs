using FileGroupy.Models;

namespace FileGroupy.Services;

/// <summary>定义通过 Windows WPD 协议发现并扫描 MTP/PTP 便携设备的能力</summary>
public interface IMtpDeviceService
{
    /// <summary>预检当前可访问的 MTP 手机或 PTP 相机，过滤普通可移动磁盘和无法读取存储目录的设备</summary>
    /// <param name="cancellationToken">用于取消设备连接预检的标记</param>
    /// <returns>已确认可访问存储的便携设备摘要集合</returns>
    Task<IReadOnlyList<MtpDeviceInfo>> GetAvailablePortableDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>获取设备的虚拟根目录，例如“内部共享存储空间”的上级节点</summary>
    /// <param name="deviceInfo">需要访问的 MTP 设备</param>
    /// <param name="cancellationToken">用于取消会话建立的标记</param>
    /// <returns>设备根目录摘要</returns>
    Task<MtpFolderInfo> GetRootFolderAsync(MtpDeviceInfo deviceInfo, CancellationToken cancellationToken = default);

    /// <summary>获取指定 MTP 文件夹直属的子文件夹，用于惰性构建目录树</summary>
    /// <param name="deviceInfo">需要访问的 MTP 设备</param>
    /// <param name="parentPath">父文件夹的设备内完整路径</param>
    /// <param name="cancellationToken">用于取消枚举的标记</param>
    /// <returns>按名称排序的直属子文件夹</returns>
    Task<IReadOnlyList<MtpFolderInfo>> GetChildFoldersAsync(MtpDeviceInfo deviceInfo, string parentPath, CancellationToken cancellationToken = default);

    /// <summary>异步扫描指定设备的共享存储空间</summary>
    /// <param name="deviceInfo">要扫描的 MTP 设备</param>
    /// <param name="progress">可选的实时扫描统计接收器</param>
    /// <param name="cancellationToken">用于停止尚未完成枚举的取消标记</param>
    /// <returns>可复用在现有分类树中的扫描结果</returns>
    Task<FolderScanResult> ScanAsync(
        MtpDeviceInfo deviceInfo,
        string rootPath,
        IProgress<FileScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>将同一 MTP 设备上的文件复制或移动到本地目标目录</summary>
    /// <param name="sourceFiles">来自同一台设备的文件集合</param>
    /// <param name="options">本地目标目录与冲突处理策略</param>
    /// <param name="progress">可选的聚合进度接收器</param>
    /// <param name="cancellationToken">在文件之间检查的取消标记</param>
    /// <returns>本次 MTP 下载操作的结果汇总</returns>
    Task<FileTransferResult> TransferToLocalAsync(
        IReadOnlyCollection<FileItem> sourceFiles,
        FileTransferOptions options,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>将本地文件复制或移动到选定手机的共享存储根目录</summary>
    /// <param name="sourceFiles">位于本地文件系统的源文件集合</param>
    /// <param name="options">包含目标设备标识的传输策略</param>
    /// <param name="progress">可选的聚合进度接收器</param>
    /// <param name="cancellationToken">在文件之间检查的取消标记</param>
    /// <returns>本次上传操作的结果汇总</returns>
    Task<FileTransferResult> TransferFromLocalAsync(
        IReadOnlyCollection<FileItem> sourceFiles,
        FileTransferOptions options,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>批量删除同一设备上的文件，并返回成功与失败明细</summary>
    /// <param name="sourceFiles">待删除的设备文件集合</param>
    /// <param name="progress">可选的删除进度接收器</param>
    /// <param name="cancellationToken">用于中止尚未完成删除的取消标记</param>
    /// <returns>删除结果汇总</returns>
    Task<FileTransferResult> DeleteFilesAsync(
        IReadOnlyCollection<FileItem> sourceFiles,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>按设备标识清理扫描缓存，供删除和跨目录写入后强制刷新</summary>
    /// <param name="deviceId">需要清理缓存的设备标识</param>
    void InvalidateScanCache(string deviceId);

    /// <summary>将手机文件下载至应用临时预览目录，并返回本地可读取路径</summary>
    /// <param name="file">需要下载的 MTP 源文件</param>
    /// <param name="cancellationToken">在下载前检查的取消标记</param>
    /// <returns>临时本地文件的绝对路径</returns>
    Task<string> DownloadPreviewFileAsync(FileItem file, CancellationToken cancellationToken = default);
}