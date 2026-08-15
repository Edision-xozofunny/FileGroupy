namespace FileGroupy.Models;

public sealed record MtpDeviceInfo(string DeviceId, string DisplayName, string? Manufacturer, PortableDeviceProtocol Protocol);
