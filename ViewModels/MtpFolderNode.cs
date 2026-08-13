using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FileGroupy.Models;

namespace FileGroupy.ViewModels;

/// <summary>MTP 文件夹选择树中的一个惰性加载节点</summary>
public sealed partial class MtpFolderNode(MtpFolderInfo folder, bool isPlaceholder = false) : ObservableObject
{
    /// <summary>节点对应的设备文件夹信息</summary>
    public MtpFolderInfo Folder { get; } = folder;

    /// <summary>占位节点仅用于让尚未读取的目录显示展开箭头</summary>
    public bool IsPlaceholder { get; } = isPlaceholder;

    /// <summary>界面显示的文件夹名称</summary>
    public string Name => Folder.Name;

    /// <summary>设备内完整路径</summary>
    public string FullPath => Folder.FullPath;

    /// <summary>已加载的直属子文件夹</summary>
    public ObservableCollection<MtpFolderNode> Children { get; } = [];

    /// <summary>由工具生成的子目录是否已读取公开绑定属性</summary>
    [ObservableProperty] private bool _isLoaded;

    /// <summary>由工具生成的节点加载失败提示公开绑定属性</summary>
    [ObservableProperty] private string? _loadError;

    /// <summary>由工具生成的树节点展开状态公开绑定属性</summary>
    [ObservableProperty] private bool _isExpanded;

}