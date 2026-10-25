using System.Text.Json.Serialization;

namespace ToolkitApp;

public sealed class Poco
{
    public int Id { get; set; }

    public string? Label { get; set; }
}

[JsonSerializable(typeof(Poco))]
public partial class AppJsonContext : JsonSerializerContext
{
}
