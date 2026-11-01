using System.Collections;
using Avalonia;
using Avalonia.Controls;

namespace NetCable.Desktop.Controls;

public partial class ResultTable : UserControl
{
    public static readonly StyledProperty<IEnumerable?> RowsProperty =
        AvaloniaProperty.Register<ResultTable, IEnumerable?>(nameof(Rows));

    public static readonly StyledProperty<string> KeyHeaderProperty =
        AvaloniaProperty.Register<ResultTable, string>(nameof(KeyHeader), "项目");

    public static readonly StyledProperty<string> ValueHeaderProperty =
        AvaloniaProperty.Register<ResultTable, string>(nameof(ValueHeader), "值");

    public static readonly StyledProperty<string> ExtraHeaderProperty =
        AvaloniaProperty.Register<ResultTable, string>(nameof(ExtraHeader), "备注");

    public IEnumerable? Rows { get => GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public string KeyHeader { get => GetValue(KeyHeaderProperty); set => SetValue(KeyHeaderProperty, value); }
    public string ValueHeader { get => GetValue(ValueHeaderProperty); set => SetValue(ValueHeaderProperty, value); }
    public string ExtraHeader { get => GetValue(ExtraHeaderProperty); set => SetValue(ExtraHeaderProperty, value); }

    public ResultTable()
    {
        InitializeComponent();
    }
}
