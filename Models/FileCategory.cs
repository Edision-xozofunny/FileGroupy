namespace FileGroupy.Models;

/// <summary>系统内置的文件类型分类</summary>
public enum FileCategory
{
    /// <summary>图像文件</summary>
    Images,
    /// <summary>音频文件</summary>
    Audio,
    /// <summary>视频文件</summary>
    Video,
    /// <summary>Office、PDF 及开放文档格式</summary>
    Office,
    /// <summary>压缩归档文件</summary>
    Archives,
    /// <summary>未匹配内置规则的其他文件</summary>
    Other
}