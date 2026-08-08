using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetMcp.Server;

public static class JsonOptions
{
    public static JsonSerializerOptions Default { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };
}

public static class PolicyErrorCodes
{
    public const string PathOutsideTrustedRoots = "PathOutsideTrustedRoots";
}

public sealed record PolicyErrorDto
{
    public required string Error { get; init; }
    public required string Message { get; init; }
    public required string SuggestedAction { get; init; }
}

public sealed record WorkspaceOpenResultDto
{
    public required string Phase { get; init; }
    public string? Message { get; init; }
    public string? SuggestedAction { get; init; }
}
