using System.Windows;
using FileGroupy.ViewModels;

namespace FileGroupy.Views;

/// <summary>展示删除快照创建和恢复审计记录</summary>
public partial class RecoveryHistoryDialog : Window
{
    public RecoveryHistoryDialog(DeletedFileRecoveryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadHistoryAsync();
    }
}