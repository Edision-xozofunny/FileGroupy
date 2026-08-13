using System.Windows.Media;

namespace FileGroupy.Models;

/// <summary>文件预览服务返回给界面的已解码内容</summary>
/// <param name="File">正在预览的源文件</param>
/// <param name="TextContent">文本文件的截断文本；图片预览时为空</param>
/// <param name="ImageSource">图片文件的已冻结 WPF 图像；文本预览时为空</param>
/// <param name="IsTruncated">文本是否因预览大小上限而被截断</param>
public sealed record FilePreviewResult(FileItem File, string? TextContent, ImageSource? ImageSource, bool IsTruncated);