using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace NetCable.Desktop.Services;

/// <summary>轻量确认对话框（Avalonia 无内置 MessageBox）。</summary>
public static class Dialogs
{
    public static async Task<bool> ConfirmAsync(Window? owner, string title, string message, string okText = "继续", string cancelText = "取消")
    {
        var okButton = new Button { Content = okText, Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelButton = new Button { Content = cancelText, Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#0B1220")),
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#E2E8F0")),
                        FontSize = 13
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = { cancelButton, okButton }
                    }
                }
            }
        };

        okButton.Click += (_, _) => dialog.Close(true);
        cancelButton.Click += (_, _) => dialog.Close(false);

        if (owner is not null) return await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
        dialog.Show();
        return false;
    }
}
