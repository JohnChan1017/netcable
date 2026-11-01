namespace NetCable.Desktop.ViewModels;

public enum LogKind
{
    Info,
    Ok,
    Warn,
    Error,
    Muted
}

public sealed record LogEntry(DateTimeOffset Time, LogKind Kind, string Text)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string KindText => Kind switch
    {
        LogKind.Ok => "OK",
        LogKind.Warn => "WARN",
        LogKind.Error => "ERR",
        LogKind.Muted => "···",
        _ => "INF"
    };
}

public sealed record ResultRow(string Key, string Value, string? Extra = null);
