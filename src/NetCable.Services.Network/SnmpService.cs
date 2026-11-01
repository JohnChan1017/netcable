using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using NetCable.Core.Contracts;
using NetCable.Core.Models;

namespace NetCable.Services.Network;

/// <summary>
/// SNMP v1 / v2c 客户端（手写 BER 编解码，零第三方依赖）。
/// <para>支持 <b>GET</b>（PDU 0xA0）与 <b>WALK</b>（GETNEXT PDU 0xA1 循环）。UDP 161，超时用
/// <see cref="SnmpRequest.TimeoutMs"/>（默认重试 1 次）。所有网络/解码错误都转成中文消息返回，
/// 不向调用方抛异常；只有用户主动取消才抛 <see cref="OperationCanceledException"/>。</para>
/// <para>值类型解码覆盖：INTEGER / OCTET STRING（可打印文本或十六进制）/ NULL / OID / IpAddress /
/// Counter32 / Gauge32 / UInteger32 / TimeTicks（格式化为 <c>N days hh:mm:ss</c>）/ Counter64 /
/// noSuchObject / noSuchInstance / endOfMibView。</para>
/// </summary>
public sealed class SnmpService : ISnmpService
{
    // ---------- 常用 OID（GET 标量需带 .0；WALK 用子树根） ----------
    /// <summary>sysDescr.0 —— 系统描述。</summary>
    public const string SysDescrOid = "1.3.6.1.2.1.1.1.0";

    /// <summary>sysObjectID.0 —— 厂商对象标识。</summary>
    public const string SysObjectIdOid = "1.3.6.1.2.1.1.2.0";

    /// <summary>sysUpTime.0 —— 系统运行时间。</summary>
    public const string SysUpTimeOid = "1.3.6.1.2.1.1.3.0";

    /// <summary>sysContact.0 —— 联系人。</summary>
    public const string SysContactOid = "1.3.6.1.2.1.1.4.0";

    /// <summary>sysName.0 —— 设备名。</summary>
    public const string SysNameOid = "1.3.6.1.2.1.1.5.0";

    /// <summary>sysLocation.0 —— 设备位置。</summary>
    public const string SysLocationOid = "1.3.6.1.2.1.1.6.0";

    /// <summary>ifNumber.0 —— 接口数量。</summary>
    public const string IfNumberOid = "1.3.6.1.2.1.2.1.0";

    /// <summary>system 子树根，适合直接 WALK。</summary>
    public const string SystemSubtreeOid = "1.3.6.1.2.1.1";

    // ---------- 协议常量 ----------
    private const int SnmpPort = 161;
    private const int MaxAttempts = 2;              // 首次发送 + 重试 1 次
    private const int MinTimeoutMs = 200;
    private const int MaxTimeoutMs = 60000;
    private const int MaxWalkRowsLimit = 100000;

    private const byte GetRequestPduTag = 0xA0;
    private const byte GetNextRequestPduTag = 0xA1;
    private const byte GetResponsePduTag = 0xA2;

    private const byte IntegerTag = 0x02;
    private const byte OctetStringTag = 0x04;
    private const byte NullTag = 0x05;
    private const byte OidTag = 0x06;
    private const byte SequenceTag = 0x30;

    private const byte IpAddressTag = 0x40;
    private const byte Counter32Tag = 0x41;
    private const byte Gauge32Tag = 0x42;
    private const byte TimeTicksTag = 0x43;
    private const byte Counter64Tag = 0x46;
    private const byte UInteger32Tag = 0x47;
    private const byte NoSuchObjectTag = 0x80;
    private const byte NoSuchInstanceTag = 0x81;
    private const byte EndOfMibViewTag = 0x82;

    /// <summary>主机名解析超时（与 SNMP 请求超时分开，避免 DNS 卡住整个探测）。</summary>
    private static readonly TimeSpan HostLookupTimeout = TimeSpan.FromMilliseconds(3000);

