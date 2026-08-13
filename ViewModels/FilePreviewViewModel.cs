using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileGroupy.Models;
using FileGroupy.Services;

namespace FileGroupy.ViewModels;

/// <summary>文件预览窗口的状态，负责呈现已解码文本或图片及调用默认程序</summary>
public partial class FilePreviewViewModel : ObservableObject
{
    /// <summary>提供 Shell 默认程序打开能力的预览服务</summary>
    private readonly IFilePreviewService _previewService;

    /// <summary>创建一个文本或图片预览状态</summary>
    /// <param name="preview">已加载的预览内容</param>
    /// <param name="previewService">文件预览服务</param>
    public FilePreviewViewModel(FilePreviewResult preview, IFilePreviewService previewService)
    {
        _previewService = previewService;
        File = preview.File;
        Title = preview.File.Name;
        TextContent = preview.TextContent;
        ImageSource = preview.ImageSource;
        IsTextPreview = preview.TextContent is not null;
        IsImagePreview = preview.ImageSource is not null;
        Notice = preview.IsTruncated ? "文件较大，当前仅显示前 2 MiB 内容" : string.Empty;
    }

    /// <summary>源文件元数据</summary>
    public FileItem File { get; }
    /// <summary>窗口标题</summary>
    public string Title { get; }
    /// <summary>文本预览内容</summary>
    public string? TextContent { get; }
    /// <summary>图片预览内容</summary>
    public ImageSource? ImageSource { get; }
    /// <summary>指示当前内容是否为文本</summary>
    public bool IsTextPreview { get; }
    /// <summary>指示当前内容是否为图片</summary>
    public bool IsImagePreview { get; }
    /// <summary>文本截断等预览提示</summary>
    public string Notice { get; }

    /// <summary>使用 Windows 默认关联程序打开当前文件</summary>
    [RelayCommand]
    private async Task OpenWithDefaultApplicationAsync() => await _previewService.OpenWithDefaultApplicationAsync(File);
}