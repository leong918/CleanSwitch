using CleanSwitch.Models;
using CleanSwitch.Recovery;

namespace CleanSwitch.Services;

/// <summary>Thrown when a caller asks for a state transition the state machine forbids.</summary>
public sealed class RetirementStateException : Exception
{
    public RetirementStateException(string message)
        : base(message)
    {
    }
}

public sealed class RetirementCoordinator : IRetirementCoordinator
{
    private readonly RetirementStateStore _store;
    private readonly IOperationLog _log;

    public RetirementCoordinator(RetirementStateStore store, IOperationLog? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log ?? NullOperationLog.Instance;
    }

    public string StateFilePath => _store.StateFilePath;

    public void EnsureStorageReady() => _store.EnsureWritable();

    public RetirementState? TryLoad() => _store.TryLoad();

    public RetirementState BeginRetirement(
        string boot1Id,
        string boot2Id,
        string recoveryId,
        PartitionIdentity boot1Identity,
        PartitionIdentity boot2Identity)
    {
        ArgumentNullException.ThrowIfNull(boot1Identity);
        ArgumentNullException.ThrowIfNull(boot2Identity);

        if (string.IsNullOrWhiteSpace(boot1Id) || string.IsNullOrWhiteSpace(boot2Id))
        {
            throw new RetirementStateException(
                "A retirement operation needs both the Boot 1 and Boot 2 BCD identifiers.");
        }

        if (!BcdIdentifiers.TryParseObjectId(boot1Id, out var boot1Bcd) ||
            BcdIdentifiers.IsProtectedObject(boot1Bcd) ||
            BcdIdentifiers.IsAlias(boot1Id))
        {
            throw new RetirementStateException(
                $"Boot 1 BCD identifier '{boot1Id}' is not a concrete object GUID. " +
                "Aliases such as {{current}} or {{bootmgr}} are refused. Nothing was written.");
        }

        if (!BcdIdentifiers.TryParseObjectId(boot2Id, out var boot2Bcd) ||
            BcdIdentifiers.IsProtectedObject(boot2Bcd) ||
            BcdIdentifiers.IsAlias(boot2Id))
        {
            throw new RetirementStateException(
                $"Boot 2 BCD identifier '{boot2Id}' is not a concrete object GUID. " +
                "Aliases such as {{current}} or {{bootmgr}} are refused. Nothing was written.");
        }

        if (boot1Bcd == boot2Bcd)
        {
            throw new RetirementStateException(
                $"Boot 1 and Boot 2 resolved to the same BCD identifier ({BcdIdentifiers.Format(boot1Bcd)}). Refusing to continue.");
        }

        RetirementStateIdentityRequirements.ValidateForNewPending(
            boot1Id,
            boot2Id,
            boot1Identity,
            boot2Identity);

        if (string.Equals(
                boot1Identity.GptPartitionId?.Trim(),
                boot2Identity.GptPartitionId?.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new RetirementStateException(
                $"Boot 1 and Boot 2 resolved to the same GPT partition GUID ({boot1Identity.GptPartitionId}). " +
                "Refusing to continue.");
        }

        var existing = _store.TryLoad();
        if (existing is not null && !existing.IsTerminal && existing.Status != RetirementStatus.Failed)
        {
            throw new RetirementStateException(
                $"A retirement operation is already in progress with status " +
                $"{RetirementStatusNames.ToWire(existing.Status)} (started {existing.CreatedAtUtc:u})." +
                Environment.NewLine +
                $"State file: {_store.StateFilePath}" +
                Environment.NewLine +
                "Finish or abort that operation before starting a new one.");
        }

        var now = DateTimeOffset.UtcNow;
        var state = new RetirementState
        {
            Operation = RetirementState.RetireBoot1Operation,
            Status = RetirementStatus.Pending,
            Boot1Id = BcdIdentifiers.Format(boot1Bcd),
            Boot2Id = BcdIdentifiers.Format(boot2Bcd),
            RecoveryId = recoveryId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            SchemaVersion = RetirementState.CurrentSchemaVersion,
            Phase = "2B-identify",
            Boot1BcdObjectId = BcdIdentifiers.Format(boot1Bcd),
            Boot2BcdObjectId = BcdIdentifiers.Format(boot2Bcd),
            DestructiveDeletionPerformed = false,
            MachineName = Environment.MachineName,
            Boot1Identity = boot1Identity,
            Boot2Identity = boot2Identity,
            StateVolumeIdentity = _store.StateVolumeIdentity,
            Transitions =
            [
                new RetirementTransition
                {
                    From = RetirementStatus.Pending,
                    To = RetirementStatus.Pending,
                    AtUtc = now,
                    Reason = "Operation created by RETIRE SYSTEM on Boot 1 with partition-table identities recorded."
                }
            ]
        };

        _log.Info(
            "coordinator",
            $"Creating retirement operation: boot1={BcdIdentifiers.Format(boot1Bcd)} [{boot1Identity.Describe()}], " +
            $"boot2={BcdIdentifiers.Format(boot2Bcd)} [{boot2Identity.Describe()}], recovery={recoveryId}, " +
            $"boot1BcdObject={BcdIdentifiers.Format(boot1Bcd)} boot2BcdObject={BcdIdentifiers.Format(boot2Bcd)}, " +
            $"stateVolume=[{state.StateVolumeIdentity?.Describe() ?? "unknown"}].");
        _store.Save(state);
        return state;
    }

    public RetirementState RecordBoot1Retired(RetirementState state, string reason, bool deletionOccurred)
    {
        ArgumentNullException.ThrowIfNull(state);

        state.DestructiveDeletionPerformed = true;
        _log.Info(
            "coordinator",
            "Recording Boot 1 retired. " +
            $"destructiveDeletionPerformed=true deletionOccurredThisInvocation={deletionOccurred} " +
            $"status={RetirementStatusNames.ToWire(state.Status)}");

        if (state.Status is RetirementStatus.Boot1Retired
            or RetirementStatus.BcdUpdated
            or RetirementStatus.Verified
            or RetirementStatus.Complete)
        {
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            _store.Save(state);
            return state;
        }

        return Transition(state, RetirementStatus.Boot1Retired, reason);
    }

    public RetirementState Transition(RetirementState state, RetirementStatus target, string reason)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new RetirementStateException(
                "Every retirement state transition must carry a reason for the audit trail.");
        }

