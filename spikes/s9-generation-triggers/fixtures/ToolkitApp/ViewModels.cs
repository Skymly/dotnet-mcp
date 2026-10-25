using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ToolkitApp;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string m_city = "";

    [ObservableProperty]
    private int plain;

    [ObservableProperty]
    private bool isActive;

    [ObservableProperty]
    public partial string Nick { get; set; }

    public MainViewModel()
    {
        Nick = "";
    }

    // User-implemented callback: pairs with the generator's partial method *definition*.
    partial void OnNameChanged(string value)
    {
    }

    [RelayCommand]
    private void Save()
    {
    }

    [RelayCommand]
    private Task LoadAsync() => Task.CompletedTask;

    [RelayCommand]
    private void OnRefresh()
    {
    }
}
