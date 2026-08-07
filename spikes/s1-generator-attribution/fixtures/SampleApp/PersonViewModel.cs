using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Serialization;

namespace SampleApp;

public partial class PersonViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "Ada";

    [ObservableProperty]
    private int _age;

    // Handwritten member on a partial type that also has generated members.
    public string DisplayName => $"{Name} ({Age})";
}

public partial class PersonViewModel
{
    // Overloaded handwritten members — attribution keys must include signature.
    public string Format() => Name;

    public string Format(string prefix) => $"{prefix}:{Name}";
}

[JsonSerializable(typeof(PersonDto))]
public partial class AppJsonContext : JsonSerializerContext;

public sealed class PersonDto
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}
