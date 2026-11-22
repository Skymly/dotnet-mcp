using DotNetMcp.Server;

// Exercise the production stdio entry point. Only this test executable can pause
// after a specified write so the parent can kill an actual service mid-apply.
var pauseAfter = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
return await ServerHost.RunAsync(args[2..], new WorkspaceHostOptions
{
    RecoveryDirectoryForTests = args[0],
    AfterApplyDocumentWriteForTests = count =>
    {
        if (count == pauseAfter)
        {
            Console.Error.WriteLine($"recovery-probe-paused {Environment.ProcessId}");
            Thread.Sleep(Timeout.Infinite);
        }
    }
});
