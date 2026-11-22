using System.Text.Json;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class WorkspaceOpenResultDtoTests
{
    [Fact]
    public void open_acknowledgment_uses_camel_case_and_omits_nulls()
    {
        var open = new WorkspaceOpenResultDto { Phase = "loading", TotalUnits = 1 };

        using var payload = JsonSerializer.SerializeToDocument(open, JsonOptions.Default);

        Assert.Equal("loading", payload.RootElement.GetProperty("phase").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("totalUnits").GetInt32());
        Assert.False(payload.RootElement.TryGetProperty("error", out _));
        Assert.Equal(open, payload.Deserialize<WorkspaceOpenResultDto>(JsonOptions.Default));
    }

    [Fact]
    public void from_status_keeps_error_code()
    {
        var status = new WorkspaceStatusDto
        {
            Phase = "failed",
            SuggestedAction = "Open a filtered solution.",
            Error = "A project is outside trusted roots.",
            ErrorCode = PolicyErrorCodes.LoadedGraphOutsideTrustedRoots
        };

        var open = WorkspaceOpenResultDto.FromStatus(status);

        Assert.Equal(status.ErrorCode, open.ErrorCode);
        Assert.Equal(status.Error, open.Error);
        Assert.Equal(status.Phase, open.Phase);
    }
}
