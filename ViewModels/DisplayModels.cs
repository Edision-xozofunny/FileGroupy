using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FileGroupy.Controls;
using FileGroupy.Models;

namespace FileGroupy.ViewModels;

public sealed class CategorySummary
{
    public required FileCategory Category { get; init; }
    /// <summary>界面显示的分类名称</summary>
    public required string Name { get; init; }
    /// <summary>界面显示的简短分类标识</summary>
    public required string Icon { get; init; }
    public int FileCount { get; init; }
    public long TotalSize { get; init; }
    /// <summary>适合直接显示在界面上的友好大小文本</summary>
    public string SizeText => SizeFormatter.Format(TotalSize);
}

/// <summary>文件浏览表格中的一行,可以是分类根行或实际文件行</summary>
public partial class ExplorerRow : ObservableObject, IStickyDataGridRow
{
    public bool IsCategory { get; init; }
    public bool IsExtensionGroup { get; init; }
    /// <summary>分类根节点是否处于展开状态, 文件行始终视为展开</summary>
    [ObservableProperty] private bool _isExpanded = true;
    public FileCategory Category { get; init; }
    /// <summary>分类名或文件名</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>文件扩展名;分类根行为空</summary>
    public string Extension { get; init; } = string.Empty;
    /// <summary>供表格显示的类型说明;保留 <see cref="Extension"/> 的原始扩展名语义</summary>
    public string TypeDisplayName => FileCategoryCatalog.GetDisplayName(Extension);
    /// <summary>文件完整路径;分类根行为空</summary>
    public string Location { get; init; } = string.Empty;
    public string Modified { get; init; } = string.Empty;
    /// <summary>格式化后的文件或分类总大小</summary>
    public string Size { get; init; } = string.Empty;
    /// <summary>分类根行包含的文件数量;文件行固定为零</summary>
    public int ChildCount { get; init; }
    public string GroupExtension { get; init; } = string.Empty;
    public FileItem? File { get; init; }
    public bool IsGroup => IsCategory || IsExtensionGroup;
    public bool IsStickyRow => IsCategory || IsExtensionGroup;

    /// <summary>固定分组层级,分类为 0,扩展名分组为 1,文件行为最大层级</summary>
    public int StickyLevel => IsCategory ? 0 : IsExtensionGroup ? 1 : int.MaxValue;
    public bool IsSelectable => true;

    [ObservableProperty] private bool _isSelected;

    public string DisplayName => IsCategory
        ? $"{(IsExpanded ? "▾" : "▸")} {Name}  {ChildCount:N0} 个文件"
        : IsExtensionGroup
            ? $"├─ {(IsExpanded ? "▾" : "▸")} {Name}  {ChildCount:N0} 个文件"
            : $"└─ {Name}";
}

/// <summary>鼠标悬停图片行时显示的轻量预览数据</summary>
/// <param name="ImageSource">可显示的缩略图;损坏或不可读时为空</param>
/// <param name="Resolution">分辨率文本,例如 1920 × 1080</param>
/// <param name="SizeText">文件大小文本</param>
/// <param name="TypeText">扩展名文本</param>
/// <param name="IsCorrupted">是否为损坏或不可解码的图片</param>
public sealed record ImageHoverPreview(
    ImageSource? ImageSource,
    string Resolution,
    string SizeText,
    string TypeText,
    bool IsCorrupted);

public static class SizeFormatter
{
    /// <summary>格式化给定的字节数</summary>
    /// <param name="bytes">待格式化的字节数</param>
    /// <returns>最多保留两位小数的友好大小文本</returns>
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {units[index]}";
    }
}