        var from = state.Status;
        if (from == target)
        {
            throw new RetirementStateException(
                $"Refusing a no-op transition: the operation is already {RetirementStatusNames.ToWire(from)}.");
        }

        if (!RetirementStateMachine.IsLegal(from, target))
        {
            var message =
                $"Illegal retirement transition {RetirementStatusNames.ToWire(from)} -> " +
                $"{RetirementStatusNames.ToWire(target)}. Legal targets: " +
                RetirementStateMachine.DescribeLegalTargets(from) + ".";
            _log.Warn("coordinator", message);
            throw new RetirementStateException(message);
        }

        var skipReason = RetirementStateMachine.DescribePhase2ASkip(from, target);
        if (skipReason is not null)
        {
            _log.Warn(
                "coordinator",
                $"Taking declared Phase 2A shortcut {RetirementStatusNames.ToWire(from)} -> " +
                $"{RetirementStatusNames.ToWire(target)}: {skipReason}");
        }

        var now = DateTimeOffset.UtcNow;
        state.Status = target;
        state.UpdatedAtUtc = now;
        state.Transitions.Add(new RetirementTransition
        {
            From = from,
            To = target,
            AtUtc = now,
            Reason = reason
        });

        if (target is not (RetirementStatus.Failed or RetirementStatus.Aborted))
        {
            state.LastError = null;
        }

        _log.Info(
            "transition",
            $"{RetirementStatusNames.ToWire(from)} -> {RetirementStatusNames.ToWire(target)}: {reason}");

        _store.Save(state);
        return state;
    }

    public RetirementState MarkFailed(RetirementState state, string error)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.LastError = error;

        if (state.Status == RetirementStatus.Failed)
        {
            _log.Warn("coordinator", $"Recording an additional failure: {error}");
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            _store.Save(state);
            return state;
        }

        if (!RetirementStateMachine.IsLegal(state.Status, RetirementStatus.Failed))
        {
            _log.Warn(
                "coordinator",
                $"Cannot mark {RetirementStatusNames.ToWire(state.Status)} as FAILED; recording error only: {error}");
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            _store.Save(state);
            return state;
        }

        return Transition(state, RetirementStatus.Failed, error);
    }

    public RetirementState MarkAborted(RetirementState state, string reason) =>
        Transition(state, RetirementStatus.Aborted, reason);

    public RetirementState? TryCompleteAfterReboot(string currentBootGuid)
    {
        var state = _store.TryLoad();
        if (state is null)
        {
            return null;
        }

        if (state.Status != RetirementStatus.Verified)
        {
            return state;
        }

        if (!string.Equals(state.Boot2Id, currentBootGuid, StringComparison.OrdinalIgnoreCase))
        {
            _log.Warn(
                "coordinator",
                $"Retirement is VERIFIED but the running boot entry is {currentBootGuid}, not the recorded " +
                $"Boot 2 ({state.Boot2Id}). Leaving the operation open.");
            return state;
        }

        return Transition(
            state,
            RetirementStatus.Complete,
            $"Boot 2 ({currentBootGuid}) confirmed running after the recovery handoff.");
    }
}