    /// <inheritdoc />
    public async Task<SnmpResult> GetAsync(SnmpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryValidateRequest(request, out var validationError))
            return new SnmpResult(false, validationError!, Array.Empty<SnmpVarBind>());

        if (!TryBuildMessage(request, GetRequestPduTag, request.Oid, out var message, out var requestId,
                out var buildError))
        {
            return new SnmpResult(false, buildError!, Array.Empty<SnmpVarBind>());
        }

        var address = await NetworkHelpers
            .ResolveAddressAsync(request.Host, HostLookupTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (address is null)
            return new SnmpResult(false, $"无法解析主机名：{request.Host}", Array.Empty<SnmpVarBind>());

        var timeoutMs = Math.Clamp(request.TimeoutMs, MinTimeoutMs, MaxTimeoutMs);
        var endpoint = new IPEndPoint(address, SnmpPort);

        try
        {
            using var client = new UdpClient(address.AddressFamily);
            var response = await ExchangeAsync(client, endpoint, message, requestId, timeoutMs, cancellationToken)
                .ConfigureAwait(false);

            if (response.ErrorStatus != 0)
            {
                return new SnmpResult(false, DescribeErrorStatus(response.ErrorStatus, response.ErrorIndex),
                    response.Variables);
            }

            var expectedVersion = request.Version == 1 ? 0 : 1;
            if (response.Version != expectedVersion)
            {
                return new SnmpResult(false,
                    $"响应版本不匹配：期望 SNMPv{(request.Version == 1 ? "1" : "2c")}，实际 version={response.Version}",
                    response.Variables);
            }

            var messageText = response.Variables.Count == 0
                ? "成功：设备未返回任何变量"
                : $"成功：返回 {response.Variables.Count} 个变量";
            return new SnmpResult(true, messageText, response.Variables);
        }
        catch (OperationCanceledException)
        {
            throw; // 用户取消
        }
        catch (TimeoutException ex)
        {
            return new SnmpResult(false, ex.Message, Array.Empty<SnmpVarBind>());
        }
        catch (InvalidDataException ex)
        {
            return new SnmpResult(false, ex.Message, Array.Empty<SnmpVarBind>());
        }
        catch (SocketException ex)
        {
            return new SnmpResult(false, $"网络错误：{ex.Message}（{endpoint}）", Array.Empty<SnmpVarBind>());
        }
        catch (Exception ex)
        {
            return new SnmpResult(false, $"SNMP 请求失败：{ex.Message}", Array.Empty<SnmpVarBind>());
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SnmpVarBind> WalkAsync(
        SnmpRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryValidateRequest(request, out _)) yield break; // 流式接口没有错误通道：参数非法时返回空流
        if (!TryParseOid(request.Oid, out var rootArcs)) yield break;

        var address = await NetworkHelpers
            .ResolveAddressAsync(request.Host, HostLookupTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (address is null) yield break;

        var timeoutMs = Math.Clamp(request.TimeoutMs, MinTimeoutMs, MaxTimeoutMs);
        var maxRows = Math.Clamp(request.MaxRows, 1, MaxWalkRowsLimit);
        var expectedVersion = request.Version == 1 ? 0 : 1;
        var endpoint = new IPEndPoint(address, SnmpPort);

        var currentOidArcs = rootArcs;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rowCount = 0;

        using var client = new UdpClient(address.AddressFamily);

        while (rowCount < maxRows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentOid = FormatOid(currentOidArcs);
            if (!TryBuildMessage(request, GetNextRequestPduTag, currentOid, out var message, out var requestId,
                    out _))
            {
                yield break;
            }

            SnmpResponse? response = null;
            var exchangeFailed = false;
            try
            {
                response = await ExchangeAsync(client, endpoint, message, requestId, timeoutMs, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 超时/解码失败：让流自然结束（错误详情由 GET 或 UI 日志体现）
                exchangeFailed = true;
            }

            if (exchangeFailed || response is null) yield break;

            if (response.ErrorStatus != 0 || response.Version != expectedVersion) yield break;
            if (response.Variables.Count == 0) yield break;

            var stop = false;
            foreach (var variable in response.Variables)
            {
                // endOfMibView / 越界 被视为 WALK 正常结束（不是错误）
                if (IsExceptionValue(variable.Type)) { stop = true; break; }
                if (!TryParseOid(variable.Oid, out var valueArcs)) { stop = true; break; }

                // 走出请求子树，或没有严格递增（设备异常/回环）→ 结束，避免死循环
                if (!IsUnderRoot(valueArcs, rootArcs)) { stop = true; break; }
                if (!IsStrictlyGreater(valueArcs, currentOidArcs)) { stop = true; break; }
                if (!seen.Add(variable.Oid)) { stop = true; break; }

                currentOidArcs = valueArcs;
                rowCount++;
                yield return variable;

                if (rowCount >= maxRows) { stop = true; break; }
            }

            if (stop) yield break;
        }
    }

    // ======================================================================
    // 校验 / 编码
    // ======================================================================

    private static bool TryValidateRequest(SnmpRequest request, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(request.Host))
        {
            error = "请输入 SNMP 设备地址（IP 或主机名）";
            return false;
        }

        if (string.IsNullOrEmpty(request.Community))
        {
            error = "Community 不能为空（默认通常是 public）";
            return false;
        }

        if (request.Version is not (1 or 2))
        {
            error = $"SNMP 版本无效：{request.Version}（仅支持 1 = v1、2 = v2c）";
            return false;
        }

        if (!TryParseOid(request.Oid, out _))
        {
            error = $"OID 格式无效：{request.Oid}（应形如 1.3.6.1.2.1.1.1.0）";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 构建完整的 SNMP 报文：<c>SEQUENCE { version, community, PDU }</c>。
    /// PDU（0xA0 GET / 0xA1 GETNEXT）内容为 request-id、error-status=0、error-index=0、VarBindList。
    /// </summary>
    private static bool TryBuildMessage(
        SnmpRequest request,
        byte pduTag,
        string oidText,
        out byte[] message,
        out int requestId,
        out string? error)
    {
        message = Array.Empty<byte>();
        requestId = 0;
        error = null;

        var versionCode = request.Version switch { 1 => 0, 2 => 1, _ => -1 };
        if (versionCode < 0)
        {
            error = $"SNMP 版本无效：{request.Version}（仅支持 1 = v1、2 = v2c）";
            return false;
        }

        if (string.IsNullOrEmpty(request.Community))
        {
            error = "Community 不能为空（默认通常是 public）";
            return false;
        }

        if (!TryParseOid(oidText, out var oidArcs))
        {
            error = $"OID 格式无效：{oidText}（应形如 1.3.6.1.2.1.1.1.0）";
            return false;
        }

        requestId = Random.Shared.Next(1, int.MaxValue);

        var varbind = Tlv(SequenceTag, Concat(EncodeOid(oidArcs), new byte[] { NullTag, 0x00 }));
        var varbindList = Tlv(SequenceTag, varbind);
        var pduContent = Concat(
            EncodeInteger(requestId),
            EncodeInteger(0), // error-status
            EncodeInteger(0), // error-index
            varbindList);
        var pdu = Tlv(pduTag, pduContent);

        message = Tlv(SequenceTag, Concat(
            EncodeInteger(versionCode),
            Tlv(OctetStringTag, Encoding.UTF8.GetBytes(request.Community)),
            pdu));

        return true;
    }

    private static byte[] EncodeInteger(long value)
    {
        var bytes = new List<byte>(9);
        var remaining = value;
        while (true)
        {
            bytes.Insert(0, (byte)(remaining & 0xFF));
            remaining >>= 8;
            if (remaining == 0 && (bytes[0] & 0x80) == 0) break;
            if (remaining == -1 && (bytes[0] & 0x80) != 0) break;
        }

        return Tlv(IntegerTag, bytes.ToArray());
    }

    private static byte[] EncodeOid(uint[] arcs)
    {
        var content = new List<byte>(arcs.Length + 4);

        // 前两个弧合并成第一个 subidentifier：40 * arcs[0] + arcs[1]
        WriteBase128(content, (ulong)arcs[0] * 40 + arcs[1]);
        for (var i = 2; i < arcs.Length; i++) WriteBase128(content, arcs[i]);

        return Tlv(OidTag, content.ToArray());
    }

    private static void WriteBase128(List<byte> target, ulong value)
    {
        var groups = new List<byte>(10);
        do
        {
            groups.Insert(0, (byte)(value & 0x7F));
            value >>= 7;
        } while (value > 0);

        for (var i = 0; i < groups.Count - 1; i++) target.Add((byte)(groups[i] | 0x80));
        target.Add(groups[^1]);
    }

    private static byte[] Tlv(byte tag, byte[] content)
    {
        var lengthBytes = EncodeLength(content.Length);
        var result = new byte[1 + lengthBytes.Length + content.Length];
        result[0] = tag;
        Array.Copy(lengthBytes, 0, result, 1, lengthBytes.Length);
        Array.Copy(content, 0, result, 1 + lengthBytes.Length, content.Length);
        return result;
    }

    private static byte[] EncodeLength(int length)
    {
        if (length < 0x80) return new[] { (byte)length };

        var bytes = new List<byte>(4);
        var remaining = (uint)length;
        while (remaining > 0)
        {
            bytes.Insert(0, (byte)(remaining & 0xFF));
            remaining >>= 8;
        }

        var result = new byte[bytes.Count + 1];
        result[0] = (byte)(0x80 | bytes.Count);
        for (var i = 0; i < bytes.Count; i++) result[i + 1] = bytes[i];
        return result;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = 0;
        foreach (var part in parts) total += part.Length;

        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Array.Copy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        return result;
    }

    // ======================================================================
    // UDP 收发（超时 + 重试）
    // ======================================================================

    private static async Task<SnmpResponse> ExchangeAsync(
        UdpClient client,
        IPEndPoint endpoint,
        byte[] message,
        int requestId,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        Exception? lastDecodeError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await client.SendAsync(message, endpoint, cancellationToken).ConfigureAwait(false);

            using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            receiveCts.CancelAfter(timeoutMs);

            while (true)
            {
                UdpReceiveResult datagram;
                try
                {
                    datagram = await client.ReceiveAsync(receiveCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw; // 用户取消
                }
                catch (OperationCanceledException)
                {
                    break; // 本轮超时 → 进入重试
                }
                catch (SocketException)
                {
                    break; // 本机无 SNMP 设备时 UDP 161 常回 ICMP 端口不可达
                }

                try
                {
                    var response = DecodeResponse(datagram.Buffer);
                    if (response.RequestId == requestId) return response;
                    // request-id 不匹配：视为过期/杂包，本轮继续等待
                }
                catch (Exception ex)
                {
                    lastDecodeError = ex;
                    // 可能收到无关 UDP 包，继续等待本轮剩余时间
                }
            }
        }

        if (lastDecodeError is not null)
            throw new InvalidDataException($"响应解析失败：{lastDecodeError.Message}", lastDecodeError);

        throw new TimeoutException(
            $"请求超时：{endpoint} 在 {timeoutMs * MaxAttempts}ms 内无响应（已重试 {MaxAttempts - 1} 次）。" +
            "请确认设备已启用 SNMP、community 正确，且 UDP 161 未被防火墙拦截。");
    }

    // ======================================================================
    // BER 解码
    // ======================================================================

    private static SnmpResponse DecodeResponse(byte[] datagram)
    {
        var outer = new BerReader(datagram);
        var message = outer.ReadTlv();
        if (message.Tag != SequenceTag)
            throw new FormatException($"SNMP 报文应为 SEQUENCE(0x30)，实际 0x{message.Tag:X2}");

        var messageReader = new BerReader(message.Content);
        var version = messageReader.ReadInteger("version");
        messageReader.ReadOctetString("community"); // community 不参与展示，但必须消费掉
        var pdu = messageReader.ReadTlv();
        if (pdu.Tag != GetResponsePduTag)
            throw new FormatException($"期望 GetResponse PDU(0xA2)，实际 0x{pdu.Tag:X2}");

        var pduReader = new BerReader(pdu.Content);
        var requestId = pduReader.ReadInteger("request-id");
        var errorStatus = pduReader.ReadInteger("error-status");
        var errorIndex = pduReader.ReadInteger("error-index");

        var varbindList = pduReader.ReadTlv();
        if (varbindList.Tag != SequenceTag)
            throw new FormatException($"VarBindList 应为 SEQUENCE(0x30)，实际 0x{varbindList.Tag:X2}");

        var variables = new List<SnmpVarBind>();
        var listReader = new BerReader(varbindList.Content);
        while (listReader.HasMore)
        {
            var varbind = listReader.ReadTlv();
            if (varbind.Tag != SequenceTag)
                throw new FormatException($"VarBind 应为 SEQUENCE(0x30)，实际 0x{varbind.Tag:X2}");

            var varbindReader = new BerReader(varbind.Content);
            var oidTlv = varbindReader.ReadTlv();
            if (oidTlv.Tag != OidTag)
                throw new FormatException($"VarBind.name 应为 OID(0x06)，实际 0x{oidTlv.Tag:X2}");

            var oid = DecodeOid(oidTlv.Content);
            var value = varbindReader.ReadTlv();
            variables.Add(DecodeValue(oid, value.Tag, value.Content));
        }

        return new SnmpResponse(version, requestId, errorStatus, errorIndex, variables);
    }

    /// <summary>按标签解码 SNMP 值类型。</summary>
    private static SnmpVarBind DecodeValue(string oid, byte tag, byte[] content)
    {
        switch (tag)
        {
            case IntegerTag:
                return new SnmpVarBind(oid, "INTEGER",
                    DecodeSignedInteger(content).ToString(CultureInfo.InvariantCulture));

            case OctetStringTag:
                return new SnmpVarBind(oid, "OCTET STRING", DecodeOctetStringValue(content));

            case NullTag:
                return new SnmpVarBind(oid, "NULL", string.Empty);

            case OidTag:
                return new SnmpVarBind(oid, "OID", DecodeOid(content));

            case IpAddressTag:
                return new SnmpVarBind(oid, "IpAddress",
                    content.Length == 4
                        ? string.Join('.', content.Select(b => b.ToString(CultureInfo.InvariantCulture)))
                        : "0x" + Convert.ToHexString(content));

            case Counter32Tag:
                return new SnmpVarBind(oid, "Counter32",
                    DecodeUnsigned(content).ToString(CultureInfo.InvariantCulture));

            case Gauge32Tag:
                return new SnmpVarBind(oid, "Gauge32",
                    DecodeUnsigned(content).ToString(CultureInfo.InvariantCulture));

            case UInteger32Tag:
                return new SnmpVarBind(oid, "UInteger32",
                    DecodeUnsigned(content).ToString(CultureInfo.InvariantCulture));

            case TimeTicksTag:
                return new SnmpVarBind(oid, "TimeTicks", FormatTimeTicks(DecodeUnsigned(content)));

            case Counter64Tag:
                return new SnmpVarBind(oid, "Counter64",
                    DecodeUnsigned(content).ToString(CultureInfo.InvariantCulture));

            case NoSuchObjectTag:
                return new SnmpVarBind(oid, "noSuchObject", "OID 不存在");

            case NoSuchInstanceTag:
                return new SnmpVarBind(oid, "noSuchInstance", "实例不存在");

            case EndOfMibViewTag:
                return new SnmpVarBind(oid, "endOfMibView", "已到达 MIB 末尾");

            default:
                return new SnmpVarBind(oid, $"0x{tag:X2}", "0x" + Convert.ToHexString(content));
        }
    }

    private static long DecodeSignedInteger(byte[] content)
    {
        if (content.Length == 0) throw new FormatException("INTEGER 内容为空");
        if (content.Length > 8) content = content[^8..]; // 异常长整数截断保护

        var value = (content[0] & 0x80) != 0 ? -1L : 0L;
        foreach (var b in content) value = (value << 8) | b;
        return value;
    }

    private static ulong DecodeUnsigned(byte[] content)
    {
        if (content.Length == 0) return 0;
        if (content.Length > 9) content = content[^9..]; // Counter64 允许 9 字节（前导 0）

        ulong value = 0;
        foreach (var b in content) value = (value << 8) | b;
        return value;
    }

    /// <summary>OCTET STRING：全部为可打印 ASCII 时按文本输出，否则输出十六进制。</summary>
    private static string DecodeOctetStringValue(byte[] content)
    {
        if (content.Length == 0) return string.Empty;

        var printable = true;
        foreach (var b in content)
        {
            if (b is >= 0x20 and <= 0x7E) continue;
            if (b is 0x09 or 0x0A or 0x0D) continue;
            printable = false;
            break;
        }

        if (printable) return Encoding.ASCII.GetString(content).TrimEnd('\r', '\n');
        return "0x" + Convert.ToHexString(content);
    }

    /// <summary>TimeTicks（1/100 秒）→ <c>N days hh:mm:ss</c>。</summary>
    private static string FormatTimeTicks(ulong ticks)
    {
        var totalSeconds = ticks / 100;
        var days = totalSeconds / 86400;
        var hours = totalSeconds % 86400 / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        return string.Format(CultureInfo.InvariantCulture,
            "{0} days {1:00}:{2:00}:{3:00}", days, hours, minutes, seconds);
    }

    private static string DecodeOid(byte[] content)
    {
        if (content.Length == 0) throw new FormatException("OID 内容为空");

        var arcs = new List<ulong>(content.Length);
        ulong value = 0;
        var pending = false;
        foreach (var b in content)
        {
            value = (value << 7) | (byte)(b & 0x7F);
            pending = true;
            if ((b & 0x80) == 0)
            {
                arcs.Add(value);
                value = 0;
                pending = false;
            }
        }

        if (pending) throw new FormatException("OID 的 base-128 编码不完整");
        if (arcs.Count == 0) throw new FormatException("OID 内容为空");

        var first = arcs[0];
        var result = new StringBuilder();
        if (first < 40) result.Append("0.").Append(first.ToString(CultureInfo.InvariantCulture));
        else if (first < 80) result.Append("1.").Append((first - 40).ToString(CultureInfo.InvariantCulture));
        else result.Append("2.").Append((first - 80).ToString(CultureInfo.InvariantCulture));

        for (var i = 1; i < arcs.Count; i++)
        {
            result.Append('.').Append(arcs[i].ToString(CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private static bool IsExceptionValue(string type) =>
        type is "endOfMibView" or "noSuchObject" or "noSuchInstance";

    // ======================================================================
    // OID 工具
    // ======================================================================

    private static bool TryParseOid(string? text, out uint[] arcs)
    {
        arcs = Array.Empty<uint>();
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Trim().TrimStart('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        var parsed = new uint[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!uint.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i]))
                return false;
        }

        if (parsed[0] > 2) return false;
        if (parsed[0] < 2 && parsed[1] > 39) return false;

        arcs = parsed;
        return true;
    }

    private static string FormatOid(uint[] arcs)
    {
        var builder = new StringBuilder(arcs.Length * 4);
        for (var i = 0; i < arcs.Length; i++)
        {
            if (i > 0) builder.Append('.');
            builder.Append(arcs[i].ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static bool IsUnderRoot(uint[] oid, uint[] root)
    {
        if (oid.Length < root.Length) return false;
        for (var i = 0; i < root.Length; i++)
        {
            if (oid[i] != root[i]) return false;
        }

        return true;
    }

    private static bool IsStrictlyGreater(uint[] left, uint[] right)
    {
        var shared = Math.Min(left.Length, right.Length);
        for (var i = 0; i < shared; i++)
        {
            if (left[i] > right[i]) return true;
            if (left[i] < right[i]) return false;
        }

        return left.Length > right.Length;
    }

    // ======================================================================
    // 错误信息
    // ======================================================================

    private static string DescribeErrorStatus(int status, int index)
    {
        var text = status switch
        {
            0 => "noError",
            1 => "tooBig（响应数据过大）",
            2 => "noSuchName（OID 不存在）",
            3 => "badValue（值不合法）",
            4 => "readOnly（只读）",
            5 => "genErr（通用错误）",
            6 => "noAccess（无访问权限）",
            7 => "wrongType（类型不匹配）",
            8 => "wrongLength（长度不匹配）",
            9 => "wrongEncoding（编码不匹配）",
            10 => "wrongValue（值不匹配）",
            11 => "noCreation（不可创建）",
            12 => "inconsistentValue（值不一致）",
            13 => "resourceUnavailable（资源不可用）",
            14 => "commitFailed（提交失败）",
            15 => "undoFailed（回滚失败）",
            16 => "authorizationError（认证失败，请检查 community）",
            17 => "notWritable（不可写）",
            18 => "inconsistentName（名称不一致）",
            _ => $"error-status={status}"
        };

        return index > 0 ? $"设备返回错误：{text}（error-index={index}）" : $"设备返回错误：{text}";
    }

    // ======================================================================
    // 内部类型
    // ======================================================================

    private sealed record SnmpResponse(
        int Version,
        int RequestId,
        int ErrorStatus,
        int ErrorIndex,
        IReadOnlyList<SnmpVarBind> Variables);

    /// <summary>最小化 BER 读取器（仅支持 definite-length，够 SNMP 使用）。</summary>
    private sealed class BerReader
    {
        private readonly byte[] _data;
        private int _offset;

        public BerReader(byte[] data) => _data = data;

        public bool HasMore => _offset < _data.Length;

        public (byte Tag, byte[] Content) ReadTlv()
        {
            if (_offset >= _data.Length) throw new FormatException("BER 数据不完整（缺少 tag）");

            var tag = _data[_offset++];
            if (_offset >= _data.Length) throw new FormatException("BER 数据不完整（缺少 length）");

            var firstLengthByte = _data[_offset++];
            int length;
            if ((firstLengthByte & 0x80) == 0)
            {
                length = firstLengthByte;
            }
            else
            {
                var lengthByteCount = firstLengthByte & 0x7F;
                if (lengthByteCount == 0) throw new FormatException("不支持 BER 不定长编码");
                if (lengthByteCount > 4) throw new FormatException("BER length 过长");

                length = 0;
                for (var i = 0; i < lengthByteCount; i++)
                {
                    if (_offset >= _data.Length) throw new FormatException("BER length 数据不完整");
                    length = (length << 8) | _data[_offset++];
                }
            }

            if (length < 0 || _offset + length > _data.Length)
                throw new FormatException("BER 内容长度超出数据范围");

            var content = new byte[length];
            Array.Copy(_data, _offset, content, 0, length);
            _offset += length;
            return (tag, content);
        }

        public int ReadInteger(string fieldName)
        {
            var (tag, content) = ReadTlv();
            if (tag != IntegerTag)
                throw new FormatException($"{fieldName}：期望 INTEGER(0x02)，实际 0x{tag:X2}");

            var value = DecodeSignedInteger(content);
            if (value is < int.MinValue or > int.MaxValue)
                throw new FormatException($"{fieldName}：INTEGER 超出 32 位范围");

            return (int)value;
        }

        public byte[] ReadOctetString(string fieldName)
        {
            var (tag, content) = ReadTlv();
            if (tag != OctetStringTag)
                throw new FormatException($"{fieldName}：期望 OCTET STRING(0x04)，实际 0x{tag:X2}");

            return content;
        }
    }
}
