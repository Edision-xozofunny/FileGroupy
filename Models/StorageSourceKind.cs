namespace FileGroupy.Models;

/// <summary>文件所在存储源的访问协议</summary>
public enum StorageSourceKind
{
    /// <summary>具有 Windows 文件系统路径的本地磁盘、U 盘或移动硬盘</summary>
    LocalFileSystem,
    /// <summary>通过 Windows Portable Devices 协议访问的手机或相机</summary>
    MtpDevice
}