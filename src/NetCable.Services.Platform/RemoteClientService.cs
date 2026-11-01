using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Services.Platform.Internal;

namespace NetCable.Services.Platform;

/// <summary>
/// 远程客户端统一服务：把 RDP / SSH / VNC / PowerShell 映射到当前操作系统的原生客户端，
/// 提供能力探测、命令行预览（<see cref="BuildCommandAsync"/>）与真正拉起（<see cref="LaunchAsync"/>）。
/// 目标格式支持 host、host:port、user@host、user@host:port（IPv6 用方括号）。
/// 客户端未安装或启动失败时返回中文说明，不抛异常。
/// </summary>
public sealed class RemoteClientService : IRemoteClientService
{
    /// <summary>返回 4 个远程客户端的能力信息（未安装的返回 IsAvailable=false 与中文提示）。</summary>
    public Task<IReadOnlyList<RemoteClientInfo>> GetClientsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            IReadOnlyList<RemoteClientInfo> clients = RemoteClients.Detect()
                .Select(client => new RemoteClientInfo(
                    client.Id,
                    client.Title,
                    client.Protocol,
                    client.IsAvailable,
                    client.Executable,
                    client.Hint))
                .ToList();

            return Task.FromResult(clients);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Task.FromResult<IReadOnlyList<RemoteClientInfo>>(Array.Empty<RemoteClientInfo>());
        }
    }

    /// <summary>返回将要执行的命令行字符串（不执行）；客户端不存在时返回中文说明。</summary>
    public Task<string> BuildCommandAsync(string clientId, string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var client = FindClient(clientId);
            if (client is null) return Task.FromResult(UnknownClientMessage(clientId));

            RemoteTarget? parsed = null;
            if (client.Kind != RemoteClientKind.PowerShell &&
                !RemoteClients.TryParseTarget(target, out parsed, out var error))
            {
                return Task.FromResult(error ?? "目标格式无效");
            }

            return Task.FromResult(RemoteClients.BuildCommand(client, parsed));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult($"构建命令失败：{ex.Message}");
        }
    }

    /// <summary>启动系统原生客户端并返回说明文本；客户端未安装/启动失败时返回中文原因。</summary>
    public Task<string> LaunchAsync(string clientId, string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var client = FindClient(clientId);
            if (client is null) return Task.FromResult(UnknownClientMessage(clientId));
            if (!client.IsAvailable)
            {
                return Task.FromResult($"未检测到 {client.Title} 客户端：{client.Hint ?? "请先安装对应客户端"}");
            }

            RemoteTarget? parsed = null;
            if (client.Kind != RemoteClientKind.PowerShell &&
                !RemoteClients.TryParseTarget(target, out parsed, out var error))
            {
                return Task.FromResult(error ?? "目标格式无效");
            }

            return Task.FromResult(RemoteClients.Launch(client, parsed));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult($"启动失败：{ex.Message}");
        }
    }

    private static RemoteClient? FindClient(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return null;
        return RemoteClients.Detect()
            .FirstOrDefault(client => string.Equals(client.Id, clientId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string UnknownClientMessage(string? clientId) =>
        $"不支持的客户端：{clientId}（可选：rdp、ssh、vnc、powershell）";
}
