using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NetCable.Services.Platform;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>监听端口（处于 LISTEN 状态的连接及对应进程）。</summary>
[ToolPage("listeners")]
public sealed partial class ListenersViewModel : ToolViewModelBase
{
    private readonly ConnectionService _service = new();

    public ListenersViewModel() : base("listeners") { }

    public ObservableCollection<ConnectionRow> Connections { get; } = new();

    [ObservableProperty] private string _summary = "—";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Connections.Clear();
        Summary = "读取中…";

        LogInfo("枚举本机监听端口…");
        var list = await _service.GetConnectionsAsync(true, cancellationToken);
        foreach (var connection in list)
            Connections.Add(new ConnectionRow(connection));

        Summary = $"共 {list.Count} 个监听端口";
        LogOk($"读取到 {list.Count} 个监听端口");

        var groups = list.GroupBy(c => c.Protocol).OrderByDescending(g => g.Count()).ToList();
        if (groups.Count > 0)
            LogMuted("协议分布：" + string.Join(" · ", groups.Select(g => $"{g.Key} {g.Count()}")));
    }
}
