using System.Windows.Media;

namespace FileGroupy.Models;

public sealed record FilePreviewResult(FileItem File, string? TextContent, ImageSource? ImageSource, bool IsTruncated);
