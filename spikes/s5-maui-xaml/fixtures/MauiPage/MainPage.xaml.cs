namespace MauiPage;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }
}

public sealed class MainViewModel
{
    public string Title { get; set; } = "hello";
}
