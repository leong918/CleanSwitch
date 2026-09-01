using CleanSwitch.Recovery;
using CleanSwitch.Services;

namespace CleanSwitch.Tests.Support.Vhd;

/// <summary>
/// The only wrapper allowed to call real diskpart in tests. It re-proves the target is
/// the temporary VHDX immediately before the production command runs.
/// </summary>
internal sealed class VhdBoundDiskCommand : IDestructiveDiskCommand
{
    private readonly DiskpartDestructiveDiskCommand _inner;
    private readonly DisposableVhdSession _session;

    public VhdBoundDiskCommand(DisposableVhdSession session, IOperationLog? log = null)
    {
        _session = session;
        _inner = new DiskpartDestructiveDiskCommand(log);
    }

    public int ExecuteCount { get; private set; }

    public ResolvedDeletionTarget? LastTarget { get; private set; }

    public async Task<DestructiveCommandResult> ExecuteAsync(ResolvedDeletionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!VhdIntegrationGuard.IsEnabled)
        {
            throw new InvalidOperationException(
                "VhdBoundDiskCommand refused: CLEANSWITCH_VHD_TESTS is not enabled.");
        }

        VirtualDiskProofVerifier.ProveResolvedTarget(
            _session.Proof,
            target,
            _session.Boot1.PartitionGptId,
            _session.DiskGptId,
            _session.Identities.ProtectedGptIds);

        ExecuteCount++;
        LastTarget = target;
        return await _inner.ExecuteAsync(target);
    }
}
