using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetCable.Core.Platform;
using NetCable.Core.Tools;

namespace NetCable.Desktop.ViewModels;

public sealed partial class ToolItemViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _owner;

    public ToolItemViewModel(MainWindowViewModel owner, ToolDefinition definition)
    {
        _owner = owner;
        Definition = definition;
    }

    public ToolDefinition Definition { get; }
    public string Title => Definition.Title;
    public bool IsPending => Definition.Status != ToolStatus.Implemented;

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Select() => _owner.SelectedTool = this;
}

public sealed class ToolGroupViewModel
{
    public ToolGroupViewModel(ToolGroup group, IReadOnlyList<ToolItemViewModel> items)
    {
        Group = group;
        Items = items;
    }

    public ToolGroup Group { get; }
    public IReadOnlyList<ToolItemViewModel> Items { get; }

    public string Title => Group switch
    {
        ToolGroup.Overview => "总览",
        ToolGroup.Network => "网络与地址",
        ToolGroup.Diagnostics => "诊断",
        ToolGroup.Lookup => "查询",
        ToolGroup.System => "本机与局域网",
        ToolGroup.Remote => "远程与集成",
        _ => Group.ToString()
    };
}

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly Dictionary<string, Type> _pageTypes;

    public MainWindowViewModel(string? initialToolId = null)
    {
        _pageTypes = DiscoverToolPages();
        Groups = BuildGroups();

        var initial = Groups.SelectMany(g => g.Items)
            .FirstOrDefault(i => i.Definition.Id == initialToolId)
            ?? Groups.SelectMany(g => g.Items).FirstOrDefault(i => i.Definition.Id == "dashboard")
            ?? Groups.SelectMany(g => g.Items).First();

        SelectedTool = initial;
    }

    public IReadOnlyList<ToolGroupViewModel> Groups { get; }

    [ObservableProperty] private ToolItemViewModel? _selectedTool;
    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private string _currentTitle = string.Empty;
    [ObservableProperty] private string _currentDescription = string.Empty;

    public string PlatformLine =>
        $"NetCable · {PlatformInfo.DisplayName} {PlatformInfo.Architecture} · .NET {Environment.Version} · " +
        $"工具 {ToolCatalog.All.Count(t => t.Status == ToolStatus.Implemented)}/{ToolCatalog.All.Count} 已实现";

    partial void OnSelectedToolChanged(ToolItemViewModel? value)
    {
        if (value is null) return;

        foreach (var item in Groups.SelectMany(g => g.Items))
            item.IsSelected = ReferenceEquals(item, value);

        CurrentTitle = value.Definition.Title;
        CurrentDescription = value.Definition.Description;
        CurrentPage = CreatePage(value.Definition);
    }

    /// <summary>自动化/冒烟测试用：直接触发当前工具页面的 Run 命令。</summary>
    public void AutoRunCurrentPage()
    {
        if (CurrentPage is not ToolViewModelBase page) return;
        if (page.RunCommand is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand asyncCommand)
            _ = asyncCommand.ExecuteAsync(null);
    }

    private object CreatePage(ToolDefinition definition)
    {
        // 有页面实现就直接用（PlatformLimited 只是说明平台依赖，不代表没实现）；
        // 没有页面实现才回退到占位页。
        if (!_pageTypes.TryGetValue(definition.Id, out var type))
            return new PlaceholderViewModel(definition);

        try
        {
            return Activator.CreateInstance(type) ?? new PlaceholderViewModel(definition);
        }
        catch (Exception ex)
        {
            return new ErrorPageViewModel(definition, ex);
        }
    }

    private IReadOnlyList<ToolGroupViewModel> BuildGroups() =>
        ToolCatalog.ByGroup
            .Select(g => new ToolGroupViewModel(g.Key, g.Select(d => new ToolItemViewModel(this, d)).ToList()))
            .ToList();

    private static Dictionary<string, Type> DiscoverToolPages()
    {
        var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(MainWindowViewModel).Assembly;
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(ViewModelBase).IsAssignableFrom(type)) continue;
            var attr = type.GetCustomAttribute<ToolPageAttribute>();
            if (attr is not null) map[attr.ToolId] = type;
        }
        return map;
    }
}

public sealed class ErrorPageViewModel : ViewModelBase
{
    public ErrorPageViewModel(ToolDefinition definition, Exception exception)
    {
        Title = definition.Title;
        Message = exception.Message;
    }

    public string Title { get; }
    public string Message { get; }
}
