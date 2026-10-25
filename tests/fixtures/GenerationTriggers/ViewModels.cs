using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TriggerApp;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

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

    // "On"-prefixed stem: generates RefreshCommand (S9 candidate On+stem).
    [RelayCommand]
    private void OnRefresh()
    {
    }

    [RelayCommand]
    private Task LoadAsync() => Task.CompletedTask;

    // Handwritten control: no generator involvement at all.
    public string HandwrittenNote => "plain";
}
