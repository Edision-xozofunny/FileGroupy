namespace FileGroupy.Models;

public sealed record FileScanProgress(
    int FoldersScanned,
    int FilesDiscovered,
    long BytesDiscovered,
    IReadOnlyDictionary<FileCategory, CategoryScanSummary> CategorySummaries,
    FileScanPhase Phase = FileScanPhase.Scanning,
    int TotalFiles = 0,
    FolderScanResult? CachedResult = null);

public enum FileScanPhase
{
    ReadingCache,
    ValidatingCache,
    RefreshingSource,
    Scanning
}

public sealed record CategoryScanSummary(int FileCount, long TotalSize);
