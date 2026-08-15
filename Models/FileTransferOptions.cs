namespace FileGroupy.Models;

/// <summary>定义批量复制或移动时的目标结构与重名文件处理策略</summary>
/// <param name="DestinationPath">目标根目录的绝对路径</param>
/// <param name="PreserveSourceStructure">是否在目标目录中保留源文件的相对目录结构</param>
/// <param name="OverwriteAll">发生同名冲突时是否一律覆盖目标文件</param>
/// <param name="SkipAllConflicts">发生同名冲突时是否一律跳过当前文件</param>
/// <param name="RenameDuplicates">不保留目录结构时，发生同名冲突是否生成带数字后缀的新文件名</param>
/// <param name="MoveFiles"><see langword="true"/> 表示移动；<see langword="false"/> 表示复制</param>
public sealed record FileTransferOptions(
    string DestinationPath,
    bool PreserveSourceStructure,
    bool OverwriteAll,
    bool SkipAllConflicts,
    bool RenameDuplicates,
    bool MoveFiles,
    string? DestinationMtpDeviceId = null);

/// <summary>描述批量传输的聚合进度，供界面显示而无需追踪各文件任务</summary>
/// <param name="CompletedFiles">已结束处理的文件数，包含成功、跳过和失败</param>
/// <param name="TotalFiles">本批次待处理的总文件数</param>
/// <param name="TransferredBytes">已处理文件的累计字节数</param>
/// <param name="TotalBytes">本批次源文件的总字节数</param>
public sealed record FileTransferProgress(int CompletedFiles, int TotalFiles, long TransferredBytes, long TotalBytes);

/// <summary>记录单个文件传输失败时的诊断信息</summary>
/// <param name="FileName">失败文件的名称</param>
/// <param name="SourcePath">源文件路径</param>
/// <param name="DestinationPath">本次计算出的目标路径</param>
/// <param name="Size">源文件大小</param>
/// <param name="SourceKind">源文件的存储来源类型</param>
/// <param name="Reason">底层操作返回的失败原因</param>
public sealed record FileTransferFailure(
    string FileName,
    string SourcePath,
    string DestinationPath,
    long Size,
    StorageSourceKind SourceKind,
    string Reason);

/// <summary>汇总批量操作的结果，并保留不影响后续任务的结构化失败记录</summary>
/// <param name="Succeeded">成功完成的文件数</param>
/// <param name="Skipped">因目标冲突或源目标相同而跳过的文件数</param>
/// <param name="Failures">每个失败文件的可查看和可导出诊断信息</param>
/// <param name="SuccessfulSourcePaths">实际完成传输的源文件路径，用于移动后同步列表</param>
/// <param name="SuccessfulTransfers">实际完成的源路径与目标路径映射，用于刷新树节点</param>
public sealed record FileTransferResult(
    int Succeeded,
    int Skipped,
    IReadOnlyList<FileTransferFailure> Failures,
    IReadOnlyList<string> SuccessfulSourcePaths,
    IReadOnlyList<FileTransferSuccess>? SuccessfulTransfers = null);

/// <summary>记录单个成功文件操作的源路径与目标路径，删除场景目标路径为空</summary>
/// <param name="SourcePath">源文件路径</param>
/// <param name="DestinationPath">目标文件路径，删除时为空字符串</param>
public sealed record FileTransferSuccess(string SourcePath, string DestinationPath);
