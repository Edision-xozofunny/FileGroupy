using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using FileGroupy.Models;
using FileGroupy.Services;
using FileGroupy.ViewModels;

namespace FileGroupy.Views;

public partial class MtpFolderPickerDialog : Window, INotifyPropertyChanged
{
    /// <summary>访问设备目录的 MTP 服务</summary>
    private readonly IMtpDeviceService _mtpDeviceService;
    private readonly MtpDeviceInfo _deviceInfo;

    /// <summary>树根节点集合,正常情况下仅包含一个设备根目录</summary>
    public ObservableCollection<MtpFolderNode> RootNodes { get; } = [];
    public MtpFolderNode? SelectedNode { get; private set; }
    public MtpFolderInfo? SelectedFolder => SelectedNode?.Folder;
    public string SelectedPath => SelectedNode?.FullPath ?? "请选择文件夹";
    /// <summary>指示是否正在读取某一层 MTP 目录</summary>
    public bool IsLoading { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>创建针对指定设备的文件夹选择器</summary>
    public MtpFolderPickerDialog(MtpDeviceInfo deviceInfo, IMtpDeviceService mtpDeviceService)
    {
        _deviceInfo = deviceInfo;
        _mtpDeviceService = mtpDeviceService;
        InitializeComponent();
        DataContext = this;
    }

    /// <summary>首次显示时预加载根目录下的存储容器,不递归遍历整台手机</summary>
    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            SetLoading(true);
            var root = await _mtpDeviceService.GetRootFolderAsync(_deviceInfo);
            var rootNode = CreateFolderNode(root);
            RootNodes.Add(rootNode);
            await LoadChildrenAsync(rootNode);
            rootNode.IsExpanded = true;
            SelectedNode = rootNode.Children.FirstOrDefault(node => !node.IsPlaceholder) ?? rootNode;
            NotifyPropertyChanged(nameof(SelectedPath));
            SelectNodeInTree(rootNode, SelectedNode);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(this, $"无法访问设备存储目录：{exception.Message}\n\n请保持设备解锁，并确认 USB 用途为“文件传输”或“照片传输”", "选择设备文件夹", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
        finally
        {
            SetLoading(false);
        }
    }

    private async void FolderTree_OnExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem { DataContext: MtpFolderNode node } || node.IsLoaded)
        {
            return;
        }

        try
        {
            SetLoading(true);
            await LoadChildrenAsync(node);
        }
        catch (Exception exception)
        {
            node.LoadError = exception.Message;
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void FolderTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        SelectedNode = e.NewValue as MtpFolderNode;
        NotifyPropertyChanged(nameof(SelectedPath));
    }

    private void ScanButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is not null)
        {
            DialogResult = true;
        }
    }

    /// <summary>切换加载遮罩状态并刷新绑定属性</summary>
    private void SetLoading(bool isLoading)
    {
        IsLoading = isLoading;
        NotifyPropertyChanged(nameof(IsLoading));
    }

    /// <summary>读取一个目录的直属子目录,并为每一项建立惰性展开占位节点</summary>
    private async Task LoadChildrenAsync(MtpFolderNode node)
    {
        var folders = await _mtpDeviceService.GetChildFoldersAsync(_deviceInfo, node.FullPath);
        node.Children.Clear();
        foreach (var folder in folders)
        {
            node.Children.Add(CreateFolderNode(folder));
        }

        node.IsLoaded = true;
    }

    private static MtpFolderNode CreateFolderNode(MtpFolderInfo folder)
    {
        var node = new MtpFolderNode(folder);
        node.Children.Add(new MtpFolderNode(new MtpFolderInfo(string.Empty, string.Empty), true));
        return node;
    }

    /// <summary>将自动选择的存储容器同步为 TreeView 中可见的选中项</summary>
    private void SelectNodeInTree(MtpFolderNode rootNode, MtpFolderNode selectedNode)
    {
        FolderTree.UpdateLayout();
        if (FolderTree.ItemContainerGenerator.ContainerFromItem(rootNode) is not TreeViewItem rootItem)
        {
            return;
        }

        rootItem.IsExpanded = true;
        rootItem.UpdateLayout();
        if (rootItem.ItemContainerGenerator.ContainerFromItem(selectedNode) is TreeViewItem selectedItem)
        {
            selectedItem.IsSelected = true;
        }
    }

    /// <summary>触发指定绑定属性的变更通知</summary>
    private void NotifyPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
