namespace FileGroupy.Models;

/// <summary>系统内置的文件类型分类</summary>
public enum FileCategory
{
    /// <summary>图像文件</summary>
    Images = 0,
    /// <summary>音频文件</summary>
    Audio = 1,
    /// <summary>视频文件</summary>
    Video = 2,
    Office = 3,
    /// <summary>压缩归档文件</summary>
    Archives = 4,
    /// <summary>源代码、脚本与开发配置文件</summary>
    SourceCode = 5,
    /// <summary>未匹配内置规则的其他文件</summary>
    Other = 6,
    /// <summary>Windows、Android、Apple 与 Linux 安装包</summary>
    Installers = 7
}
