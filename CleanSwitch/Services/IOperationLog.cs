namespace CleanSwitch.Services;

public enum OperationLogLevel
{
    Info,
    Warning
}

/// <summary>
/// Append-only audit log for the retirement flow. Implementations must survive a reboot,
/// because everything interesting happens across one.
/// </summary>
public interface IOperationLog
{
    void Write(OperationLogLevel level, string category, string message);

    IReadOnlyList<string> Destinations { get; }
}

public static class OperationLogExtensions
{
    public static void Info(this IOperationLog log, string category, string message) =>
        log.Write(OperationLogLevel.Info, category, message);

    public static void Warn(this IOperationLog log, string category, string message) =>
        log.Write(OperationLogLevel.Warning, category, message);
}

/// <summary>Used when no file log has been configured yet (for example during boot detection).</summary>
public sealed class NullOperationLog : IOperationLog
{
    public static readonly NullOperationLog Instance = new();

    private NullOperationLog()
    {
    }

    public void Write(OperationLogLevel level, string category, string message)
    {
    }

    public IReadOnlyList<string> Destinations => [];
}
