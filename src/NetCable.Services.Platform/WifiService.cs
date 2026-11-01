using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Core.Platform;
using NetCable.Services.Platform.Internal;

namespace NetCable.Services.Platform;

/// <summary>
/// WiFi 服务：
/// Windows 解析 <c>netsh wlan show interfaces</c> / <c>show networks mode=bssid</c>；
/// macOS 优先 <c>airport</c>，回退 <c>system_profiler SPAirPortDataType -json</c>；
/// Linux 优先 <c>nmcli</c>，回退 <c>iw</c>。
/// 没有无线网卡、命令缺失、无权限（例如 Windows 11 24H2 需要管理员）时返回空结果并通过
/// <see cref="IsSupported"/> / <see cref="UnsupportedReason"/> 说明原因，不会抛异常。
/// </summary>
public sealed class WifiService : IWifiService
{
    private readonly WindowsWifiReader? _windowsReader;
    private readonly LinuxWifiReader? _linuxReader;
    private readonly MacWifiReader? _macReader;
    private readonly SemaphoreSlim _probeGate = new(1, 1);

    private bool _supported = true;
    private string? _unsupportedReason;
    private bool _windowsProbed;

    /// <summary>创建 WiFi 服务并做一次命令可用性探测（不执行耗时命令）。</summary>
    public WifiService()
    {
        switch (PlatformInfo.Family)
        {
            case OsFamily.Windows:
                if (Commanding.Exists("netsh"))
                {
                    _windowsReader = new WindowsWifiReader();
                }
                else
                {
                    _supported = false;
                    _unsupportedReason = "未找到 netsh 命令，无法读取 WLAN 信息";
                }

                break;

            case OsFamily.MacOS:
                _macReader = new MacWifiReader();
                if (!_macReader.HasSystemProfiler && MacWifiReader.AirportPath is null)
                {
                    _supported = false;
                    _unsupportedReason = "未找到 system_profiler 与 airport，无法读取 WiFi 信息";
                }

                break;

            case OsFamily.Linux:
            {
                var hasNmcli = Commanding.Exists("nmcli");
                var hasIw = Commanding.Exists("iw");
                if (!hasNmcli && !hasIw)
                {
                    _supported = false;
                    _unsupportedReason = "未找到 nmcli 与 iw 命令，无法读取 WiFi 信息";
                }
                else
                {
                    _linuxReader = new LinuxWifiReader(hasNmcli, hasIw);
                }

                break;
            }

            default:
                _supported = false;
                _unsupportedReason = "当前平台不支持 WiFi 查询";
                break;
        }
    }

    /// <summary>
    /// 当前平台/权限是否支持读取 WiFi 信息。
    /// Windows 首次访问该属性时会同步执行一次 <c>netsh wlan show interfaces</c>（约几十到几百毫秒），
    /// 以便在 UI 首次渲染时就能得到“无网卡/需管理员/服务未启动”的准确结论。
    /// </summary>
    public bool IsSupported
    {
        get
        {
            EnsureWindowsProbedSynchronously();
            return _supported;
        }
    }

    /// <summary>不支持时的原因说明，可用时为 null。</summary>
    public string? UnsupportedReason
    {
        get
        {
            EnsureWindowsProbedSynchronously();
            return _unsupportedReason;
        }
    }

    /// <summary>读取当前已连接的 WiFi；未连接或读取失败返回 null。</summary>
    public async Task<WifiNetworkInfo?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_windowsReader is not null)
            {
                if (!await EnsureWindowsReadyAsync(cancellationToken).ConfigureAwait(false)) return null;
                var text = await _windowsReader.GetInterfacesTextAsync(cancellationToken).ConfigureAwait(false);
                return text is null ? null : WindowsWifiReader.ParseCurrent(text);
            }

            if (_macReader is not null) return await _macReader.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (_linuxReader is not null) return await _linuxReader.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>扫描周边 WiFi；不可用时返回空列表。</summary>
    public async Task<IReadOnlyList<WifiNetworkInfo>> ScanAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_windowsReader is not null)
            {
                if (!await EnsureWindowsReadyAsync(cancellationToken).ConfigureAwait(false))
                {
                    return Array.Empty<WifiNetworkInfo>();
                }

                var interfacesText = await _windowsReader.GetInterfacesTextAsync(cancellationToken).ConfigureAwait(false);
                var current = interfacesText is null ? null : WindowsWifiReader.ParseCurrent(interfacesText);
                var networksText = await _windowsReader.GetNetworksTextAsync(cancellationToken).ConfigureAwait(false);
                if (networksText is null)
                {
                    return current is null ? Array.Empty<WifiNetworkInfo>() : new[] { current };
                }

                var networks = WindowsWifiReader.ParseNetworks(networksText, current).ToList();
                if (current is not null && networks.All(n => !n.IsConnected)) networks.Insert(0, current);
                return networks;
            }

            if (_macReader is not null) return await _macReader.ScanAsync(cancellationToken).ConfigureAwait(false);
            if (_linuxReader is not null) return await _linuxReader.ScanAsync(cancellationToken).ConfigureAwait(false);
            return Array.Empty<WifiNetworkInfo>();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<WifiNetworkInfo>();
        }
    }

    /// <summary>
    /// 同步探测 Windows WiFi 可用性（首次访问 <see cref="IsSupported"/> 时触发）。
    /// 只执行一次，后续直接读缓存结果；所有内部 await 都使用 ConfigureAwait(false)，不会死锁。
    /// </summary>
    private void EnsureWindowsProbedSynchronously()
    {
        if (_windowsReader is null || _windowsProbed) return;

        try
        {
            EnsureWindowsReadyAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // 探测本身失败不应影响属性读取
            _windowsProbed = true;
        }
    }

    /// <summary>
    /// Windows 首次查询时执行一次 <c>netsh wlan show interfaces</c>，据此判定
    /// “无无线网卡 / 服务未运行 / 需要管理员权限”等不可用状态。
    /// </summary>
    private async Task<bool> EnsureWindowsReadyAsync(CancellationToken cancellationToken)
    {
        if (_windowsReader is null) return false;
        if (_windowsProbed) return _supported;

        await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_windowsProbed) return _supported;

            var text = await _windowsReader.GetInterfacesTextAsync(cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                _supported = false;
                _unsupportedReason = "无法执行 netsh wlan show interfaces（命令不存在、超时或无权限）";
            }
            else
            {
                var reason = WindowsWifiReader.DetectUnavailableReason(text);
                if (reason is not null)
                {
                    _supported = false;
                    _unsupportedReason = reason;
                }
            }

            _windowsProbed = true;
            return _supported;
        }
        finally
        {
            _probeGate.Release();
        }
    }
}
