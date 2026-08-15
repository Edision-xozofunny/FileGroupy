namespace FileGroupy.Models;

public sealed record FileTransferOptions(
    string DestinationPath,
    bool PreserveSourceStructure,
    bool OverwriteAll,
    bool SkipAllConflicts,
    bool RenameDuplicates,
    bool MoveFiles,
    string? DestinationMtpDeviceId = null);

public sealed record FileTransferProgress(int CompletedFiles, int TotalFiles, long TransferredBytes, long TotalBytes);

public sealed record FileTransferFailure(
    string FileName,
    string SourcePath,
    string DestinationPath,
    long Size,
    StorageSourceKind SourceKind,
    string Reason);

public sealed record FileTransferResult(
    int Succeeded,
    int Skipped,
    IReadOnlyList<FileTransferFailure> Failures,
    IReadOnlyList<string> SuccessfulSourcePaths,
    IReadOnlyList<FileTransferSuccess>? SuccessfulTransfers = null);

public sealed record FileTransferSuccess(string SourcePath, string DestinationPath);
