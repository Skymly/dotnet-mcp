using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class WorkspaceOpenResultDtoTests
{
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