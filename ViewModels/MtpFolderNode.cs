using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FileGroupy.Models;

namespace FileGroupy.ViewModels;

/// <summary>MTP 文件夹选择树中的一个惰性加载节点</summary>
public sealed partial class MtpFolderNode(MtpFolderInfo folder, bool isPlaceholder = false) : ObservableObject
{
    public MtpFolderInfo Folder { get; } = folder;

    public bool IsPlaceholder { get; } = isPlaceholder;

    /// <summary>界面显示的文件夹名称</summary>
    public string Name => Folder.Name;

    /// <summary>设备内完整路径</summary>
    public string FullPath => Folder.FullPath;

    /// <summary>已加载的直属子文件夹</summary>
    public ObservableCollection<MtpFolderNode> Children { get; } = [];

    [ObservableProperty] private bool _isLoaded;

    [ObservableProperty] private string? _loadError;

    [ObservableProperty] private bool _isExpanded;

}
