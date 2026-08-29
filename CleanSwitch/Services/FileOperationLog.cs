using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CleanSwitch.Services;

/// <summary>
/// Appends audit lines to one or more log files. Multiple destinations are used on
/// purpose: the primary destination lives with the retirement state (off Boot 1) so it
/// survives Boot 1 being retired, while the local fallback keeps a copy readable even if
/// the external location disappears mid-run.
/// </summary>
public sealed class FileOperationLog : IOperationLog
{
    private readonly object _gate = new();
    private readonly List<string> _files = [];
    private readonly HashSet<string> _brokenFiles = new(StringComparer.OrdinalIgnoreCase);

    private FileOperationLog(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            if (!string.IsNullOrWhiteSpace(file) && !_files.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                _files.Add(file);
            }
        }
    }

    public IReadOnlyList<string> Destinations => _files;

    /// <summary>
    /// Creates a log writing to <paramref name="primaryDirectory"/> and, when it differs,
    /// to a local fallback under %ProgramData%. Directories that cannot be created are
    /// skipped rather than throwing: losing the log must never abort a boot operation.
    /// </summary>
    public static FileOperationLog Create(string? primaryDirectory, string fileNamePrefix)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var fileName = $"{fileNamePrefix}-{stamp}.log";

        var candidates = new List<string?>
        {
            primaryDirectory,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "CleanSwitch",
                "logs")
        };

        var files = new List<string>();
        foreach (var directory in candidates)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(directory);
                files.Add(Path.Combine(directory, fileName));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                Trace.WriteLine($"CleanSwitch could not prepare log directory '{directory}': {exception.Message}");
            }
        }

        return new FileOperationLog(files);
    }

    public void Write(OperationLogLevel level, string category, string message)
    {
        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0} | {1,-7} | {2,-18} | {3}",
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            level.ToString().ToUpperInvariant(),
            category,
            Flatten(message));

        Trace.WriteLine(line);

        lock (_gate)
        {
            foreach (var file in _files)
            {
                if (_brokenFiles.Contains(file))
                {
                    continue;
                }

                try
                {
                    File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _brokenFiles.Add(file);
                    Trace.WriteLine($"CleanSwitch log destination '{file}' is no longer writable: {exception.Message}");
                }
            }
        }
    }

    private static string Flatten(string message) =>
        message.Replace("\r\n", " \\n ", StringComparison.Ordinal)
            .Replace("\n", " \\n ", StringComparison.Ordinal)
            .Replace("\r", " \\n ", StringComparison.Ordinal);
}
