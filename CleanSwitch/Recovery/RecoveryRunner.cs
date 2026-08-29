using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

public enum RecoveryRunOutcome
{
    /// <summary>Handoff completed: Boot 2 is set as the next boot and a restart was scheduled.</summary>
    HandoffScheduled,

    /// <summary>Everything validated, but no BCD change or restart was made (dry run).</summary>
    DryRunCompleted,

    /// <summary>Nothing to do: no state file, or the operation is already finished.</summary>
    NothingToDo,

    /// <summary>Refused or failed. No boot change was made.</summary>
    Failed
}

public sealed record RecoveryRunResult(RecoveryRunOutcome Outcome, string Message);

/// <summary>
/// The recovery-side half of the retirement flow. Invoked with <c>--recovery-run</c> (or
/// <c>--recovery-dry-run</c>) from a recovery environment command prompt.
/// <para>
/// PHASE 2A BEHAVIOUR: load the state file, mark that recovery started, validate the Boot 2
/// entry, SKIP DELETION ENTIRELY, point the next boot at Boot 2 and restart. Nothing is
/// deleted, formatted or removed. <see cref="RetirementExecutor"/> is never called.
/// </para>
/// </summary>
public sealed class RecoveryRunner
{
    private readonly IBootManager _bootManager;
    private readonly IRetirementCoordinator _coordinator;
    private readonly DiskValidator _diskValidator;
    private readonly BootEntryValidator _bootEntryValidator;
    private readonly RetirementExecutor _executor;
    private readonly CleanSwitchOptions _options;
    private readonly IOperationLog _log;

    public RecoveryRunner(
        IBootManager bootManager,
        IRetirementCoordinator coordinator,
        DiskValidator diskValidator,
        BootEntryValidator bootEntryValidator,
        RetirementExecutor executor,
        CleanSwitchOptions options,
        IOperationLog? log = null)
    {
        _bootManager = bootManager ?? throw new ArgumentNullException(nameof(bootManager));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _diskValidator = diskValidator ?? throw new ArgumentNullException(nameof(diskValidator));
        _bootEntryValidator = bootEntryValidator ?? throw new ArgumentNullException(nameof(bootEntryValidator));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? NullOperationLog.Instance;
    }

    /// <param name="dryRun">
    /// When true, every validation and state transition runs but the BCD is not changed and
    /// the PC is not restarted. This is the safe way to exercise the flow.
    /// </param>
    public async Task<RecoveryRunResult> RunAsync(bool dryRun)
    {
        _log.Info("recovery", $"Recovery-side run starting. dryRun={dryRun}, phase=2A (non-destructive).");

        RetirementState? state;
        try
        {
            state = _coordinator.TryLoad();
        }
        catch (RetirementStorageException exception)
        {
            _log.Warn("recovery", $"Could not load retirement state: {exception.Message}");
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, exception.Message);
        }

        if (state is null)
        {
            var message =
                $"No retirement state file at '{_coordinator.StateFilePath}'. Nothing to do; no boot change made.";
            _log.Info("recovery", message);
            return new RecoveryRunResult(RecoveryRunOutcome.NothingToDo, message);
        }

        if (state.IsTerminal)
        {
            var message =
                $"Retirement operation is already {RetirementStatusNames.ToWire(state.Status)}. " +
                "Nothing to do; no boot change made.";
            _log.Info("recovery", message);
            return new RecoveryRunResult(RecoveryRunOutcome.NothingToDo, message);
        }

        var loaded = state;

