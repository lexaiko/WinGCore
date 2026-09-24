using System.Windows.Controls;
using GManager.ViewModels;

namespace GManager.Views.Pages;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    public SettingsPage(SettingsViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
