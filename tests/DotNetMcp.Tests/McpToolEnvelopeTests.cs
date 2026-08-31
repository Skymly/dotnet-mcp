using System.Text.Json;
using DotNetMcp.Core;
using DotNetMcp.Server;
using DotNetMcp.Xaml;
using ModelContextProtocol.Protocol;

namespace DotNetMcp.Tests;

public class McpToolEnvelopeTests
{
    [Fact]
    public void ok_result_is_not_error_and_json_contains_payload()
    {
        var result = McpToolEnvelope.OkResult(new { handle = "S:Widget" });

        Assert.True(result.IsError is not true);
        var block = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("S:Widget", block.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void error_result_is_error_and_deserializes_same_code()
    {
        var error = new PolicyErrorDto
        {
            Error = PolicyErrorCodes.PathOutsideTrustedRoots,
            Message = "path is outside trusted roots",
            SuggestedAction = "open a workspace under a trusted root"
        };

        var result = McpToolEnvelope.ErrorResult(error);

        Assert.True(result.IsError);
        var block = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        var parsed = JsonSerializer.Deserialize<PolicyErrorDto>(block.Text, JsonOptions.Default);
        Assert.Equal(error.Error, parsed!.Error);
    }

    [Fact]
    public void to_policy_error_maps_symbol_and_xaml_query_errors()
    {
        var symbol = new InvalidSymbolHandleError("bad handle", "pass a valid handle");
        var xaml = new MissingXamlClassError("missing x:Class", "add x:Class");

        var symbolDto = McpToolEnvelope.ToPolicyError(symbol);
        var xamlDto = McpToolEnvelope.ToPolicyError(xaml);

        Assert.Equal(SymbolQueryErrorCodes.InvalidSymbolHandle, symbolDto.Error);
        Assert.Equal(XamlQueryErrorCodes.MissingXamlClass, xamlDto.Error);
    }
}
