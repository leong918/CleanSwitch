using CleanSwitch.Models;

namespace CleanSwitch.Services;

/// <summary>
/// Owns the retirement state file and is the only component allowed to change status.
/// </summary>
public interface IRetirementCoordinator
{
    /// <summary>Absolute path of the state file this coordinator reads and writes.</summary>
    string StateFilePath { get; }

    /// <summary>Throws with actionable guidance when the state location is unusable.</summary>
    void EnsureStorageReady();

    RetirementState? TryLoad();

    /// <summary>
    /// Creates and persists a fresh PENDING record with Boot 1 / Boot 2 partition
    /// identities already filled from the partition table. Refuses to overwrite an
    /// operation in flight. Refuses if either identity is incomplete or they collide.
    /// </summary>
    RetirementState BeginRetirement(
        string boot1Id,
        string boot2Id,
        string recoveryId,
        PartitionIdentity boot1Identity,
        PartitionIdentity boot2Identity);

    /// <summary>Applies a validated transition and persists it. Illegal transitions throw.</summary>
    RetirementState Transition(RetirementState state, RetirementStatus target, string reason);

    /// <summary>
    /// Records that Boot 1 is gone (live delete or already-absent resume). Sets
    /// <see cref="Models.RetirementState.DestructiveDeletionPerformed"/> and transitions to
    /// BOOT1_RETIRED when that is a legal next status.
    /// </summary>
    RetirementState RecordBoot1Retired(RetirementState state, string reason, bool deletionOccurred);

    RetirementState MarkFailed(RetirementState state, string error);

    RetirementState MarkAborted(RetirementState state, string reason);

    /// <summary>
    /// Called on startup: if a VERIFIED handoff exists and the running boot entry is the
    /// recorded Boot 2, the operation is closed out as COMPLETE.
    /// </summary>
    RetirementState? TryCompleteAfterReboot(string currentBootGuid);
}
