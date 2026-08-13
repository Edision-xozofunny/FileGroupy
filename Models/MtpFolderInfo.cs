namespace FileGroupy.Models;

/// <summary>MTP 设备中可作为扫描根目录的文件夹摘要</summary>
/// <param name="Name">文件夹显示名称</param>
/// <param name="FullPath">MediaDevices/WPD 使用的设备内完整路径</param>
public sealed record MtpFolderInfo(string Name, string FullPath);