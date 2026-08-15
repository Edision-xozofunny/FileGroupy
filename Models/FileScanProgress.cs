namespace FileGroupy.Models;

public sealed record FileScanProgress(
    int FoldersScanned,
    int FilesDiscovered,
    long BytesDiscovered,
    IReadOnlyDictionary<FileCategory, CategoryScanSummary> CategorySummaries);

public sealed record CategoryScanSummary(int FileCount, long TotalSize);
