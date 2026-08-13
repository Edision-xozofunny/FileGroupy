using FileGroupy.Models;

namespace FileGroupy.Services;

/// <summary>定义文本、图像和系统默认程序的文件预览能力</summary>
public interface IFilePreviewService
{
    /// <summary>为可内嵌预览的文本或图像文件异步加载内容</summary>
    /// <param name="file">需要预览的文件</param>
    /// <param name="cancellationToken">用于取消读取或 MTP 下载的标记</param>
    /// <returns>文本或图像预览结果；不支持内嵌预览时返回 <see langword="null"/></returns>
    Task<FilePreviewResult?> CreatePreviewAsync(FileItem file, CancellationToken cancellationToken = default);

    /// <summary>使用 Windows 默认关联打开文件；没有关联程序时显示 Windows“打开方式”界面</summary>
    /// <param name="file">需要打开的文件</param>
    /// <param name="cancellationToken">用于取消 MTP 临时下载的标记</param>
    Task OpenAsync(FileItem file, CancellationToken cancellationToken = default);

    /// <summary>使用 Windows 为该扩展名登记的默认程序打开文件</summary>
    /// <param name="file">需要外部打开的文件</param>
    /// <param name="cancellationToken">用于取消 MTP 临时下载的标记</param>
    Task OpenWithDefaultApplicationAsync(FileItem file, CancellationToken cancellationToken = default);

    /// <summary>显示 Windows 内置的“打开方式”界面，让用户选择应用程序</summary>
    /// <param name="file">需要选择打开方式的文件</param>
    /// <param name="cancellationToken">用于取消 MTP 临时下载的标记</param>
    Task OpenWithApplicationAsync(FileItem file, CancellationToken cancellationToken = default);
}