namespace FileGroupy.Models;

/// <summary>Windows WPD 将便携设备暴露给应用时使用的文件传输协议</summary>
public enum PortableDeviceProtocol
{
    /// <summary>媒体传输协议，通常可访问 Android 的共享存储空间</summary>
    Mtp,
    /// <summary>图片传输协议，通常仅暴露相机或媒体目录</summary>
    Ptp
}