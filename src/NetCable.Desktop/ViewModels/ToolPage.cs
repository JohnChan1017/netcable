namespace NetCable.Desktop.ViewModels;

/// <summary>
/// 标记工具页面 ViewModel 对应的工具 Id（来自 ToolCatalog）。
/// 新增工具页面时只需给 ViewModel 加此特性，无需修改任何共享文件。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ToolPageAttribute(string toolId) : Attribute
{
    public string ToolId { get; } = toolId;
}
