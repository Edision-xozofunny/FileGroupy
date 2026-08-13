using System.Collections.ObjectModel;
using System.Windows;
using FileGroupy.Models;

namespace FileGroupy.Views;

/// <summary>让用户从 Windows 已识别的 MTP 设备中选择一个进行扫描的模态窗口</summary>
public partial class MtpDevicePickerDialog : Window
{
    /// <summary>可供选择的设备集合</summary>
    public ObservableCollection<MtpDeviceInfo> Devices { get; }

    /// <summary>用户当前选择的设备；未选择时为空</summary>
    public MtpDeviceInfo? SelectedDevice { get; set; }

    /// <summary>用枚举到的 MTP 设备初始化选择窗口</summary>
    /// <param name="devices">Windows 当前识别的设备</param>
    public MtpDevicePickerDialog(IEnumerable<MtpDeviceInfo> devices)
    {
        Devices = new ObservableCollection<MtpDeviceInfo>(devices);
        InitializeComponent();
        DataContext = this;
        SelectedDevice = Devices.FirstOrDefault();
    }

    /// <summary>确认设备选择；未选择设备时保持对话框开启</summary>
    private void ScanButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not null)
        {
            DialogResult = true;
        }
    }
}