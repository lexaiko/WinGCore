using CommunityToolkit.Mvvm.ComponentModel;

namespace GManager.ViewModels;

/// <summary>
/// Base class for all MVVM view models, leveraging CommunityToolkit.Mvvm.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
    private bool _isBusy;
    private string? _statusMessage;

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }
}
