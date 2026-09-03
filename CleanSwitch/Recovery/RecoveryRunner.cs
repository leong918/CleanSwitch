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

    /// <summary>Live-delete review printed. No disk, BCD or state change.</summary>
    ReviewCompleted,

    /// <summary>Refused or failed. No boot change was made.</summary>
    Failed
}

public sealed record RecoveryRunRequest(bool DryRun, bool ReviewOnly, bool ExecuteDeletion);

public sealed record RecoveryRunResult(RecoveryRunOutcome Outcome, string Message);

/// <summary>
/// Recovery-side retirement flow.
/// <para>
/// Live disk deletion is compiled in <see cref="DestructiveRetirementEngine"/> but is not
/// reached while <c>DestructiveOperationsImplemented</c> is false.
/// Phase 2C BCD deletion is compiled in <see cref="DestructiveBcdRetirementEngine"/> but
/// is not called from this runner while <c>BcdOperationsImplemented</c> is false.
/// <c>--recovery-run</c> without <c>--execute-deletion</c> skips deletion and hands
/// off to Boot 2. <c>--recovery-review</c> never starts a disk or BCD delete command.
/// </para>
/// </summary>
public sealed class RecoveryRunner
{
    private readonly IBootManager _bootManager;
    private readonly IRetirementCoordinator _coordinator;
    private readonly DiskValidator _diskValidator;
    private readonly BootEntryValidator _bootEntryValidator;
    private readonly RetirementExecutor _executor;
    private readonly RetirementHardwareReview _hardwareReview;
    private readonly IBcdStoreSource _bcdStore;
    private readonly IGptLayoutSource _layout;
    private readonly CleanSwitchOptions _options;
    private readonly IOperationLog _log;

    public RecoveryRunner(
        IBootManager bootManager,
        IRetirementCoordinator coordinator,
        DiskValidator diskValidator,
        BootEntryValidator bootEntryValidator,
        RetirementExecutor executor,
        CleanSwitchOptions options,
        IOperationLog? log = null,
        RetirementHardwareReview? hardwareReview = null,
        IBcdStoreSource? bcdStore = null,
        IGptLayoutSource? layout = null)
    {
        _bootManager = bootManager ?? throw new ArgumentNullException(nameof(bootManager));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _diskValidator = diskValidator ?? throw new ArgumentNullException(nameof(diskValidator));
        _bootEntryValidator = bootEntryValidator ?? throw new ArgumentNullException(nameof(bootEntryValidator));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? NullOperationLog.Instance;
        _hardwareReview = hardwareReview ?? new RetirementHardwareReview(
            layout ?? new VolumeLocatorGptLayoutSource(),
            bcdStore ?? new BootManagerBcdStoreSource(bootManager),
            _log);
        _bcdStore = bcdStore ?? new BootManagerBcdStoreSource(bootManager);
        _layout = layout ?? new VolumeLocatorGptLayoutSource();
    }

    public Task<RetirementResumePreviewResult> RunResumePreviewAsync(RetirementState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _log.Info(
            "resume-preview",
            "Starting read-only BOOT1_RETIRED resume preview. No disk, BCD delete, state write, or reboot.");
        var preview = new RetirementResumePreview(_diskValidator, _bcdStore, _log);
        return preview.RunAsync(state);
    }

