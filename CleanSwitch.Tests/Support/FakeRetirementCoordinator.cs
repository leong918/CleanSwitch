using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Tests.Support;

internal sealed class FakeRetirementCoordinator : IRetirementCoordinator
{
    public string StateFilePath => @"Z:\test\retirement-state.json";

    public RetirementState? State { get; set; }

    public RetirementState? LastFailed { get; private set; }

    public RetirementState? LastRetired { get; private set; }

    public void EnsureStorageReady()
    {
    }

    public RetirementState? TryLoad() => State;

    public RetirementState BeginRetirement(
        string boot1Id,
        string boot2Id,
        string recoveryId,
        PartitionIdentity boot1Identity,
        PartitionIdentity boot2Identity) =>
        throw new NotSupportedException();

    public RetirementState Transition(RetirementState state, RetirementStatus target, string reason)
    {
        state.Status = target;
        return state;
    }

    public RetirementState RecordBoot1Retired(RetirementState state, string reason, bool deletionOccurred)
    {
        LastRetired = state;
        state.Status = RetirementStatus.Boot1Retired;
        state.DestructiveDeletionPerformed = deletionOccurred;
        return state;
    }

    public RetirementState MarkFailed(RetirementState state, string error)
    {
        LastFailed = state;
        state.Status = RetirementStatus.Failed;
        return state;
    }

    public RetirementState MarkAborted(RetirementState state, string reason) =>
        throw new NotSupportedException();

    public RetirementState? TryCompleteAfterReboot(string currentBootGuid) => null;
}
