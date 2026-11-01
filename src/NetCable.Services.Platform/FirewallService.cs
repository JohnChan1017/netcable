using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Services.Platform.Internal;

namespace NetCable.Services.Platform;

/// <summary>
/// 防火墙只读服务：Windows 解析 <c>netsh advfirewall</c>（非管理员回退注册表），
/// Linux 解析 <c>nft</c> / <c>iptables</c>，macOS 解析 <c>pfctl</c>。
/// 只读取状态与规则，不修改任何配置；权限不足/命令缺失返回 Supported=false + 中文原因。
/// </summary>
public sealed class FirewallService : IFirewallService
{
    /// <summary>读取防火墙状态（是否支持、开关状态、配置文件/引擎说明）。</summary>
    public async Task<FirewallStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await FirewallReaders.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new FirewallStatus(false, "读取防火墙状态失败", null, null, null);
        }
    }

    /// <summary>读取防火墙规则列表；无权限或解析失败时返回空列表。</summary>
    public async Task<IReadOnlyList<FirewallRule>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await FirewallReaders.GetRulesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<FirewallRule>();
        }
    }
}
