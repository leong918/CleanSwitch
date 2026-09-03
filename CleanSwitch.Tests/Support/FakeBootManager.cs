using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Tests.Support;

internal sealed class FakeBootManager : IBootManager
{
    public bool RestartCalled { get; private set; }

    public int SetDefaultBootCallCount { get; private set; }

    public string? DefaultBootTarget { get; private set; }

    public Func<string, Task<bool>>? OnSetDefaultBootAsync { get; set; }

    public Task<BootLayout> DetectAsync(string? preferredOtherGuid) =>
        throw new NotSupportedException();

    public Task<bool> SetNextBootAsync(string bootGuid) => Task.FromResult(true);

    public Task<bool> SetDefaultBootAsync(string bootGuid)
    {
        SetDefaultBootCallCount++;
        DefaultBootTarget = bootGuid;
        return OnSetDefaultBootAsync?.Invoke(bootGuid) ?? Task.FromResult(true);
    }

    public Task RestartAsync(int delaySeconds)
    {
        RestartCalled = true;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BcdEntry>> EnumerateAsync(string scope) =>
        Task.FromResult<IReadOnlyList<BcdEntry>>([]);

    public Task<BcdEntry?> TryGetEntryAsync(string bootGuid) =>
        Task.FromResult<BcdEntry?>(null);
}
