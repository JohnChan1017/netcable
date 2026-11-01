using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Services.Platform.Internal;

namespace NetCable.Services.Platform;

/// <summary>
/// TCP/UDP 连接与监听端口服务。
/// Windows 解析 <c>netstat -ano</c>；Linux 解析 <c>ss -tunap</c>（回退 <c>netstat</c>）；
/// macOS 解析 <c>netstat</c> 并用 <c>lsof</c> 补全进程信息。
/// 无权限/命令缺失时返回空列表，不抛异常。
/// </summary>
public sealed class ConnectionService : IConnectionService
{
    /// <summary>读取连接表；<paramref name="listeningOnly"/> 为 true 时只返回监听中的端点。</summary>
    public async Task<IReadOnlyList<ConnectionInfo>> GetConnectionsAsync(
        bool listeningOnly,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var connections = await ConnectionReaders.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!listeningOnly) return connections;
            return connections.Where(IsListening).ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<ConnectionInfo>();
        }
    }

    private static bool IsListening(ConnectionInfo connection) =>
        connection.State.Contains("LISTEN", StringComparison.OrdinalIgnoreCase)
        || connection.State.Contains("UNCONN", StringComparison.OrdinalIgnoreCase);
}
