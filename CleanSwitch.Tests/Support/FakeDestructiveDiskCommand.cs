using CleanSwitch.Recovery;

namespace CleanSwitch.Tests.Support;

internal sealed class FakeDestructiveDiskCommand : IDestructiveDiskCommand
{
    public ResolvedDeletionTarget? LastTarget { get; private set; }

    public int ExecuteCount { get; private set; }

    public bool ThrowOnExecute { get; set; }

    public int ExitCode { get; set; }

    public Action<ResolvedDeletionTarget>? OnExecute { get; set; }

    public Task<DestructiveCommandResult> ExecuteAsync(ResolvedDeletionTarget target)
    {
        ExecuteCount++;
        LastTarget = target;
        OnExecute?.Invoke(target);

        if (ThrowOnExecute)
        {
            throw new InvalidOperationException("fake executor threw");
        }

        return Task.FromResult(new DestructiveCommandResult(ExitCode, "fake-ok", string.Empty, "fake-disk-command"));
    }
}