        try
        {
            return await RunCoreAsync(loaded, dryRun);
        }
        catch (RetirementNotImplementedException exception)
        {
            // Phase 2A must never reach the executor. If it somehow does, stop hard.
            SafeMarkFailed(loaded, "Destructive path was reached in Phase 2A: " + exception.Message);
            _log.Warn("recovery", $"REFUSED: {exception.Message}");
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, exception.Message);
        }
        catch (Exception exception) when (
            exception is BootManagerException or RetirementStateException or RetirementStorageException)
        {
            SafeMarkFailed(loaded, exception.Message);
            _log.Warn("recovery", $"Recovery run failed: {exception.Message}");
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, exception.Message);
        }
    }

    private async Task<RecoveryRunResult> RunCoreAsync(RetirementState state, bool dryRun)
    {
        if (state.Status is RetirementStatus.Pending or RetirementStatus.Failed)
        {
            state = _coordinator.Transition(
                state,
                RetirementStatus.RecoveryStarted,
                state.Status == RetirementStatus.Failed
                    ? "Retrying the recovery-side run after an earlier failure."
                    : "Recovery environment started the CleanSwitch retirement run.");
        }
        else
        {
            _log.Info(
                "recovery",
                $"Resuming an operation already at {RetirementStatusNames.ToWire(state.Status)}.");
        }

        if (state.Status == RetirementStatus.Verified)
        {
            // A previous run already completed the handoff and was interrupted before the
            // restart. Re-running must not redo the BCD work, only the restart.
            var resumeMessage =
                $"Handoff was already verified for Boot 2 ({state.Boot2Id}). " +
                (dryRun ? "Dry run: not restarting." : "Restarting only.");
            _log.Info("recovery", resumeMessage);

            if (dryRun)
            {
                return new RecoveryRunResult(RecoveryRunOutcome.DryRunCompleted, resumeMessage);
            }

            await _bootManager.RestartAsync(_options.RestartDelaySeconds);
            return new RecoveryRunResult(RecoveryRunOutcome.HandoffScheduled, resumeMessage);
        }

        // ---- Report-only target identification. Phase 2A does not validate for deletion. ----
        _diskValidator.DescribeRunningSystemVolume();
        var boot1Identity = await _bootEntryValidator.TryDescribeBootEntryVolumeAsync(state.Boot1Id);
        var targetReport = _diskValidator.ReportRetirementTarget(boot1Identity);
        _log.Info("recovery", targetReport.Describe());

        if (boot1Identity is not null)
        {
            state.Boot1Identity = boot1Identity;
        }

        // ---- Boot 2 must be a real, distinct Windows loader before we hand control over. ----
        var boot2Report = await _bootEntryValidator.ValidateBoot2EntryAsync(state.Boot2Id, state.Boot1Id);
        if (!boot2Report.Passed)
        {
            var message =
                "Boot 2 validation failed, so no boot change was made and the PC will not be restarted." +
                Environment.NewLine + boot2Report.Describe();
            _coordinator.MarkFailed(state, message);
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
        }

        if (state.Status == RetirementStatus.RecoveryStarted)
        {
            state = _coordinator.Transition(
                state,
                RetirementStatus.Boot2Validated,
                "Boot 2 BCD entry validated. Phase 2A skips TARGET_VALIDATED because nothing will be deleted.");
        }

        // ---- Deletion: intentionally skipped in Phase 2A. ----
        _log.Warn(
            "recovery",
            "PHASE 2A: Boot 1 deletion is SKIPPED. RetirementExecutor is not called, no partition is touched, " +
            $"and no BCD entry is removed. Destructive implementation available={_executor.IsDestructiveRetirementAvailable}.");

        if (dryRun)
        {
            var dryRunMessage =
                "Dry run finished. Validations passed, deletion was skipped as designed, and neither the BCD " +
                $"nor the running state of the PC was changed. State left at " +
                $"{RetirementStatusNames.ToWire(state.Status)}.";
            _log.Info("recovery", dryRunMessage);
            return new RecoveryRunResult(RecoveryRunOutcome.DryRunCompleted, dryRunMessage);
        }

        // ---- Hand off to Boot 2. ----
        await _bootManager.SetNextBootAsync(state.Boot2Id);

        if (state.Status != RetirementStatus.BcdUpdated)
        {
            state = _coordinator.Transition(
                state,
                RetirementStatus.BcdUpdated,
                $"One-time boot sequence set to Boot 2 ({state.Boot2Id}).");
        }

        var verification = await _bootManager.TryGetEntryAsync(state.Boot2Id);
        if (verification is null)
        {
            var message =
                $"Boot 2 entry {state.Boot2Id} could not be re-read after setting the boot sequence. " +
                "Not restarting.";
            _coordinator.MarkFailed(state, message);
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
        }

        state = _coordinator.Transition(
            state,
            RetirementStatus.Verified,
            $"Boot 2 entry re-read after the BCD update: {verification.Describe()}.");

        _log.Info(
            "recovery",
            "Handoff complete. COMPLETE is recorded by the app the next time it starts on Boot 2.");

        await _bootManager.RestartAsync(_options.RestartDelaySeconds);

        return new RecoveryRunResult(
            RecoveryRunOutcome.HandoffScheduled,
            $"Boot 2 ({state.Boot2Id}) set as the next boot and a restart was scheduled in " +
            $"{_options.RestartDelaySeconds} second(s). Nothing was deleted.");
    }

    private void SafeMarkFailed(RetirementState state, string error)
    {
        try
        {
            _coordinator.MarkFailed(state, error);
        }
        catch (Exception exception) when (exception is RetirementStorageException or RetirementStateException)
        {
            _log.Warn("recovery", $"Could not record the failure in the state file: {exception.Message}");
        }
    }
}
