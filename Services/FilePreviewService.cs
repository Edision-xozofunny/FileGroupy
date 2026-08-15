using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileGroupy.Models;

namespace FileGroupy.Services;

/// <summary>处理轻量文本/图片预览,并将其他类型安全交给 Windows 默认程序</summary>
public sealed class FilePreviewService(IMtpDeviceService mtpDeviceService) : IFilePreviewService
{
    /// <summary>内嵌文本预览允许读取的最大字节数</summary>
    private const int MaxTextPreviewBytes = 2 * 1024 * 1024;

    /// <summary>可直接在应用内展示的文本文件扩展名</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".csv", ".json", ".xml", ".yaml", ".yml", ".ini", ".config", ".cs", ".xaml", ".js", ".ts", ".html", ".css", ".ps1", ".py", ".java", ".cpp", ".h"
    };

    /// <inheritdoc />
    public async Task<FilePreviewResult?> CreatePreviewAsync(FileItem file, CancellationToken cancellationToken = default)
    {
        if (file.Category == FileCategory.Images)
        {
            // SVG 属于矢量图, 交给系统默认程序或浏览器打开, 不进入 WPF 位图解码流程.
            if (string.Equals(file.Extension, ".svg", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var previewPath = await GetReadablePathAsync(file, cancellationToken);
            if (string.Equals(Path.GetExtension(previewPath), ".gif", StringComparison.OrdinalIgnoreCase))
            {
                return new FilePreviewResult(file, null, LoadAnimatedGif(previewPath), false);
            }

            return new FilePreviewResult(file, null, await LoadImageAsync(previewPath, cancellationToken), false);
        }

        if (!TextExtensions.Contains(file.Extension))
        {
            return null;
        }

        var textPath = await GetReadablePathAsync(file, cancellationToken);
        var (content, isTruncated) = await ReadTextAsync(textPath, cancellationToken);
        return new FilePreviewResult(file, content, null, isTruncated);
    }

    /// <inheritdoc />
    public async Task OpenAsync(FileItem file, CancellationToken cancellationToken = default)
    {
        var path = await GetReadablePathAsync(file, cancellationToken);
        try
        {
            OpenWithDefaultApplication(path);
        }
        catch (Win32Exception)
        {
            OpenWithApplication(path);
        }
    }

    /// <inheritdoc />
    public async Task OpenWithDefaultApplicationAsync(FileItem file, CancellationToken cancellationToken = default)
    {
        var path = await GetReadablePathAsync(file, cancellationToken);
        OpenWithDefaultApplication(path);
    }

    /// <inheritdoc />
    public async Task OpenWithApplicationAsync(FileItem file, CancellationToken cancellationToken = default)
    {
        var path = await GetReadablePathAsync(file, cancellationToken);
        await Task.Run(() => OpenWithApplication(path), cancellationToken);
    }

    /// <inheritdoc />
    public Task OpenFileLocationAsync(FileItem file, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (file.SourceKind != StorageSourceKind.LocalFileSystem)
        {
            throw new NotSupportedException("便携设备文件无法在 Windows 文件资源管理器中定位");
        }

        if (!File.Exists(file.FullPath))
        {
            throw new FileNotFoundException("源文件已不存在", file.FullPath);
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.FullPath}\"") { UseShellExecute = true });
        return Task.CompletedTask;
    }

    /// <summary>调用 Windows Shell 的默认文件关联打开文件</summary>
    private static void OpenWithDefaultApplication(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>启动 Windows 的打开方式选择窗口</summary>
    private static void OpenWithApplication(string path)
    {
        var openWithPath = Path.Combine(Environment.SystemDirectory, "OpenWith.exe");
        if (!File.Exists(openWithPath))
        {
            throw new FileNotFoundException("找不到 Windows“打开方式”组件", openWithPath);
        }

        Process.Start(new ProcessStartInfo(openWithPath, $"\"{path}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    /// <summary>获取可由本机 API 打开的路径, MTP 文件会下载至临时目录</summary>
    private async Task<string> GetReadablePathAsync(FileItem file, CancellationToken cancellationToken)
    {
        if (file.SourceKind == StorageSourceKind.LocalFileSystem)
        {
            return file.FullPath;
        }

        return await mtpDeviceService.DownloadPreviewFileAsync(file, cancellationToken);
    }

    /// <summary>以大小上限读取文本,并按 UTF-8 优先解析 BOM 或默认编码</summary>
    private static async Task<(string Content, bool IsTruncated)> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, MaxTextPreviewBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = Math.Min(stream.Length, MaxTextPreviewBytes);
        var buffer = new byte[length];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken);
            if (count == 0) break;
            read += count;
        }

        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(buffer, 0, read), stream.Length > MaxTextPreviewBytes);
    }

    /// <summary>在线程池解码静态图片并冻结结果, 便于安全绑定回 UI 线程</summary>
    private static Task<ImageSource> LoadImageAsync(string path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 1600;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return (ImageSource)bitmap;
    }, cancellationToken);

    /// <summary>GIF 必须保持可变状态, Image 控件才能播放动画帧</summary>
    private static ImageSource LoadAnimatedGif(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        return bitmap;
    }
}
