namespace DotNetMcp.Tests;

/// <summary>
/// Skips on non-Windows. Windows CI runs the body; other OS do not count a silent pass.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Runs on Windows CI.";
        }
    }
}

/// <summary>
/// Skips on Windows. Linux and macOS CI run the body.
/// </summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Runs on Linux and macOS CI.";
        }
    }
}

/// <summary>
/// Skips except on Linux. Linux CI runs the body.
/// </summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Runs on Linux CI.";
        }
    }
}
