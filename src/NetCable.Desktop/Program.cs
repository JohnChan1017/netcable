using Avalonia;

namespace NetCable.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 提权实例：以管理员身份执行 headless 任务，结果写文件后退出（不打开 UI）
        if (Services.ElevatedBridge.IsElevatedTask(args))
            return Services.ElevatedBridge.RunElevatedTask(args);

        App.ScreenshotPath = args.FirstOrDefault(a => a.StartsWith("--screenshot=", StringComparison.Ordinal))?[13..];
        App.StartTool = args.FirstOrDefault(a => a.StartsWith("--tool=", StringComparison.Ordinal))?[7..];
        App.AutoRun = args.Contains("--autorun", StringComparer.OrdinalIgnoreCase);
        if (int.TryParse(args.FirstOrDefault(a => a.StartsWith("--wait=", StringComparison.Ordinal))?[7..], out var waitMs) && waitMs > 0)
            App.ScreenshotDelayMs = waitMs;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
