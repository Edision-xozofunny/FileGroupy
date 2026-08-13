namespace FileGroupy.Models;

/// <summary>单遍扫描过程中的实时统计快照，用于显示不阻塞扫描的活动进度</summary>
/// <param name="FoldersScanned">已完成枚举的目录数量</param>
/// <param name="FilesDiscovered">已发现的文件数量</param>
/// <param name="BytesDiscovered">已发现文件的累计字节数</param>
/// <param name="CategorySummaries">按文件分类统计的当前快照</param>
public sealed record FileScanProgress(
    int FoldersScanned,
    int FilesDiscovered,
    long BytesDiscovered,
    IReadOnlyDictionary<FileCategory, CategoryScanSummary> CategorySummaries);

/// <summary>扫描过程中某个分类的文件数量与总大小</summary>
/// <param name="FileCount">当前已发现的文件数量</param>
/// <param name="TotalSize">当前已发现文件的总大小，单位为字节</param>
public sealed record CategoryScanSummary(int FileCount, long TotalSize);