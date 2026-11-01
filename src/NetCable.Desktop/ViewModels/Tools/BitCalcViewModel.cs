using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetCable.Core.Utils;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>位计算器：进制互转与 AND / OR / XOR / 位移快速运算（纯 Core 实现，不依赖服务）。</summary>
[ToolPage("bitcalc")]
public sealed partial class BitCalcViewModel : ToolViewModelBase
{
    public BitCalcViewModel() : base("bitcalc")
    {
        Operators = new ObservableCollection<string> { "AND", "OR", "XOR", "左移", "右移" };
        SelectedOperator = "AND";
        Recompute(log: false);
        RecomputeBinarySummary();
    }

    public ObservableCollection<string> Operators { get; }

    [ObservableProperty] private string _input = "0xFF";
    [ObservableProperty] private string _operandA = "0b1010";
    [ObservableProperty] private string _operandB = "0b0110";
    [ObservableProperty] private string _selectedOperator = "AND";
    [ObservableProperty] private string _summary = "—";
    [ObservableProperty] private string _binarySummary = "—";

    partial void OnInputChanged(string value) => Recompute(log: false);

    partial void OnOperandAChanged(string value) => RecomputeBinarySummary();

    partial void OnOperandBChanged(string value) => RecomputeBinarySummary();

    partial void OnSelectedOperatorChanged(string value) => RecomputeBinarySummary();

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Recompute(log: true);
        RecomputeBinarySummary();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void ComputeBinary()
    {
        var opKey = OperatorKey();
        var result = BitCalculator.Binary(opKey, OperandA, OperandB);
        if (result is null)
        {
            LogError("位运算失败：无返回结果");
            return;
        }

        if (!result.Success)
        {
            LogError("位运算输入无效：" + (result.Error ?? "未知错误"));
            return;
        }

        AddRow($"运算 {SelectedOperator}", $"A = {OperandA}", $"B = {OperandB}");
        AddRow("结果（十进制）", result.Decimal);
        AddRow("结果（二进制）", "0b" + result.Binary);
        AddRow("结果（十六进制）", "0x" + result.Hexadecimal);
        AddRow("结果（八进制）", "0o" + result.Octal);
        AddRow("结果位长", $"{result.BitLength} bit", $"{(result.BitLength + 7) / 8} 字节");

        BinarySummary = $"{OperandA} {SelectedOperator} {OperandB} = {result.Decimal}（0b{result.Binary} / 0x{result.Hexadecimal}）";
        LogOk(BinarySummary);
    }

    private string OperatorKey() => SelectedOperator switch
    {
        "AND" => "and",
        "OR" => "or",
        "XOR" => "xor",
        "左移" => "shift-left",
        "右移" => "shift-right",
        _ => "and"
    };

    private void RecomputeBinarySummary()
    {
        var result = BitCalculator.Binary(OperatorKey(), OperandA, OperandB);
        BinarySummary = result is { Success: true }
            ? $"{OperandA} {SelectedOperator} {OperandB} = {result.Decimal}（0b{result.Binary} / 0x{result.Hexadecimal}）"
            : "位运算输入无效：" + (result?.Error ?? "无法解析操作数");
    }

    private void Recompute(bool log)
    {
        Rows.Clear();
        var result = BitCalculator.Convert(Input);
        if (!result.Success)
        {
            Summary = "输入无效：" + (result.Error ?? "无法解析");
            if (log) LogError(Summary);
            return;
        }

        AddRow("二进制 (0b)", "0b" + result.Binary);
        AddRow("十进制", result.Decimal);
        AddRow("十六进制 (0x)", "0x" + result.Hexadecimal);
        AddRow("八进制 (0o)", "0o" + result.Octal);
        AddRow("位长", $"{result.BitLength} bit", $"{(result.BitLength + 7) / 8} 字节");

        Summary = $"{result.Decimal} = 0b{result.Binary} = 0x{result.Hexadecimal} = 0o{result.Octal} · {result.BitLength} bit";
        if (log) LogOk(Summary);
    }
}
