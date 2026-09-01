using CleanSwitch.Recovery;

namespace CleanSwitch.Tests.Support.Bcd;

internal sealed class FakeDestructiveBcdCommand : IDestructiveBcdCommand
{
    public ResolvedBcdDeletionTarget? LastTarget { get; private set; }

    public int ExecuteCount { get; private set; }

    public bool ThrowOnExecute { get; set; }

    public int ExitCode { get; set; }

    public Action<ResolvedBcdDeletionTarget>? OnExecute { get; set; }

    public Task<DestructiveCommandResult> ExecuteAsync(ResolvedBcdDeletionTarget target)
    {
        ExecuteCount++;
        LastTarget = target;
        OnExecute?.Invoke(target);

        if (ThrowOnExecute)
        {
            throw new InvalidOperationException("fake bcd executor threw");
        }

        return Task.FromResult(new DestructiveCommandResult(ExitCode, "fake-ok", string.Empty, "fake-bcdedit"));
    }
}
