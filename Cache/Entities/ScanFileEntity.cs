namespace FileGroupy.Cache.Entities;

/// <summary>属于某次缓存扫描的单个文件索引项</summary>
public sealed class ScanFileEntity
{
    /// <summary>scan_files.cache_key, 所属扫描快照的外键</summary>
    public required string CacheKey { get; set; }
    /// <summary>scan_files.full_path, 文件在来源中的唯一完整路径</summary>
    public required string FullPath { get; set; }
    /// <summary>scan_files.name, 不含父路径的文件名</summary>
    public required string Name { get; set; }
    /// <summary>scan_files.extension, 包含点号的扩展名</summary>
    public required string Extension { get; set; }
    /// <summary>scan_files.size, 文件大小字节数</summary>
    public long Size { get; set; }
    /// <summary>scan_files.last_modified, DateTime.Ticks 格式的最后修改时间</summary>
    public long LastModified { get; set; }
    /// <summary>scan_files.category, FileCategory 的整数值</summary>
    public int Category { get; set; }
    /// <summary>scan_files.source_kind, StorageSourceKind 的整数值</summary>
    public int SourceKind { get; set; }
    /// <summary>scan_files.source_id, 设备文件使用设备 ID, 本地文件通常为空</summary>
    public string? SourceId { get; set; }
    /// <summary>EF 导航属性, 指向所属扫描缓存元数据</summary>
    public ScanCacheEntity? ScanCache { get; set; }
}