using CleanSwitch.Models;

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

    public RetirementState BeginRetirement(string boot1Id, string boot2Id, string recoveryId)
    {
        if (string.IsNullOrWhiteSpace(boot1Id) || string.IsNullOrWhiteSpace(boot2Id))
        {
            throw new RetirementStateException(
                "A retirement operation needs both the Boot 1 and Boot 2 BCD identifiers.");
        }

        if (string.Equals(boot1Id, boot2Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new RetirementStateException(
                $"Boot 1 and Boot 2 resolved to the same BCD identifier ({boot1Id}). Refusing to continue.");
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
            Boot1Id = boot1Id,
            Boot2Id = boot2Id,
            RecoveryId = recoveryId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            SchemaVersion = RetirementState.CurrentSchemaVersion,
            Phase = "2A",
            DestructiveDeletionPerformed = false,
            MachineName = Environment.MachineName,
            // Recorded so a later phase running in WinRE or on Boot 2 can prove it is
            // looking at the same volume Boot 1 wrote to, without trusting drive letters.
            StateVolumeIdentity = _store.StateVolumeIdentity,
            Transitions =
            [
                new RetirementTransition
                {
                    From = RetirementStatus.Pending,
                    To = RetirementStatus.Pending,
                    AtUtc = now,
                    Reason = "Operation created by the RETIRE SYSTEM action on Boot 1."
                }
            ]
        };

        _log.Info(
            "coordinator",
            $"Creating retirement operation: boot1={boot1Id}, boot2={boot2Id}, recovery={recoveryId}, " +
            $"stateVolume=[{state.StateVolumeIdentity?.Describe() ?? "unknown"}].");
        _store.Save(state);
        return state;
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
