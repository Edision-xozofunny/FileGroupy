namespace FileGroupy.Models;

/// <summary>扫描阶段提取的单个文件元数据</summary>
/// <param name="Name">文件名，不包含目录</param>
/// <param name="FullPath">文件的绝对路径</param>
/// <param name="Extension">带点号的文件扩展名</param>
/// <param name="Size">文件大小，单位为字节</param>
/// <param name="LastModified">文件最后修改的本地时间</param>
/// <param name="Category">按扩展名识别出的文件分类</param>
public sealed record FileItem(
	string Name,
	string FullPath,
	string Extension,
	long Size,
	DateTime LastModified,
	FileCategory Category,
	StorageSourceKind SourceKind = StorageSourceKind.LocalFileSystem,
	string? SourceId = null);