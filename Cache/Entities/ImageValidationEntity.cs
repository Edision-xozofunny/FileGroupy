namespace FileGroupy.Cache.Entities;

/// <summary>与文件元数据绑定的图像有效性校验结果</summary>
public sealed class ImageValidationEntity
{
    /// <summary>image_validation.source_kind, StorageSourceKind 的整数值</summary>
    public int SourceKind { get; set; }
    /// <summary>image_validation.source_id, 本地文件为空字符串, 设备文件为设备 ID</summary>
    public required string SourceId { get; set; }
    /// <summary>image_validation.full_path, 校验文件的完整路径</summary>
    public required string FullPath { get; set; }
    /// <summary>image_validation.size, 用于识别内容可能变化的文件大小</summary>
    public long Size { get; set; }
    /// <summary>image_validation.last_modified, DateTime.Ticks 格式的最后修改时间</summary>
    public long LastModified { get; set; }
    /// <summary>image_validation.is_invalid, true 表示文件无法通过快速栅格图像校验</summary>
    public bool IsInvalid { get; set; }
    /// <summary>image_validation.validated_at, 最近一次校验时刻的 Unix 毫秒时间戳</summary>
    public long ValidatedAt { get; set; }
}