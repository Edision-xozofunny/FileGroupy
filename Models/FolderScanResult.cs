namespace FileGroupy.Models;

/// <summary>一次文件夹扫描产生的聚合结果</summary>
/// <param name="Path">被扫描的根目录绝对路径</param>
/// <param name="FolderCount">根目录及其所有可访问子目录的数量</param>
/// <param name="Files">扫描到的文件元数据集合</param>
/// <param name="SkippedItemCount">因权限、损坏元数据或设备协议错误而跳过的目录或文件数</param>
public sealed record FolderScanResult(string Path, int FolderCount, IReadOnlyList<FileItem> Files, int SkippedItemCount = 0);