using System.Windows.Controls;
using GManager.ViewModels;

namespace GManager.Views.Pages;

public partial class DrivePage : UserControl
{
    public DrivePage()
    {
        InitializeComponent();
    }

    public DrivePage(DriveViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
