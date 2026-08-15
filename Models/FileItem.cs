namespace FileGroupy.Models;

public sealed record FileItem(
	string Name,
	string FullPath,
	string Extension,
	long Size,
	DateTime LastModified,
	FileCategory Category,
	StorageSourceKind SourceKind = StorageSourceKind.LocalFileSystem,
	string? SourceId = null,
	bool IsInvalidImage = false);
