using CommunityToolkit.Mvvm.ComponentModel;
using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Services.Lookup;
using NetCable.Services.Platform;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>IP 归属：查询指定 IP 或本机公网出口的国家 / 城市 / ASN / 运营商。</summary>
[ToolPage("ipgeo")]
public sealed partial class IpGeoViewModel : ToolViewModelBase
{
    private readonly IIpGeolocationService _geo = new IpGeolocationService();
    private readonly INetworkInfoService _network = new NetworkInfoService();

    public IpGeoViewModel() : base("ipgeo")
    {
        Summary = "留空则查询本机公网出口 IP";
    }

    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private string _summary = "—";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Summary = "查询中…";

        var target = Query.Trim();
        if (target.Length == 0)
        {
            LogInfo("地址留空，先解析本机公网出口 IP…");
            try
            {
                var publicIp = await _network.GetPublicIpAsync(cancellationToken).ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(publicIp))
                {
                    target = publicIp.Trim();
                    LogOk($"本机公网出口 IP：{target}");
                }
                else
                {
                    LogWarn("公网 IP 未获取到，改由归属服务解析本机出口");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogWarn($"公网 IP 解析失败：{ex.Message}，改由归属服务解析本机出口");
            }
        }

        LogInfo(target.Length == 0 ? "查询本机公网归属…" : $"查询 {target} 的归属信息…");

        IpGeoInfo info;
        try
        {
            info = await _geo.LookupAsync(target, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            Summary = "查询失败";
            throw;
        }

        if (info is null)
        {
            Summary = "查询失败：无返回数据";
            LogError("归属服务返回空结果");
            return;
        }

        AddRow("IP 地址", info.Ip, "查询目标");
        AddRow("国家 / 地区", info.Country ?? "—", info.CountryCode ?? "—");
        AddRow("省 / 州", info.Region ?? "—");
        AddRow("城市", info.City ?? "—");
        AddRow("ISP 运营商", info.Isp ?? "—");
        AddRow("所属组织", info.Organization ?? "—");
        AddRow("ASN", info.Asn ?? "—");
        AddRow("经纬度", FormatCoordinate(info.Latitude, info.Longitude));
        AddRow("时区", info.TimeZone ?? "—");

        Summary = BuildSummary(info);
        LogOk($"归属：{Summary}");
    }

    private static string BuildSummary(IpGeoInfo info)
    {
        var parts = new List<string> { info.Ip };
        if (!string.IsNullOrWhiteSpace(info.Country)) parts.Add(info.Country!);
        if (!string.IsNullOrWhiteSpace(info.Region)) parts.Add(info.Region!);
        if (!string.IsNullOrWhiteSpace(info.City)) parts.Add(info.City!);
        if (!string.IsNullOrWhiteSpace(info.Asn)) parts.Add(info.Asn!);
        if (!string.IsNullOrWhiteSpace(info.Isp)) parts.Add(info.Isp!);
        return string.Join(" · ", parts);
    }

    private static string FormatCoordinate(double? latitude, double? longitude)
    {
        if (latitude is null || longitude is null) return "—";
        return $"{latitude.Value:0.####}, {longitude.Value:0.####}";
    }
}
