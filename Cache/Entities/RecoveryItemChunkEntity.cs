namespace FileGroupy.Cache.Entities;

/// <summary>恢复文件的有序块清单</summary>
public sealed class RecoveryItemChunkEntity
{
    public required string ItemId { get; set; }
    public int Ordinal { get; set; }
    public required string ObjectHash { get; set; }
    public int OriginalSize { get; set; }
    public RecoveryObjectEntity? RecoveryObject { get; set; }
}