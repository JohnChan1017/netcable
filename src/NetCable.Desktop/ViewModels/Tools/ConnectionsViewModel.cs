using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NetCable.Core.Models;
using NetCable.Services.Platform;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>TCP/UDP 连接表的一行（供自定义表格使用）。</summary>
public sealed class ConnectionRow
{
    public ConnectionRow(ConnectionInfo info)
    {
        Protocol = info.Protocol;
        Local = info.LocalEndPoint;
        Remote = info.RemoteEndPoint;
        State = info.State;
        Pid = info.Pid?.ToString() ?? "—";
        Process = info.ProcessName ?? "—";
    }

    public string Protocol { get; }
    public string Local { get; }
    public string Remote { get; }
    public string State { get; }
    public string Pid { get; }
    public string Process { get; }
}

/// <summary>当前连接列表（TCP/UDP 及对应进程）。</summary>
[ToolPage("connections")]
public sealed partial class ConnectionsViewModel : ToolViewModelBase
{
    private readonly ConnectionService _service = new();

    public ConnectionsViewModel() : base("connections") { }

    public ObservableCollection<ConnectionRow> Connections { get; } = new();

    [ObservableProperty] private string _summary = "—";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Connections.Clear();
        Summary = "读取中…";

        LogInfo("枚举当前 TCP/UDP 连接…");
        var list = await _service.GetConnectionsAsync(false, cancellationToken);
        foreach (var connection in list)
            Connections.Add(new ConnectionRow(connection));

        Summary = $"共 {list.Count} 条连接";
        LogOk($"读取到 {list.Count} 条连接");

        var groups = list.GroupBy(c => c.Protocol).OrderByDescending(g => g.Count()).ToList();
        if (groups.Count > 0)
            LogMuted("协议分布：" + string.Join(" · ", groups.Select(g => $"{g.Key} {g.Count()}")));
    }
}
