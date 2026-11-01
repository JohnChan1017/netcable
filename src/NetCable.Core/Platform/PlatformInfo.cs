using System.Runtime.InteropServices;

namespace NetCable.Core.Platform;

public enum OsFamily
{
    Windows,
    MacOS,
    Linux,
    Unknown
}

public static class PlatformInfo
{
    public static OsFamily Family { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OsFamily.Windows :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OsFamily.MacOS :
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? OsFamily.Linux :
        OsFamily.Unknown;

    public static string DisplayName => Family switch
    {
        OsFamily.Windows => "Windows",
        OsFamily.MacOS => "macOS",
        OsFamily.Linux => "Linux",
        _ => RuntimeInformation.OSDescription
    };

    public static string Architecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    public static bool IsWindows => Family == OsFamily.Windows;
    public static bool IsMacOS => Family == OsFamily.MacOS;
    public static bool IsLinux => Family == OsFamily.Linux;
}
