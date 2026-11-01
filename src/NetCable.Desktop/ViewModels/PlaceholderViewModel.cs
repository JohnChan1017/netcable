using NetCable.Core.Tools;

namespace NetCable.Desktop.ViewModels;

public sealed class PlaceholderViewModel : ViewModelBase
{
    public PlaceholderViewModel(ToolDefinition definition) => Definition = definition;

    public ToolDefinition Definition { get; }
    public string Title => Definition.Title;
    public string Description => Definition.Description;

    public string Note => Definition.Status switch
    {
        ToolStatus.Planned => "该工具在 M1 里程碑中尚未实现，已列入后续计划。",
        ToolStatus.PlatformLimited => Definition.PlatformNote ?? "该工具依赖平台特定的客户端程序。",
        _ => "该工具尚未接入。"
    };
}
