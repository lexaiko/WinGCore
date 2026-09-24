using System.Windows.Controls;
using GManager.ViewModels;

namespace GManager.Views.Pages;

public partial class InboxPage : UserControl
{
    public InboxPage()
    {
        InitializeComponent();
    }

    public InboxPage(InboxViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
