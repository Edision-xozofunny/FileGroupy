using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileGroupy.Models;
using FileGroupy.Services;

namespace FileGroupy.ViewModels;

public partial class FilePreviewViewModel : ObservableObject
{
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
    public bool IsTextPreview { get; }
    public bool IsImagePreview { get; }
    /// <summary>文本截断等预览提示</summary>
    public string Notice { get; }

    [RelayCommand]
    private async Task OpenWithDefaultApplicationAsync() => await _previewService.OpenWithDefaultApplicationAsync(File);
}
