using System.Windows.Controls;
using System.Windows.Input;
using GManager.ViewModels;

namespace GManager.Views.Controls;

public partial class AvatarSheet : UserControl
{
    public AvatarSheet()
    {
        InitializeComponent();
    }

    private void OnBackdropClicked(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsAccountSwitcherOpen = false;
        }
    }
}
