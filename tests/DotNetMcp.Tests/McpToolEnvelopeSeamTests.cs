using System.Text.Json;
using DotNetMcp.Server;
using ModelContextProtocol.Protocol;

namespace DotNetMcp.Tests;

public class McpToolEnvelopeSeamTests
{
    [Fact]
    public void mcp_tool_envelope_is_the_single_ready_session_and_json_envelope()
    {
        var serverDir = FindServerDir();
        var toolFiles = new[]
        {
            "SymbolTools.cs",
            "ProjectTools.cs",
            "XamlTools.cs",
            "DiagnosticTools.cs",
            "SymbolRefactoringTools.cs",
            "WorkspaceTools.cs",
        };

        foreach (var name in toolFiles)
        {
            var text = File.ReadAllText(Path.Combine(serverDir, name));
            Assert.Contains("McpToolEnvelope", text, StringComparison.Ordinal);
            Assert.DoesNotContain("private bool TryGetReadySession", text, StringComparison.Ordinal);
            Assert.DoesNotContain("private static CallToolResult OkResult", text, StringComparison.Ordinal);
            Assert.DoesNotContain("private static CallToolResult ErrorResult", text, StringComparison.Ordinal);
            Assert.DoesNotContain("private static PolicyErrorDto ToPolicyError", text, StringComparison.Ordinal);
            Assert.DoesNotContain("JsonSerializer.Serialize", text, StringComparison.Ordinal);
        }

        var envelope = File.ReadAllText(Path.Combine(serverDir, "McpToolEnvelope.cs"));
        Assert.Contains("TryGetReadySession", envelope, StringComparison.Ordinal);
        Assert.Contains("OkResult", envelope, StringComparison.Ordinal);
        Assert.Contains("ErrorResult", envelope, StringComparison.Ordinal);
        Assert.Contains("ToPolicyError", envelope, StringComparison.Ordinal);
        Assert.Contains("JsonSerializer.Serialize", envelope, StringComparison.Ordinal);
        Assert.Contains("PolicyErrorCodes.WorkspaceNotReady", envelope, StringComparison.Ordinal);
    }

    [Fact]
    public void envelope_error_result_marks_is_error_and_serializes_policy_dto()
    {
        var error = new PolicyErrorDto
        {
            Error = PolicyErrorCodes.WorkspaceNotReady,
            Message = "Workspace is not ready (phase=loading). Query tools cannot run until load completes.",
            SuggestedAction =
                "Call workspace_status to poll until phase is ready; do not retry workspace_open while loading."
        };

        var result = McpToolEnvelope.ErrorResult(error);
        Assert.True(result.IsError);
        var block = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        var parsed = JsonSerializer.Deserialize<PolicyErrorDto>(block.Text, JsonOptions.Default);
        Assert.Equal(error.Error, parsed!.Error);
        Assert.Equal(error.Message, parsed.Message);
        Assert.Equal(error.SuggestedAction, parsed.SuggestedAction);
    }

    private static string FindServerDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DotNetMcp.Server");
            if (File.Exists(Path.Combine(candidate, "McpToolEnvelope.cs")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate src/DotNetMcp.Server from the test assembly.");
    }
}
