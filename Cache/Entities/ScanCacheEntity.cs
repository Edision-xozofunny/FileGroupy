namespace FileGroupy.Cache.Entities;

/// <summary>一次扫描结果的缓存元数据</summary>
public sealed class ScanCacheEntity
{
    /// <summary>scan_cache.cache_key, 由来源类型、来源标识和根路径组成的稳定唯一键</summary>
    public required string CacheKey { get; set; }
    /// <summary>scan_cache.source_kind, StorageSourceKind 的整数值</summary>
    public int SourceKind { get; set; }
    /// <summary>scan_cache.source_id, 本地扫描为卷根路径, MTP/PTP 扫描为设备 ID</summary>
    public required string SourceId { get; set; }
    /// <summary>scan_cache.root_path, 用户实际选择并扫描的根路径</summary>
    public required string RootPath { get; set; }
    /// <summary>scan_cache.display_path, 用于界面展示的来源文本</summary>
    public required string DisplayPath { get; set; }
    /// <summary>scan_cache.folder_count, 扫描到的目录数量</summary>
    public int FolderCount { get; set; }
    /// <summary>scan_cache.skipped_item_count, 因权限或协议错误跳过的项数</summary>
    public int SkippedItemCount { get; set; }
    /// <summary>scan_cache.created_at, 缓存写入时刻的 Unix 毫秒时间戳</summary>
    public long CreatedAt { get; set; }
    /// <summary>关联的 scan_files 行, EF Core 读取扫描缓存时一并加载</summary>
    public List<ScanFileEntity> Files { get; } = [];
}