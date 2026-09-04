using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Tests.Support;

internal sealed class FakeBootManager : IBootManager
{
    public bool RestartCalled { get; private set; }

    public int RestartCallCount { get; private set; }

    public int? RestartDelaySeconds { get; private set; }

    public int SetDefaultBootCallCount { get; private set; }

    public string? DefaultBootTarget { get; private set; }

    public Func<string, Task<bool>>? OnSetDefaultBootAsync { get; set; }

    public int SetNextBootCallCount { get; private set; }

    public string? NextBootTarget { get; private set; }

    public Func<string, Task<bool>>? OnSetNextBootAsync { get; set; }

    public int ClearNextBootCallCount { get; private set; }

    public Func<Task<bool>>? OnClearNextBootAsync { get; set; }

    public Func<int, Task>? OnRestartAsync { get; set; }

    public BootLayout? DetectedLayout { get; set; }

    public Task<BootLayout> DetectAsync(string? preferredOtherGuid) =>
        DetectedLayout is null
            ? throw new NotSupportedException()
            : Task.FromResult(DetectedLayout);

    public Task<bool> SetNextBootAsync(string bootGuid)
    {
        SetNextBootCallCount++;
        NextBootTarget = bootGuid;
        return OnSetNextBootAsync?.Invoke(bootGuid) ?? Task.FromResult(true);
    }

    public Task<bool> SetDefaultBootAsync(string bootGuid)
    {
        SetDefaultBootCallCount++;
        DefaultBootTarget = bootGuid;
        return OnSetDefaultBootAsync?.Invoke(bootGuid) ?? Task.FromResult(true);
    }

    public Task<bool> ClearNextBootAsync()
    {
        ClearNextBootCallCount++;
        NextBootTarget = null;
        return OnClearNextBootAsync?.Invoke() ?? Task.FromResult(true);
    }

    public Task RestartAsync(int delaySeconds)
    {
        RestartCalled = true;
        RestartCallCount++;
        RestartDelaySeconds = delaySeconds;
        return OnRestartAsync?.Invoke(delaySeconds) ?? Task.CompletedTask;
    }

    public Task<IReadOnlyList<BcdEntry>> EnumerateAsync(string scope) =>
        Task.FromResult<IReadOnlyList<BcdEntry>>([]);

    public Task<BcdEntry?> TryGetEntryAsync(string bootGuid) =>
        Task.FromResult<BcdEntry?>(null);
}
