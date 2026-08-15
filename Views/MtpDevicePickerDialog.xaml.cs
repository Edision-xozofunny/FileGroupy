using System.Collections.ObjectModel;
using System.Windows;
using FileGroupy.Models;

namespace FileGroupy.Views;

public partial class MtpDevicePickerDialog : Window
{
    /// <summary>可供选择的设备集合</summary>
    public ObservableCollection<MtpDeviceInfo> Devices { get; }

    public MtpDeviceInfo? SelectedDevice { get; set; }

    public MtpDevicePickerDialog(IEnumerable<MtpDeviceInfo> devices)
    {
        Devices = new ObservableCollection<MtpDeviceInfo>(devices);
        InitializeComponent();
        DataContext = this;
        SelectedDevice = Devices.FirstOrDefault();
    }

    /// <summary>确认设备选择;未选择设备时保持对话框开启</summary>
    private void ScanButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not null)
        {
            DialogResult = true;
        }
    }
}
