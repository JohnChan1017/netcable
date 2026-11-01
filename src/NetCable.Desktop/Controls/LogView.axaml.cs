using System.Collections;
using Avalonia;
using Avalonia.Controls;
using NetCable.Desktop.ViewModels;

namespace NetCable.Desktop.Controls;

public partial class LogView : UserControl
{
    public static readonly StyledProperty<IEnumerable?> LogsProperty =
        AvaloniaProperty.Register<LogView, IEnumerable?>(nameof(Logs));

    public IEnumerable? Logs
    {
        get => GetValue(LogsProperty);
        set => SetValue(LogsProperty, value);
    }

    public LogView()
    {
        InitializeComponent();
    }
}
