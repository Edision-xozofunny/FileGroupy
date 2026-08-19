namespace FileGroupy.Cache.Entities;

/// <summary>内容寻址恢复对象, 每个 BLAKE3 块只保存一次</summary>
public sealed class RecoveryObjectEntity
{
    public required string ObjectHash { get; set; }
    public required string StoragePath { get; set; }
    public int Compression { get; set; }
    public long OriginalSize { get; set; }
    public long StoredSize { get; set; }
    public long RefCount { get; set; }
}