using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace NetCable.Desktop;

public partial class App : Application
{
    public static string? ScreenshotPath { get; set; }
    public static string? StartTool { get; set; }
    public static bool AutoRun { get; set; }
    public static int? ScreenshotDelayMs { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Views.MainWindow(StartTool);
            desktop.MainWindow = window;

            if (AutoRun && window.DataContext is ViewModels.MainWindowViewModel autoVm)
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    await Task.Delay(900);
                    autoVm.AutoRunCurrentPage();
                });
            }

            if (ScreenshotPath is { Length: > 0 } path)
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    await Task.Delay(ScreenshotDelayMs ?? (AutoRun ? 9000 : 2200));
                    try
                    {
                        var size = new PixelSize(
                            Math.Max(1, (int)window.Bounds.Width),
                            Math.Max(1, (int)window.Bounds.Height));
                        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
                        bitmap.Render(window);
                        bitmap.Save(path);
                    }
                    finally
                    {
                        desktop.Shutdown();
                    }
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
