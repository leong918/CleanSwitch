using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Tests.Support;

internal sealed class FakeBootManager : IBootManager
{
    public bool RestartCalled { get; private set; }

    public Task<BootLayout> DetectAsync(string? preferredOtherGuid) =>
        throw new NotSupportedException();

    public Task<bool> SetNextBootAsync(string bootGuid) => Task.FromResult(true);

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