    public async Task<RecoveryRunResult> RunAsync(RecoveryRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _log.Info(
            "recovery",
            $"Recovery-side run starting. dryRun={request.DryRun} reviewOnly={request.ReviewOnly} " +
            $"executeDeletion={request.ExecuteDeletion} " +
            $"destructiveImplemented={_executor.IsDestructiveRetirementAvailable} " +
            $"enableDestructiveRetirement={_options.EnableDestructiveRetirement}.");

        RetirementState? state;
        try
        {
            state = _coordinator.TryLoad();
        }
        catch (RetirementStorageException exception)
        {
            _log.Warn("recovery", $"Could not load retirement state: {exception.Message}");
            if (request.ReviewOnly)
            {
                var rejected = _hardwareReview.Run(null);
                return new RecoveryRunResult(
                    RecoveryRunOutcome.Failed,
                    rejected.Describe() + Environment.NewLine + exception.Message);
            }

            return new RecoveryRunResult(RecoveryRunOutcome.Failed, exception.Message);
        }

        if (request.ReviewOnly)
        {
            return RunReview(state, request.ExecuteDeletion);
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
            return await RunCoreAsync(loaded, request);
        }
        catch (RetirementNotImplementedException exception)
        {
            SafeMarkFailed(loaded, "Live deletion is disabled: " + exception.Message);
            _log.Warn("recovery", $"REFUSED: {exception.Message}");
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, exception.Message);
        }
        catch (Exception exception) when (
            exception is RetirementExecutionException or BootManagerException
                or RetirementStateException or RetirementStorageException)
        {
            SafeMarkFailed(loaded, exception.Message);
            _log.Warn("recovery", $"Recovery run failed: {exception.Message}");
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, exception.Message);
        }
    }

    private RecoveryRunResult RunReview(RetirementState? state, bool executeDeletion)
    {
        _ = executeDeletion;
        var review = _hardwareReview.Run(state);
        return new RecoveryRunResult(
            review.OverallPassed ? RecoveryRunOutcome.ReviewCompleted : RecoveryRunOutcome.Failed,
            review.Describe());
    }

    private async Task<RecoveryRunResult> RunCoreAsync(RetirementState state, RecoveryRunRequest request)
    {
        if (ShouldUseProductionExecution(request, state))
        {
            var production = new ProductionRetirementExecution(
                _bootManager,
                _coordinator,
                _diskValidator,
                _executor,
                _hardwareReview,
                _bcdStore,
                _layout,
                _options,
                _log);
            return await production.RunAsync(state, request);
        }

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
            var resumeMessage =
                $"Handoff was already verified for Boot 2 ({state.Boot2Id}). " +
                (request.DryRun ? "Dry run: not restarting." : "Restarting only.");
            _log.Info("recovery", resumeMessage);

            if (request.DryRun)
            {
                return new RecoveryRunResult(RecoveryRunOutcome.DryRunCompleted, resumeMessage);
            }

            await _bootManager.RestartAsync(_options.RestartDelaySeconds);
            return new RecoveryRunResult(RecoveryRunOutcome.HandoffScheduled, resumeMessage);
        }

        var deletionAlreadySettled = IsDeletionAlreadySettled(state);
        TargetIdentification identification;
        RetirementDeletionPlan? deletionPlan = null;

        if (deletionAlreadySettled)
        {
            identification = IdentifyBoot2Only(state);
            _log.Info("recovery", _executor.AcknowledgeAlreadyRecorded().Message);
        }
        else
        {
            identification = IdentifyTarget(state);
        }

        if (!identification.Passed)
        {
            var message =
                "TARGET validation FAILED. No partition was changed and the PC will not be restarted." +
                Environment.NewLine + identification.Describe();
            _coordinator.MarkFailed(state, message);
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
        }

        if (identification.ObservedBoot1 is not null)
        {
            state.Boot1IdentityObserved = identification.ObservedBoot1;
        }

        if (state.Status == RetirementStatus.RecoveryStarted)
        {
            state = _coordinator.Transition(
                state,
                RetirementStatus.TargetValidated,
                identification.Summary);
        }

        _log.Info("recovery", identification.AlreadyAbsent ? "TARGET already absent" : "TARGET_VALIDATED");
        _log.Info("recovery", identification.Describe());

        if (!deletionAlreadySettled && !identification.AlreadyAbsent)
        {
            if (state.Boot1Identity is null || identification.ObservedBoot1 is null)
            {
                var message =
                    "TARGET_VALIDATED was recorded without Boot 1 identities. Refusing to continue. " +
                    "No partition was changed.";
                _coordinator.MarkFailed(state, message);
                return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
            }

            deletionPlan = _executor.BuildDeletionPlan(
                expectedBoot1: state.Boot1Identity,
                observedBoot1: identification.ObservedBoot1,
                boot2: state.Boot2Identity,
                validation: identification.Report,
                explicitOptIn: request.ExecuteDeletion,
                boot1BcdId: state.Boot1Id);
            _log.Info("recovery", deletionPlan.Describe());
        }

        if (request.DryRun)
        {
            var dryRunMessage =
                identification.Describe() + Environment.NewLine +
                (deletionPlan?.Describe() ?? string.Empty) + Environment.NewLine +
                "Dry run: deletion was not attempted and the BCD was not changed.";
            return new RecoveryRunResult(RecoveryRunOutcome.DryRunCompleted, dryRunMessage);
        }

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
                "Boot 2 BCD entry validated.");
        }

        state = await ApplyDeletionOrSkipAsync(state, request, identification, deletionPlan);

        if (!_executor.IsBcdRetirementAvailable)
        {
            _log.Warn(
                "recovery",
                "Phase 2C BCD deletion is SKIPPED. " +
                $"BcdOperationsImplemented={_executor.IsBcdRetirementAvailable}. " +
                "DeleteBoot1BcdEntryAsync is not called. No BCD object is removed.");
        }

        await _bootManager.SetNextBootAsync(state.Boot2Id);

        if (state.Status != RetirementStatus.BcdUpdated)
        {
            state = _coordinator.Transition(
                state,
                RetirementStatus.BcdUpdated,
                $"One-time boot sequence set to Boot 2 ({state.Boot2Id}). Boot 1 BCD object was not deleted.");
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
            $"{_options.RestartDelaySeconds} second(s). " +
            $"destructiveDeletionPerformed={state.DestructiveDeletionPerformed}." +
            Environment.NewLine + (deletionPlan?.Describe() ?? string.Empty));
    }

    private async Task<RetirementState> ApplyDeletionOrSkipAsync(
        RetirementState state,
        RecoveryRunRequest request,
        TargetIdentification identification,
        RetirementDeletionPlan? deletionPlan)
    {
        if (IsDeletionAlreadySettled(state))
        {
            _log.Info(
                "recovery",
                "Deletion already settled (destructiveDeletionPerformed or BOOT1_RETIRED+). " +
                "diskpart will not run again.");
            return state;
        }

        if (identification.AlreadyAbsent)
        {
            if (state.Boot1Identity is null || state.Boot2Identity is null)
            {
                throw new RetirementExecutionException(
                    "Already-absent resume needs recorded Boot 1 and Boot 2 identities.");
            }

            var acknowledged = _executor.AcknowledgeAlreadyDeleted(state.Boot1Identity, state.Boot2Identity);
            return _coordinator.RecordBoot1Retired(
                state,
                acknowledged.Message,
                deletionOccurred: false);
        }

        var liveEnabled = _executor.IsDestructiveRetirementAvailable && _executor.IsConfigEnabled;
        if (!liveEnabled || !request.ExecuteDeletion)
        {
            _log.Warn(
                "recovery",
                "Live Boot 1 deletion is SKIPPED. " +
                $"DestructiveOperationsImplemented={_executor.IsDestructiveRetirementAvailable} " +
                $"EnableDestructiveRetirement={_executor.IsConfigEnabled} " +
                $"executeDeletion={request.ExecuteDeletion} " +
                $"planAuthorised={deletionPlan?.ExecutionAuthorised ?? false}. " +
                "RetireBoot1Async is not called. No partition is touched. No BCD object is removed.");
            return state;
        }

        if (state.Boot1Identity is null || identification.ObservedBoot1 is null || state.Boot2Identity is null)
        {
            throw new RetirementExecutionException(
                "Live deletion refused: Boot 1/Boot 2 identities are incomplete.");
        }

        var result = await _executor.RetireBoot1Async(
            state.Boot1Identity,
            identification.ObservedBoot1,
            state.Boot2Identity,
            identification.Report,
            explicitOptIn: true);

        _log.Info(
            "recovery",
            $"Deletion result kind={result.Kind} destructiveDeletionOccurred={result.DestructiveDeletionOccurred} " +
            result.Message);

        return _coordinator.RecordBoot1Retired(
            state,
            result.Message,
            deletionOccurred: result.DestructiveDeletionOccurred);
    }

    private static bool IsDeletionAlreadySettled(RetirementState state) =>
        state.DestructiveDeletionPerformed ||
        state.Status is RetirementStatus.Boot1Retired
            or RetirementStatus.BcdUpdated
            or RetirementStatus.Verified;

    private bool ShouldUseProductionExecution(RecoveryRunRequest request, RetirementState state) =>
        !request.ReviewOnly &&
        _executor.IsDestructiveRetirementAvailable &&
        _executor.IsConfigEnabled &&
        (request.ExecuteDeletion || IsDeletionAlreadySettled(state));

    private TargetIdentification IdentifyBoot2Only(RetirementState state)
    {
        var report = new ValidationReport("Resume after Boot 1 already retired");
        if (state.Boot2Identity is null || !state.Boot2Identity.HasStableIdentifiers)
        {
            report.Fail("boot2-identity-recorded", "Boot 2 identity is missing; cannot resume safely.");
            return TargetIdentification.Failed(report);
        }

        var observedBoot2 = _diskValidator.TryObserveByGptId(
            state.Boot2Identity.GptPartitionId,
            "WinRE observation of Boot 2 after Boot 1 retirement",
            out var boot2Error);
        if (observedBoot2 is null)
        {
            report.Fail("boot2-observed-by-gpt", boot2Error ?? "Boot 2 GPT GUID was not found.");
            return TargetIdentification.Failed(report);
        }

        report.Pass("boot2-still-present", observedBoot2.Describe());
        report.Pass("deletion-already-settled", "diskpart will not run again.");
        return new TargetIdentification(
            true,
            false,
            "Boot 2 still present after prior Boot 1 retirement.",
            null,
            report);
    }

    private TargetIdentification IdentifyTarget(RetirementState state)
    {
        _diskValidator.DescribeRunningSystemVolume();

        if (state.Boot1Identity is null || !state.Boot1Identity.HasStableIdentifiers)
        {
            var report = new ValidationReport("Retirement target (identification gate)");
            report.Fail(
                "boot1-identity-recorded",
                "Boot 1 partition identity was not recorded at PENDING time with disk+partition and GPT unique id.");
            return TargetIdentification.Failed(report);
        }

        if (state.Boot2Identity is null || !state.Boot2Identity.HasStableIdentifiers)
        {
            var report = new ValidationReport("Retirement target (identification gate)");
            report.Fail(
                "boot2-identity-recorded",
                "Boot 2 partition identity was not recorded at PENDING time.");
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

        _log.Info("recovery", $"Boot 2 still present: {observedBoot2.Describe()}");

        var observedBoot1 = _diskValidator.TryObserveByGptId(
            state.Boot1Identity.GptPartitionId,
            "WinRE observation of Boot 1 by recorded GPT unique partition GUID",
            out var boot1Error);
        if (observedBoot1 is null)
        {
            var snapshot = _executor.ReadOperationSnapshot(state.Boot1Identity, state.Boot2Identity);
            if (snapshot.Boot1Count == 0 && snapshot.Boot2Count == 1)
            {
                var report = new ValidationReport("Retirement target (already absent)");
                report.Pass(
                    "boot1-gpt-absent",
                    boot1Error ?? "Boot 1 GPT unique id has zero matches.");
                report.Pass("boot2-still-unique", observedBoot2.Describe());
                return new TargetIdentification(
                    true,
                    true,
                    "Boot 1 GPT is already absent; Boot 2 GPT is still unique. diskpart will not run.",
                    null,
                    report);
            }

            var failed = new ValidationReport("Retirement target (identification gate)");
            failed.Fail(
                "boot1-observed-by-gpt",
                (boot1Error ?? "Boot 1 GPT GUID was not found.") +
                $" boot1Matches={snapshot.Boot1Count} boot2Matches={snapshot.Boot2Count}.");
            return TargetIdentification.Failed(failed);
        }

        var reportGate = _diskValidator.ValidateRetirementTarget(
            state.Boot1Identity,
            observedBoot1,
            state.Boot2Identity);

        return new TargetIdentification(
            reportGate.Passed,
            false,
            reportGate.Passed
                ? "TARGET_VALIDATED: Boot 1 identity matched in WinRE; Boot 2 GPT GUID still present; " +
                  "target is not WinRE, ESP, Boot 2 or Recovery."
                : "TARGET validation failed.",
            observedBoot1,
            reportGate);
    }

    private sealed record TargetIdentification(
        bool Passed,
        bool AlreadyAbsent,
        string Summary,
        PartitionIdentity? ObservedBoot1,
        ValidationReport Report)
    {
        public string Describe() => Report.Describe();

        public static TargetIdentification Failed(ValidationReport report) =>
            new(false, false, "TARGET validation failed.", null, report);
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
