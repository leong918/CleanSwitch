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
        _log.Info("recovery", $"Recovery-side run starting. dryRun={dryRun}, phase=2B-identify (non-destructive).");

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

        // ---- Phase 2B-identify: hard gate. No deletion. ----
        var identification = IdentifyTarget(state);
        if (!identification.Passed)
        {
            var message =
                "TARGET validation FAILED. No partition was changed and the PC will not be restarted." +
                Environment.NewLine + identification.Describe();
            _coordinator.MarkFailed(state, message);
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
        }

        state.Boot1IdentityObserved = identification.ObservedBoot1;

        if (state.Status == RetirementStatus.RecoveryStarted)
        {
            state = _coordinator.Transition(
                state,
                RetirementStatus.TargetValidated,
                identification.Summary);
        }

        _log.Info("recovery", "TARGET_VALIDATED");
        _log.Info("recovery", identification.Describe());

        if (dryRun)
        {
            var dryRunMessage =
                "TARGET_VALIDATED" + Environment.NewLine + identification.Describe() + Environment.NewLine +
                "Dry run: deletion was not attempted and the BCD was not changed.";
            return new RecoveryRunResult(RecoveryRunOutcome.DryRunCompleted, dryRunMessage);
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

        if (state.Status == RetirementStatus.TargetValidated)
        {
            state = _coordinator.Transition(
                state,
                RetirementStatus.Boot2Validated,
                "Boot 2 BCD entry validated. Deletion remains NOT IMPLEMENTED.");
        }

        // ---- Deletion: still not implemented. ----
        _log.Warn(
            "recovery",
            "PHASE 2B-identify: Boot 1 deletion is SKIPPED. RetirementExecutor is not called, no partition is " +
            "touched, and no BCD entry is removed. " +
            $"Destructive implementation available={_executor.IsDestructiveRetirementAvailable}.");

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

    private TargetIdentification IdentifyTarget(RetirementState state)
    {
        _diskValidator.DescribeRunningSystemVolume();

        if (state.Boot1Identity is null || !state.Boot1Identity.HasStableIdentifiers)
        {
            var report = new ValidationReport("Retirement target (identification gate)");
            report.Fail(
                "boot1-identity-recorded",
                "Boot 1 partition identity was not recorded at PENDING time with disk+partition and GPT unique id. " +
                "Re-run RETIRE SYSTEM from Boot 1 with the 2B-identify build.");
            return TargetIdentification.Failed(report);
        }

        if (state.Boot2Identity is null || !state.Boot2Identity.HasStableIdentifiers)
        {
            var report = new ValidationReport("Retirement target (identification gate)");
            report.Fail(
                "boot2-identity-recorded",
                "Boot 2 partition identity was not recorded at PENDING time. Re-run RETIRE SYSTEM from Boot 1.");
            return TargetIdentification.Failed(report);
        }

        var observedBoot1 = _diskValidator.TryObserveByGptId(
            state.Boot1Identity.GptPartitionId,
            "WinRE observation of Boot 1 by recorded GPT unique partition GUID",
            out var boot1Error);
        if (observedBoot1 is null)
        {
            var report = new ValidationReport("Retirement target (identification gate)");
            report.Fail("boot1-observed-by-gpt", boot1Error ?? "Boot 1 GPT GUID was not found in this environment.");
            return TargetIdentification.Failed(report);
        }

        var observedBoot2 = _diskValidator.TryObserveByGptId(
            state.Boot2Identity.GptPartitionId,
            "WinRE observation of Boot 2 by recorded GPT unique partition GUID",
            out var boot2Error);
        if (observedBoot2 is null)
        {
            var report = new ValidationReport("Retirement target (identification gate)");
            report.Fail("boot2-observed-by-gpt", boot2Error ?? "Boot 2 GPT GUID was not found in this environment.");
            return TargetIdentification.Failed(report);
        }

        if (!string.Equals(
                observedBoot2.GptPartitionId?.Trim(),
                state.Boot2Identity.GptPartitionId?.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            var report = new ValidationReport("Retirement target (identification gate)");
            report.Fail(
                "boot2-gpt-resolved",
                $"Boot 2 GPT GUID in this environment is {observedBoot2.GptPartitionId}; " +
                $"expected {state.Boot2Identity.GptPartitionId}.");
            return TargetIdentification.Failed(report);
        }

        _log.Info("recovery", $"Boot 2 still present: {observedBoot2.Describe()}");

        var reportGate = _diskValidator.ValidateRetirementTarget(
            state.Boot1Identity,
            observedBoot1,
            state.Boot2Identity);

        return new TargetIdentification(
            reportGate.Passed,
            reportGate.Passed
                ? "TARGET_VALIDATED: Boot 1 identity matched in WinRE; Boot 2 GPT GUID still present; " +
                  "target is not WinRE, ESP, Boot 2 or Recovery."
                : "TARGET validation failed.",
            observedBoot1,
            reportGate);
    }

    private sealed record TargetIdentification(
        bool Passed,
        string Summary,
        PartitionIdentity? ObservedBoot1,
        ValidationReport Report)
    {
        public string Describe() => Report.Describe();

        public static TargetIdentification Failed(ValidationReport report) =>
            new(false, "TARGET validation failed.", null, report);
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
