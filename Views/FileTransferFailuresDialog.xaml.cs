using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using FileGroupy.Models;

namespace FileGroupy.Views;

/// <summary>显示单次传输中失败文件的结构化明细，并支持导出 CSV</summary>
public partial class FileTransferFailuresDialog : System.Windows.Window
{
    /// <summary>本次任务的失败记录快照</summary>
    public ReadOnlyObservableCollection<FileTransferFailure> Failures { get; }

    /// <summary>创建失败详情窗口</summary>
    public FileTransferFailuresDialog(ObservableCollection<FileTransferFailure> failures)
    {
        Failures = new ReadOnlyObservableCollection<FileTransferFailure>(failures);
        InitializeComponent();
        DataContext = this;
    }

    /// <summary>将当前失败记录写成 UTF-8 BOM CSV，便于 Excel 直接识别中文</summary>
    private void ExportCsvButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"FileGroupy-传输失败-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var csv = new StringBuilder("文件名,来源类型,大小(字节),源路径,目标路径,失败原因\r\n");
        foreach (var failure in Failures)
        {
            csv.AppendLine(string.Join(',', [
                EscapeCsv(failure.FileName), EscapeCsv(failure.SourceKind.ToString()), failure.Size.ToString(),
                EscapeCsv(failure.SourcePath), EscapeCsv(failure.DestinationPath), EscapeCsv(failure.Reason)
            ]));
        }

        File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>按 RFC 4180 转义包含引号、逗号或换行的字段</summary>
    private static string EscapeCsv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}