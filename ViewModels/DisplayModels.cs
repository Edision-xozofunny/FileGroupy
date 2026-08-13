using CommunityToolkit.Mvvm.ComponentModel;
using FileGroupy.Models;

namespace FileGroupy.ViewModels;

/// <summary>用于概览页分类卡片的统计数据</summary>
public sealed class CategorySummary
{
    /// <summary>卡片对应的文件分类</summary>
    public required FileCategory Category { get; init; }
    /// <summary>界面显示的分类名称</summary>
    public required string Name { get; init; }
    /// <summary>界面显示的简短分类标识</summary>
    public required string Icon { get; init; }
    /// <summary>当前分类内的文件数量</summary>
    public int FileCount { get; init; }
    /// <summary>当前分类所有文件的总大小，单位为字节</summary>
    public long TotalSize { get; init; }
    /// <summary>适合直接显示在界面上的友好大小文本</summary>
    public string SizeText => SizeFormatter.Format(TotalSize);
}

/// <summary>文件浏览表格中的一行，可以是分类根行或实际文件行</summary>
public partial class ExplorerRow : ObservableObject
{
    /// <summary>指示当前行是否为分类根节点</summary>
    public bool IsCategory { get; init; }
    /// <summary>指示当前行是否为扩展名分组节点</summary>
    public bool IsExtensionGroup { get; init; }
    /// <summary>分类根节点是否处于展开状态；文件行始终视为展开</summary>
    public bool IsExpanded { get; set; } = true;
    /// <summary>当前行所属的文件分类</summary>
    public FileCategory Category { get; init; }
    /// <summary>分类名或文件名</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>文件扩展名；分类根行为空</summary>
    public string Extension { get; init; } = string.Empty;
    /// <summary>文件完整路径；分类根行为空</summary>
    public string Location { get; init; } = string.Empty;
    /// <summary>格式化后的最后修改时间；分类根行为空</summary>
    public string Modified { get; init; } = string.Empty;
    /// <summary>格式化后的文件或分类总大小</summary>
    public string Size { get; init; } = string.Empty;
    /// <summary>分类根行包含的文件数量；文件行固定为零</summary>
    public int ChildCount { get; init; }
    /// <summary>扩展名分组对应的原始扩展名；其他行为空</summary>
    public string GroupExtension { get; init; } = string.Empty;
    /// <summary>文件行关联的原始文件元数据；分类根行为空</summary>
    public FileItem? File { get; init; }
    /// <summary>指示当前行是否为可展开或可全选的分组节点</summary>
    public bool IsGroup => IsCategory || IsExtensionGroup;
    /// <summary>指示当前行是否允许通过复选框参与批量操作</summary>
    public bool IsSelectable => true;

    /// <summary>由 CommunityToolkit 生成的行选中状态公开属性</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>按行类型生成包含展开标志或文件名的显示文本</summary>
    public string DisplayName => IsCategory
        ? $"{(IsExpanded ? "▾" : "▸")} {Name}  {ChildCount:N0} 个文件"
        : IsExtensionGroup
            ? $"{(IsExpanded ? "▾" : "▸")} {Name}  {ChildCount:N0} 个文件"
            : Name;
}

/// <summary>将字节数转换为 KB、MB、GB 等易读文本</summary>
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