namespace FileGroupy.Models;

public sealed record FolderScanResult(string Path, int FolderCount, IReadOnlyList<FileItem> Files, int SkippedItemCount = 0);
