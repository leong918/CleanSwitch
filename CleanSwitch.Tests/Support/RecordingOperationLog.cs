using CleanSwitch.Services;

namespace CleanSwitch.Tests.Support;

internal sealed class RecordingOperationLog : IOperationLog
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries => _entries;

    public IReadOnlyList<string> Destinations => ["memory"];

    public void Write(OperationLogLevel level, string category, string message) =>
        _entries.Add($"{level} {category} {message}");

    public bool Contains(string fragment) =>
        _entries.Any(entry => entry.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
