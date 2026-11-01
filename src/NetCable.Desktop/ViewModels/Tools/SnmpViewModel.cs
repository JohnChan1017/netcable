using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Services.Network;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>SNMP：v1/v2c 的 GET 与 WALK 查询，结果按 OID / 类型 / 值 展示。</summary>
[ToolPage("snmp")]
public sealed partial class SnmpViewModel : ToolViewModelBase
{
    private enum QueryMode
    {
        Get,
        Walk
    }

    private readonly ISnmpService _service = new SnmpService();
    private QueryMode _mode = QueryMode.Get;

    public SnmpViewModel() : base("snmp")
    {
        Versions = new ObservableCollection<string> { "v2c", "v1" };
        SelectedVersion = "v2c";
        Summary = "填写设备地址与 community，选择 GET 或 WALK；默认查询 sysDescr";
    }

    public ObservableCollection<string> Versions { get; }

    [ObservableProperty] private string _host = "192.168.1.1";
    [ObservableProperty] private string _community = "public";
    [ObservableProperty] private string _selectedVersion = "v2c";
    [ObservableProperty] private string _oid = "1.3.6.1.2.1.1.1.0";
    [ObservableProperty] private int _timeout = 2000;
    [ObservableProperty] private string _summary = "—";

    [RelayCommand]
    private void StartGet()
    {
        if (IsRunning) return;
        _mode = QueryMode.Get;
        RunCommand.Execute(null);
    }

    [RelayCommand]
    private void StartWalk()
    {
        if (IsRunning) return;
        _mode = QueryMode.Walk;
        RunCommand.Execute(null);
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Summary = "查询中…";

        var host = Host.Trim();
        if (host.Length == 0)
        {
            LogError("请输入 SNMP 设备地址（IP 或主机名）");
            Summary = "—";
            return;
        }

        var request = new SnmpRequest
        {
            Host = host,
            Community = string.IsNullOrWhiteSpace(Community) ? "public" : Community.Trim(),
            Version = SelectedVersion == "v1" ? 1 : 2,
            Oid = string.IsNullOrWhiteSpace(Oid) ? "1.3.6.1.2.1.1.1.0" : Oid.Trim(),
            TimeoutMs = Math.Clamp(Timeout, 200, 30000),
            MaxRows = 200
        };

        if (_mode == QueryMode.Get) await ExecuteGetAsync(request, cancellationToken).ConfigureAwait(true);
        else await ExecuteWalkAsync(request, cancellationToken).ConfigureAwait(true);
    }

    private async Task ExecuteGetAsync(SnmpRequest request, CancellationToken cancellationToken)
    {
        LogInfo($"SNMP GET {request.Host}  OID {request.Oid}  community {MaskCommunity(request.Community)}  v{request.Version}c");

        SnmpResult result;
        try
        {
            result = await _service.GetAsync(request, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            Summary = "SNMP GET 失败：" + message;
            LogError(Summary);
            AddFailureRows(request, message);
            return;
        }

        if (!result.Success)
        {
            Summary = "SNMP GET 失败：" + result.Message;
            LogError(Summary);
            AddFailureRows(request, result.Message);
            return;
        }

        foreach (var variable in result.Variables)
        {
            AddRow(variable.Oid, variable.Value, variable.Type);
            LogOk($"{variable.Oid} = {variable.Value}  ({variable.Type})");
        }

        Summary = $"GET 成功 · {result.Variables.Count} 个变量" +
                  (string.IsNullOrWhiteSpace(result.Message) ? string.Empty : $" · {result.Message}");
        LogOk(Summary);
        if (result.Variables.Count == 0) LogWarn("设备返回空变量列表（检查 OID 是否存在）");
    }

    private async Task ExecuteWalkAsync(SnmpRequest request, CancellationToken cancellationToken)
    {
        LogInfo($"SNMP WALK {request.Host}  OID {request.Oid}  community {MaskCommunity(request.Community)}  v{request.Version}c（最多 {request.MaxRows} 行）");

        var count = 0;
        try
        {
            await foreach (var variable in _service.WalkAsync(request, cancellationToken).ConfigureAwait(true))
            {
                count++;
                AddRow(variable.Oid, variable.Value, variable.Type);
                LogInfo($"{variable.Oid} = {variable.Value}  ({variable.Type})");
                StatusText = $"WALK 已获取 {count} 行…";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Summary = $"WALK 中断（已获取 {count} 行）：{ex.Message}";
            LogError(Summary);
            AddFailureRows(request, ex.Message);
            return;
        }

        if (count == 0)
        {
            Summary = $"WALK 未返回任何 OID：{request.Host} 无响应，或该子树为空";
            LogWarn(Summary);
            AddFailureRows(request, "未收到任何 SNMP 响应");
            return;
        }

        Summary = $"WALK 完成 · {count} 行 · {request.Host} {request.Oid}";
        LogOk(Summary);
    }

    private void AddFailureRows(SnmpRequest request, string message)
    {
        AddRow("目标", $"{request.Host}", $"SNMP v{request.Version}c");
        AddRow("OID", request.Oid);
        AddRow("失败原因", message);
        AddRow("排查 1", "确认设备已启用 SNMP 且 community 正确", "v1/v2c 使用明文 community");
        AddRow("排查 2", "确认 UDP 161 未被防火墙拦截", "Windows 防火墙 / ACL");
        AddRow("排查 3", "确认目标 IP 可达", "可先用 Ping 页面验证连通性");
    }

    private static string MaskCommunity(string community) =>
        string.IsNullOrEmpty(community) ? "public" : community;
}
