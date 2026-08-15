using System.Windows;
using FileGroupy.ViewModels;

namespace FileGroupy.Views;

public partial class FileTransferDialog : Window
{
    public FileTransferDialog(FileTransferDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }
}
