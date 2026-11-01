using Avalonia.Controls;
using Avalonia.Controls.Templates;
using NetCable.Desktop.ViewModels;

namespace NetCable.Desktop;

public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? data)
    {
        if (data is null) return null;

        if (data is PlaceholderViewModel) return new Views.PlaceholderView();
        if (data is ErrorPageViewModel error)
            return new TextBlock
            {
                Text = $"页面加载失败：{error.Title} — {error.Message}",
                Foreground = Avalonia.Media.Brushes.OrangeRed,
                Margin = new Avalonia.Thickness(12),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };

        var name = data.GetType().FullName!
            .Replace("ViewModels", "Views", StringComparison.Ordinal)
            .Replace("ViewModel", "View", StringComparison.Ordinal);

        var type = Type.GetType(name);
        if (type is not null && Activator.CreateInstance(type) is Control control) return control;

        return new TextBlock
        {
            Text = $"未找到视图：{name}",
            Margin = new Avalonia.Thickness(12)
        };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
