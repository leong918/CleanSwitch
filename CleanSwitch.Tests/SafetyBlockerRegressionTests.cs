using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support;

namespace CleanSwitch.Tests;

public sealed class SafetyBlockerRegressionTests
{
    [Fact]
    public void Only_committed_exact_phase2a_authorization_is_accepted()
    {
        var options = RetirementFixtures.Options();
        var state = AuthorizedState(options);

        RetirementExecutionAuthorization.RequireCommitted(state, options, state.HandoffAuthorizationToken,
            Runtime(state, isWinPe: true));

        foreach (var authorization in new[]
                 {
                     HandoffAuthorizationStates.None,
                     HandoffAuthorizationStates.Preparing,
                     HandoffAuthorizationStates.Armed,
                     HandoffAuthorizationStates.Disarmed
                 })
        {
            state.HandoffAuthorizationState = authorization;
            Assert.Throws<RetirementExecutionException>(() =>
                RetirementExecutionAuthorization.RequireCommitted(state, options, state.HandoffAuthorizationToken,
                    Runtime(state, isWinPe: true)));
        }
    }

    [Theory]
    [InlineData(RetirementStatus.Failed)]
    [InlineData(RetirementStatus.Aborted)]
    [InlineData(RetirementStatus.RecoveryRequired)]
    public void Terminal_or_failed_states_never_authorize_unattended_destruction(RetirementStatus status)
    {
        var options = RetirementFixtures.Options();
        var state = AuthorizedState(options);
        state.Status = status;

        Assert.Throws<RetirementExecutionException>(() =>
            RetirementExecutionAuthorization.RequireCommitted(state, options, state.HandoffAuthorizationToken,
                Runtime(state, isWinPe: true)));
    }

    [Fact]
    public void Target_absent_resume_requires_every_non_target_partition_to_be_unchanged()
    {
        var before = RetirementFixtures.StandardLayout();
        var state = AuthorizedState(RetirementFixtures.Options());
        state.Status = RetirementStatus.DestructiveIntent;
        state.Boot1Identity = RetirementFixtures.Boot1Identity();
        state.Boot2Identity = RetirementFixtures.Boot2Identity();
        state.DestructiveIntentGptSnapshot = DestructiveIntentReconciliation.Capture(before);

        var after = before.Without(PinnedRetirementTargets.Boot1GptId);
        Assert.True(DestructiveIntentReconciliation.VerifyTargetAbsent(state, after).Passed);

        var changed = new GptLayoutSnapshot(
            after.Partitions.Select(partition => partition.PartitionGptId == PinnedRetirementTargets.Boot2GptId
                ? new LivePartition
                {
                    PartitionGptId = partition.PartitionGptId,
                    DiskGptId = partition.DiskGptId,
                    DiskNumber = partition.DiskNumber,
                    PartitionNumber = partition.PartitionNumber,
                    PartitionType = partition.PartitionType,
                    StartingOffset = partition.StartingOffset + 4096,
                    SizeBytes = partition.SizeBytes,
                    IsRunningSystemVolume = partition.IsRunningSystemVolume,
                    MountPoint = partition.MountPoint
                }
                : partition).ToList(),
            after.RunningSystemGptId,
            after.Warnings);
        Assert.False(DestructiveIntentReconciliation.VerifyTargetAbsent(state, changed).Passed);
    }

    [Fact]
    public void Expected_and_observed_winre_hashes_are_distinct_sealed_inputs()
    {
        var expected = new string('A', 64);
        WinReDeploymentHashPolicy.RequireExpectedMatchesObserved(expected, expected, "test");
        Assert.Throws<InvalidOperationException>(() =>
            WinReDeploymentHashPolicy.RequireExpectedMatchesObserved(expected, new string('B', 64), "test"));
        Assert.Throws<InvalidOperationException>(() =>
            WinReDeploymentHashPolicy.RequireExpectedMatchesObserved(null, expected, "test"));
    }

    private static RetirementState AuthorizedState(CleanSwitchOptions options)
    {
        options.RecoveryGuid = "{11111111-1111-1111-1111-111111111111}";
        var state = new RetirementState
        {
            SchemaVersion = RetirementState.CurrentSchemaVersion,
            Status = RetirementStatus.Pending,
            Boot2BcdObjectId = options.Boot2Guid,
            RecoveryId = options.RecoveryGuid,
            HandoffRecoveryBcdObjectId = options.RecoveryGuid,
            HandoffAuthorizationState = HandoffAuthorizationStates.Committed,
            HandoffAuthorizationToken = Guid.NewGuid().ToString("D"),
            HandoffCommittedAtUtc = DateTimeOffset.UtcNow
        };
        state.HandoffAuthorizationBindingSha256 = RetirementExecutionAuthorization.ComputeBinding(state);
        return state;
    }

    private static RecoveryRuntimeEvidence Runtime(RetirementState state, bool isWinPe)
    {
        BcdIdentifiers.TryParseObjectId(state.RecoveryId, out var recovery);
        return new RecoveryRuntimeEvidence(isWinPe, BcdAliasResolution.Resolved, recovery);
    }
}
