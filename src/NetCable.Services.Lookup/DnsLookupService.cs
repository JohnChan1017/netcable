using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DnsClient;
using DnsClient.Protocol;
using NetCable.Core.Contracts;
using NetCable.Core.Models;
using DnsQueryOptions = NetCable.Core.Models.DnsQueryOptions;

namespace NetCable.Services.Lookup;

/// <summary>
/// 基于 DnsClient 的跨平台 DNS 查询服务。未指定服务器时读取系统 DNS
/// （Windows 网卡/ipconfig、macOS scutil、Linux /etc/resolv.conf），
/// 支持 A/AAAA/CNAME/MX/NS/TXT/SOA/PTR/SRV/CAA 及 ANY。
/// </summary>
public sealed class DnsLookupService : IDnsLookupService
{
    /// <summary>执行一次 DNS 查询。超时、解析失败或网络异常都会写入结果的 Error，不抛异常。</summary>
    public async Task<DnsQueryResult> QueryAsync(
        DnsQueryOptions options,
        CancellationToken cancellationToken = default)
    {
        var name = options.Name?.Trim() ?? string.Empty;
        var timeoutMs = options.TimeoutMs > 0 ? options.TimeoutMs : 3000;
        var stopwatch = Stopwatch.StartNew();
        var serverLabel = string.IsNullOrWhiteSpace(options.Server) ? "系统默认 DNS" : options.Server.Trim();

        if (name.Length == 0)
        {
            return new DnsQueryResult(name, serverLabel, TimeSpan.Zero,
                Array.Empty<DnsRecordInfo>(), "查询名称为空");
        }

        try
        {
            var serverAddresses = await ResolveNameServersAsync(options.Server, cancellationToken).ConfigureAwait(false);

            LookupClientOptions lookupOptions;
            if (serverAddresses.Length > 0)
            {
                serverLabel = string.Join(", ", serverAddresses.Select(ip => ip.ToString()));
                lookupOptions = CreateOptions(serverAddresses, timeoutMs);
            }
            else
            {
                lookupOptions = CreateOptions(Array.Empty<IPAddress>(), timeoutMs);
                lookupOptions.AutoResolveNameServers = true;
            }

            var client = new LookupClient(lookupOptions);
            using var cts = LookupHttp.CreateTimeoutSource(cancellationToken, timeoutMs + 500);

            IDnsQueryResponse response;
            if (options.Type == DnsRecordType.Ptr && IPAddress.TryParse(name, out var reverseAddress))
            {
                response = await client.QueryReverseAsync(reverseAddress, cts.Token).ConfigureAwait(false);
            }
            else
            {
                response = await client
                    .QueryAsync(name, ToQueryType(options.Type), QueryClass.IN, cts.Token)
                    .ConfigureAwait(false);
            }

            stopwatch.Stop();
            var records = response.Answers.Select(ToRecordInfo).ToList();
            var error = BuildError(response);
            return new DnsQueryResult(name, serverLabel, stopwatch.Elapsed, records, error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new DnsQueryResult(name, serverLabel, stopwatch.Elapsed,
                Array.Empty<DnsRecordInfo>(), $"DNS 查询超时（{timeoutMs} ms）");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new DnsQueryResult(name, serverLabel, stopwatch.Elapsed,
                Array.Empty<DnsRecordInfo>(), LookupErrors.Describe(ex, timeoutMs));
        }
    }

    private static LookupClientOptions CreateOptions(IPAddress[] servers, int timeoutMs)
    {
        var options = servers.Length > 0 ? new LookupClientOptions(servers) : new LookupClientOptions();
        options.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
        options.Retries = 1;
        options.UseCache = false;
        options.ThrowDnsErrors = false;
        options.ContinueOnDnsError = false;
        options.ContinueOnEmptyResponse = true;
        options.UseTcpFallback = true;
        options.Recursion = true;
        options.UseRandomNameServer = false;
        return options;
    }

    private static async Task<IPAddress[]> ResolveNameServersAsync(
        string? server,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(server))
        {
            var value = server.Trim();
            if (IPAddress.TryParse(value, out var address))
                return IsUsable(address) ? new[] { address } : Array.Empty<IPAddress>();

            try
            {
                var addresses = await Dns.GetHostAddressesAsync(value, cancellationToken).ConfigureAwait(false);
                return addresses.Where(IsUsable).Distinct().ToArray();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Array.Empty<IPAddress>();
            }
        }

        var systemServers = await SystemDnsResolver.GetServersAsync(cancellationToken).ConfigureAwait(false);
        return systemServers
            .Select(ip => IPAddress.TryParse(ip, out var address) ? address : null)
            .Where(address => address is not null)
            .Select(address => address!)
            .ToArray();
    }

    private static bool IsUsable(IPAddress address) =>
        address.AddressFamily switch
        {
            AddressFamily.InterNetwork => !address.Equals(IPAddress.Any),
            AddressFamily.InterNetworkV6 => !address.IsIPv6LinkLocal && !address.IsIPv6Multicast,
            _ => false
        };

    private static QueryType ToQueryType(DnsRecordType type) => type switch
    {
        DnsRecordType.A => QueryType.A,
        DnsRecordType.Aaaa => QueryType.AAAA,
        DnsRecordType.Cname => QueryType.CNAME,
        DnsRecordType.Mx => QueryType.MX,
        DnsRecordType.Ns => QueryType.NS,
        DnsRecordType.Txt => QueryType.TXT,
        DnsRecordType.Soa => QueryType.SOA,
        DnsRecordType.Ptr => QueryType.PTR,
        DnsRecordType.Srv => QueryType.SRV,
        DnsRecordType.Caa => QueryType.CAA,
        _ => QueryType.ANY
    };

    private static DnsRecordInfo ToRecordInfo(DnsResourceRecord record)
    {
        var value = record switch
        {
            ARecord a => a.Address.ToString(),
            AaaaRecord aaaa => aaaa.Address.ToString(),
            CNameRecord cname => cname.CanonicalName.ToString(),
            MxRecord mx => $"{mx.Preference} {mx.Exchange}",
            NsRecord ns => ns.NSDName.ToString(),
            PtrRecord ptr => ptr.PtrDomainName.ToString(),
            SoaRecord soa => $"{soa.MName} {soa.RName} {soa.Serial} {soa.Refresh} {soa.Retry} {soa.Expire} {soa.Minimum}",
            SrvRecord srv => $"{srv.Priority} {srv.Weight} {srv.Port} {srv.Target}",
            TxtRecord txt => JoinTxt(txt),
            CaaRecord caa => $"{caa.Flags} {caa.Tag} \"{caa.Value}\"",
            _ => record.ToString() ?? string.Empty
        };

        return new DnsRecordInfo(
            record.DomainName.ToString(),
            record.RecordType.ToString(),
            value,
            record.TimeToLive);
    }

    private static string JoinTxt(TxtRecord txt)
    {
        var parts = txt.Text.Count > 0 ? txt.Text.ToArray() : txt.EscapedText.ToArray();
        return string.Join(" ", parts);
    }

    private static string? BuildError(IDnsQueryResponse response)
    {
        if (!response.HasError) return null;
        if (!string.IsNullOrWhiteSpace(response.ErrorMessage))
            return $"DNS 响应错误：{response.ErrorMessage}";
        return $"DNS 响应错误：{response.Header.ResponseCode}";
    }
}
