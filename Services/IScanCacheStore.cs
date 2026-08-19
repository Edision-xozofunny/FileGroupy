using FileGroupy.Models;

namespace FileGroupy.Services;

/// <summary>定义扫描结果与图像校验结果的持久化缓存能力</summary>
public interface IScanCacheStore
{
    /// <summary>读取仍在有效期内的扫描结果</summary>
    /// <param name="sourceKind">存储来源类型</param>
    /// <param name="sourceId">存储来源标识, MTP/PTP 使用设备 ID</param>
    /// <param name="rootPath">扫描根路径</param>
    /// <param name="maximumAge">允许复用缓存的最大时长</param>
    /// <returns>命中时返回扫描结果, 否则返回 null</returns>
    FolderScanResult? TryGetScan(StorageSourceKind sourceKind, string sourceId, string rootPath, TimeSpan maximumAge);

    /// <summary>流式读取缓存文件并持续报告读取进度</summary>
    Task<FolderScanResult?> TryGetScanAsync(
        StorageSourceKind sourceKind,
        string sourceId,
        string rootPath,
        TimeSpan maximumAge,
        IProgress<FileScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>在单个事务中保存扫描结果及其文件索引</summary>
    /// <param name="sourceKind">扫描来源类型</param>
    /// <param name="sourceId">存储来源标识</param>
    /// <param name="rootPath">扫描根路径</param>
    /// <param name="result">待缓存的扫描结果</param>
    void StoreScan(
        StorageSourceKind sourceKind,
        string sourceId,
        string rootPath,
        FolderScanResult result,
        CancellationToken cancellationToken = default);

    /// <summary>清除指定来源的扫描与图像校验缓存</summary>
    /// <param name="sourceId">需要失效的来源标识</param>
    void InvalidateSource(string sourceId);

    /// <summary>清除指定扫描根目录的扫描缓存与关联文件索引</summary>
    void InvalidateScan(StorageSourceKind sourceKind, string sourceId, string rootPath);

    /// <summary>批量读取与当前文件元数据仍匹配的图像校验结果</summary>
    /// <param name="files">待查询的图像文件</param>
    /// <param name="maximumAge">允许复用校验结果的最大时长</param>
    /// <returns>已缓存的文件与损坏状态映射</returns>
    IReadOnlyDictionary<FileItem, bool> GetImageValidationStates(IReadOnlyCollection<FileItem> files, TimeSpan maximumAge);

    /// <summary>批量写入图像校验结果</summary>
    /// <param name="states">文件与损坏状态映射</param>
    void StoreImageValidationStates(IReadOnlyDictionary<FileItem, bool> states);
}