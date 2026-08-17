using System.Windows.Media.Imaging;
using System.IO;

namespace FileGroupy.Services;

/// <summary>提供轻量栅格图像有效性校验, 供本地和设备文件共用</summary>
internal static class ImageValidation
{
    /// <summary>SVG 是矢量格式, 不应交由 WPF 位图解码器验证</summary>
    public static bool IsSvg(string extension) => string.Equals(extension, ".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>快速读取首帧元数据以验证图像头, 避免完整加载大型图片像素</summary>
    public static bool CanReadRasterImage(Stream stream)
    {
        try
        {
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return frame.PixelWidth > 0 && frame.PixelHeight > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}