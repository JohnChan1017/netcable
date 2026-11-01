using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Services.Platform.Internal;

namespace NetCable.Services.Platform;

/// <summary>
/// 邻居表（同网段 IP ↔ MAC 映射）服务。
/// Windows 解析 <c>arp -a</c>；Linux 解析 <c>ip neigh</c>（回退 <c>/proc/net/arp</c>）；
/// macOS 解析 <c>arp -an</c>。命令缺失/无权限时返回空列表，不抛异常。
/// </summary>
public sealed class NeighborService : INeighborService
{
    /// <summary>读取本机邻居表。</summary>
    public async Task<IReadOnlyList<NeighborEntry>> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await NeighborReaders.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<NeighborEntry>();
        }
    }
}
