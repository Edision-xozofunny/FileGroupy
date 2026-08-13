namespace FileGroupy.Models;

/// <summary>可供用户选择的 Windows 便携设备摘要，支持 MTP 与 PTP</summary>
/// <param name="DeviceId">Windows WPD 为设备分配的稳定标识</param>
/// <param name="DisplayName">用于界面显示的设备名称</param>
/// <param name="Manufacturer">设备制造商；设备未报告时为空</param>
/// <param name="Protocol">设备当前在 USB 连接中启用的文件传输协议</param>
public sealed record MtpDeviceInfo(string DeviceId, string DisplayName, string? Manufacturer, PortableDeviceProtocol Protocol);