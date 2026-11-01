using System.ComponentModel;
using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Services.Platform.Internal;

namespace NetCable.Services.Platform;

/// <summary>
/// WiFi AP 详情服务：直接调用 Windows WLAN API（wlanapi.dll）读取周边 AP 的
/// beacon / probe-response 信息元素并解析（RSN/WPA、HT/VHT/HE/EHT、信道、厂商 IE 等）。
/// 非管理员会话下 <c>WlanGetNetworkBssList</c> 会返回错误 5，此时
/// <see cref="GetAccessStateAsync"/> 给出 RequiresElevation=true 及中文原因，
/// 由 UI 通过提权桥（以管理员身份重新拉起自身）读取。
/// 所有 P/Invoke 错误都转换为中文结果，不抛未捕获异常。
/// </summary>
public sealed class WifiBssService : IWifiBssService
{
    /// <summary>检测当前进程能否读取 AP 列表；不能时给出原因与是否需要提权。</summary>
    public Task<WifiAccessState> GetAccessStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!OperatingSystem.IsWindows())
            {
                return Task.FromResult(new WifiAccessState(
                    false,
                    false,
                    "当前平台不支持 Windows WLAN API 的 AP 详情读取（仅 Windows 可用）",
                    "请改用 WiFi 页面查看本机当前连接与周边 AP 概览"));
            }

            // 只查询第一个接口，用于快速判断权限
            var outcome = WifiBssNative.ReadBssList(singleInterfaceOnly: true);

            if (outcome.InterfaceCount == 0)
            {
                return Task.FromResult(new WifiAccessState(
                    false,
                    false,
                    "未检测到无线网卡（WlanEnumInterfaces 未返回无线接口）",
                    "请确认无线网卡已启用，且 WLAN AutoConfig (wlansvc) 服务正在运行"));
            }

            var interfaceHint = outcome.InterfaceDescriptions.Count > 0
                ? $"无线接口：{string.Join(" / ", outcome.InterfaceDescriptions)}"
                : null;

            if (outcome.ErrorCode == WifiBssNative.ErrorSuccess)
            {
                return Task.FromResult(new WifiAccessState(
                    true,
                    false,
                    null,
                    interfaceHint is null ? "WLAN API 可用，可直接读取 AP 详情" : $"{interfaceHint}；可直接读取 AP 详情"));
            }

            if (outcome.ErrorCode == WifiBssNative.ErrorAccessDenied)
            {
                return Task.FromResult(new WifiAccessState(
                    false,
                    true,
                    "Windows 拒绝访问 WLAN 信息（错误 5）。需要管理员权限。",
                    "点击“以管理员身份重试”后会通过 UAC 提权读取 AP 列表"));
            }

            var detail = DescribeError(outcome.ErrorCode, outcome.ErrorMessage);
            return Task.FromResult(new WifiAccessState(
                false,
                false,
                $"读取 WLAN AP 列表失败（错误 {outcome.ErrorCode}{detail}）",
                "请确认 WLAN AutoConfig (wlansvc) 服务正在运行，或尝试以管理员身份运行 NetCable"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(new WifiAccessState(
                false,
                false,
                $"读取 WLAN 权限状态失败：{ex.Message}",
                null));
        }
    }

    /// <summary>读取周边 AP 详情（含 beacon/probe-response 的 IE 解析）；失败时返回空列表。</summary>
    public Task<IReadOnlyList<WifiBssDetail>> GetBssListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!OperatingSystem.IsWindows())
            {
                return Task.FromResult<IReadOnlyList<WifiBssDetail>>(Array.Empty<WifiBssDetail>());
            }

            var outcome = WifiBssNative.ReadBssList(singleInterfaceOnly: false);
            if (outcome.Entries.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<WifiBssDetail>>(Array.Empty<WifiBssDetail>());
            }

            var details = new List<WifiBssDetail>(outcome.Entries.Count);
            var seenBssids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in outcome.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seenBssids.Add(entry.Bssid)) continue;

                var ie = WifiIeParser.Parse(entry.IeBytes, entry.CapabilityRaw, entry.PhyType);
                var ssid = string.IsNullOrWhiteSpace(entry.Ssid) ? ie.Ssid : entry.Ssid;
                if (string.IsNullOrWhiteSpace(ssid)) ssid = "（隐藏网络）";

                details.Add(new WifiBssDetail(
                    ssid,
                    entry.Bssid,
                    WifiIeParser.ChannelFromFrequency(entry.FrequencyMhz),
                    (int)Math.Min(entry.FrequencyMhz, int.MaxValue),
                    entry.RssiDbm,
                    entry.SignalQuality,
                    entry.BeaconPeriod,
                    entry.CapabilityRaw,
                    ie.Security,
                    ie.Cipher,
                    ie.Akm,
                    ie.PhyType,
                    ie.Vendor,
                    ie.Summary));
            }

            return Task.FromResult<IReadOnlyList<WifiBssDetail>>(details);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Task.FromResult<IReadOnlyList<WifiBssDetail>>(Array.Empty<WifiBssDetail>());
        }
    }

    private static string DescribeError(uint errorCode, string? fallbackMessage)
    {
        string? message = null;
        if (!string.IsNullOrWhiteSpace(fallbackMessage))
        {
            message = fallbackMessage;
        }
        else
        {
            try
            {
                message = new Win32Exception((int)errorCode).Message;
            }
            catch
            {
                message = null;
            }
        }

        return string.IsNullOrWhiteSpace(message) ? string.Empty : $"：{message}";
    }
}
