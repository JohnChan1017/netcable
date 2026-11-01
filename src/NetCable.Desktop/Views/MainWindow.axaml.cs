using Avalonia.Controls;
using NetCable.Desktop.ViewModels;

namespace NetCable.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? initialToolId)
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel(initialToolId);
    }
}
