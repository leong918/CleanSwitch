using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Tests.Support;

internal sealed class FakeRetirementCoordinator : IRetirementCoordinator
{
    public string StateFilePath => @"Z:\test\retirement-state.json";

    public RetirementState? State { get; set; }

    public RetirementState? LastFailed { get; private set; }

    public RetirementState? LastRetired { get; private set; }

    public int BeginRetirementCallCount { get; private set; }

    public int MarkFailedCallCount { get; private set; }

    public Func<RetirementState>? OnBeginRetirement { get; set; }

    public void EnsureStorageReady()
    {
    }

    public RetirementState? TryLoad() => State;

    public RetirementState BeginRetirement(
        string boot1Id,
        string boot2Id,
        string recoveryId,
        PartitionIdentity boot1Identity,
        PartitionIdentity boot2Identity)
    {
        BeginRetirementCallCount++;
        if (OnBeginRetirement is not null)
        {
            return OnBeginRetirement();
        }

        var now = DateTimeOffset.UtcNow;
        State = new RetirementState
        {
            Status = RetirementStatus.Pending,
            SchemaVersion = RetirementState.CurrentSchemaVersion,
            Phase = "2B-identify",
            Boot1Id = boot1Id,
            Boot2Id = boot2Id,
            RecoveryId = recoveryId,
            Boot1BcdObjectId = boot1Id,
            Boot2BcdObjectId = boot2Id,
            Boot1Identity = boot1Identity,
            Boot2Identity = boot2Identity,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Transitions =
            [
                new RetirementTransition
                {
                    From = RetirementStatus.Pending,
                    To = RetirementStatus.Pending,
                    AtUtc = now,
                    Reason = "Fake Phase 2A operation created."
                }
            ]
        };
        return State;
    }

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
        MarkFailedCallCount++;
        LastFailed = state;
        var from = state.Status;
        state.Status = RetirementStatus.Failed;
        state.LastError = error;
        state.Transitions.Add(new RetirementTransition
        {
            From = from,
            To = RetirementStatus.Failed,
            AtUtc = DateTimeOffset.UtcNow,
            Reason = error
        });
        return state;
    }

    public RetirementState MarkAborted(RetirementState state, string reason) =>
        throw new NotSupportedException();

    public RetirementState Persist(RetirementState state) => state;

    public RetirementState? TryCompleteAfterReboot(string currentBootGuid) => null;
}
