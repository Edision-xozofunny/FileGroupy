using FileGroupy.Models;

namespace FileGroupy.Services;

/// <summary>定义文本、图像和系统默认程序的文件预览能力</summary>
public interface IFilePreviewService
{
    /// <summary>创建可在应用内显示的文本或图像预览</summary>
    /// <param name="file">需要预览的文件</param>
    /// <param name="cancellationToken">取消操作的标记</param>
    /// <returns>预览结果, 不支持内嵌预览时返回 null</returns>
    Task<FilePreviewResult?> CreatePreviewAsync(FileItem file, CancellationToken cancellationToken = default);

    /// <summary>使用 Windows 默认关联打开文件</summary>
    /// <param name="file">需要打开的文件</param>
    /// <param name="cancellationToken">取消操作的标记</param>
    Task OpenAsync(FileItem file, CancellationToken cancellationToken = default);

    /// <summary>仅使用默认关联打开文件, 不回退至打开方式选择器</summary>
    /// <param name="file">需要打开的文件</param>
    /// <param name="cancellationToken">取消操作的标记</param>
    Task OpenWithDefaultApplicationAsync(FileItem file, CancellationToken cancellationToken = default);

    /// <summary>显示 Windows 打开方式选择器</summary>
    /// <param name="file">需要选择应用打开的文件</param>
    /// <param name="cancellationToken">取消操作的标记</param>
    Task OpenWithApplicationAsync(FileItem file, CancellationToken cancellationToken = default);

    /// <summary>在 Windows 文件资源管理器中定位本地源文件</summary>
    /// <param name="file">需要定位的本地文件</param>
    /// <param name="cancellationToken">取消操作的标记</param>
    Task OpenFileLocationAsync(FileItem file, CancellationToken cancellationToken = default);
}
