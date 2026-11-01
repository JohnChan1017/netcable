using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NetCable.Core.Models;
using NetCable.Services.Platform;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>ARP / 邻居缓存表的一行。</summary>
public sealed class NeighborRow
{
    public NeighborRow(NeighborEntry entry)
    {
        IpAddress = entry.IpAddress;
        MacAddress = entry.MacAddress;
        State = entry.State;
        Interface = entry.Interface;
    }

    public string IpAddress { get; }
    public string MacAddress { get; }
    public string State { get; }
    public string Interface { get; }
}

/// <summary>ARP / 邻居缓存：同网段 IP ↔ MAC 映射。</summary>
[ToolPage("neighbors")]
public sealed partial class NeighborsViewModel : ToolViewModelBase
{
    private readonly NeighborService _service = new();

    public NeighborsViewModel() : base("neighbors") { }

    public ObservableCollection<NeighborRow> Entries { get; } = new();

    [ObservableProperty] private string _summary = "—";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Entries.Clear();
        Summary = "读取中…";

        LogInfo("读取 ARP / 邻居缓存…");
        var list = await _service.GetAsync(cancellationToken);
        foreach (var entry in list.OrderBy(e => e.IpAddress, StringComparer.OrdinalIgnoreCase))
            Entries.Add(new NeighborRow(entry));

        Summary = $"共 {list.Count} 条邻居记录";
        LogOk($"读取到 {list.Count} 条邻居记录");
    }
}
