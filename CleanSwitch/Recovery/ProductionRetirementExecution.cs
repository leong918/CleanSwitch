using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

/// <summary>
/// Live production retirement execution with survivor capture, Phase 2B delete, and Phase 2C
/// observation/delete. Only entered when compile-time and runtime destructive gates are true.
/// </summary>
public sealed class ProductionRetirementExecution
{
    private readonly IBootManager _bootManager;
    private readonly IRetirementCoordinator _coordinator;
    private readonly DiskValidator _diskValidator;
    private readonly RetirementExecutor _executor;
    private readonly RetirementHardwareReview _hardwareReview;
    private readonly IBcdStoreSource _bcdStore;
    private readonly IGptLayoutSource _layout;
    private readonly CleanSwitchOptions _options;
    private readonly IOperationLog _log;

    public ProductionRetirementExecution(
        IBootManager bootManager,
        IRetirementCoordinator coordinator,
        DiskValidator diskValidator,
        RetirementExecutor executor,
        RetirementHardwareReview hardwareReview,
        IBcdStoreSource bcdStore,
        IGptLayoutSource layout,
        CleanSwitchOptions options,
        IOperationLog log)
    {
        _bootManager = bootManager;
        _coordinator = coordinator;
        _diskValidator = diskValidator;
        _executor = executor;
        _hardwareReview = hardwareReview;
        _bcdStore = bcdStore;
        _layout = layout;
        _options = options;
        _log = log;
    }

    public async Task<RecoveryRunResult> RunAsync(RetirementState state, RecoveryRunRequest request)
    {
        if (state.Status is RetirementStatus.Pending or RetirementStatus.Failed)
        {
            state = _coordinator.Transition(
                state,
                RetirementStatus.RecoveryStarted,
                "Recovery environment started the production retirement execution path.");
        }

        var deletionSettled = state.DestructiveDeletionPerformed ||
                              state.Status is RetirementStatus.Boot1Retired
                                  or RetirementStatus.BcdUpdated
                                  or RetirementStatus.Verified;

        if (deletionSettled)
        {
            return await ResumeAfterBoot1RetiredAsync(state, request);
        }

        if (!request.ExecuteDeletion)
        {
            return new RecoveryRunResult(
                RecoveryRunOutcome.Failed,
                "Production execution requires --execute-deletion.");
        }

        var review = _hardwareReview.Run(state);
        if (!review.OverallPassed)
        {
            _coordinator.MarkFailed(state, review.Describe());
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, review.Describe());
        }

        BcdSnapshot beforeBcd;
        try
        {
            beforeBcd = await _bcdStore.CaptureAsync();
        }
        catch (Exception exception)
        {
            var message = "Live BCD enumeration failed before Phase 2B. No disk command was started. " +
                          exception.Message;
            _coordinator.MarkFailed(state, message);
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
        }

        SurvivorInventoryCapture.ApplyToState(state, beforeBcd, _layout);
        state.Phase = "2B-ready";
        state = _coordinator.Transition(
            state,
            RetirementStatus.Phase2BReady,
            "Pre-execution review passed. Survivor inventory captured before any delete.");

        var identification = IdentifyTarget(state);
        if (!identification.Passed || identification.ObservedBoot1 is null)
        {
            var message = "TARGET validation FAILED before Phase 2B delete." +
                          Environment.NewLine + identification.Describe();
            _coordinator.MarkFailed(state, message);
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
        }

        var result = await _executor.RetireBoot1Async(
            state.Boot1Identity!,
            identification.ObservedBoot1,
            state.Boot2Identity!,
            identification.Report,
            explicitOptIn: true);

        state.Phase = "2B-deleted-and-verified";
        state = _coordinator.RecordBoot1Retired(
            state,
            result.Message,
            deletionOccurred: result.DestructiveDeletionOccurred);

        return await ContinuePhase2CAsync(state, request, beforeBcd);
    }

    private async Task<RecoveryRunResult> ResumeAfterBoot1RetiredAsync(
        RetirementState state,
        RecoveryRunRequest request)
    {
        _log.Info(
            "execution",
            "Deletion already settled (destructiveDeletionPerformed or BOOT1_RETIRED+). diskpart will not run again.");

        if (state.Boot2Identity is null || !state.Boot2Identity.HasStableIdentifiers)
        {
            var message = "Boot 2 identity is missing; cannot resume safely.";
            _coordinator.MarkFailed(state, message);
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
        }

        var observedBoot2 = _diskValidator.TryObserveByGptId(
            state.Boot2Identity.GptPartitionId,
            "WinRE observation of Boot 2 after Boot 1 retirement",
            out var boot2Error);
        if (observedBoot2 is null)
        {
            var message = boot2Error ?? "Boot 2 GPT GUID was not found.";
            _coordinator.MarkFailed(state, message);
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
        }

        return await ContinuePhase2CAsync(state, request, beforeBcd: null);
    }

    private async Task<RecoveryRunResult> ContinuePhase2CAsync(
        RetirementState state,
        RecoveryRunRequest request,
        BcdSnapshot? beforeBcd)
    {
        BcdSnapshot afterBcd;
        try
        {
            afterBcd = await _bcdStore.CaptureAsync();
        }
        catch (Exception exception)
        {
            var message =
                "Phase 2C resume observation refused after Phase 2B. No bcdedit /delete was started." +
                Environment.NewLine +
                "Live BCD enumeration failed. " + exception.Message;
            _coordinator.MarkFailed(state, message);
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
        }

        var survivorReport = BcdSurvivorReconciliation.VerifyAfterBoot1PartitionDelete(
            state,
            afterBcd,
            beforeBcd);
        _log.Info("execution", survivorReport.Describe());

        if (!survivorReport.Passed)
        {
            var message =
                "Phase 2C resume observation refused after Phase 2B. No bcdedit /delete was started." +
                Environment.NewLine + survivorReport.Describe();
            _coordinator.MarkFailed(state, message);
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
        }

        if (!request.ExecuteDeletion || !_executor.IsBcdRetirementAvailable)
        {
            _log.Warn(
                "execution",
                "Phase 2C BCD deletion is SKIPPED. " +
                $"BcdOperationsImplemented={_executor.IsBcdRetirementAvailable} " +
                $"executeDeletion={request.ExecuteDeletion}.");
            return await HandoffAsync(state, request, survivorReport.Describe());
        }

        var boot1 = BcdIdentifiers.RequireConcreteObjectId(state.Boot1BcdObjectId, "Boot 1");
        if (afterBcd.WithObjectId(boot1).Count == 0)
        {
            _log.Info(
                "execution",
                "Boot 1 BCD object is already absent after Phase 2B. Skipping bcdedit /delete.");
            state.BcdDeletionPerformed = false;
            state = _coordinator.Persist(state);
            return await HandoffAsync(state, request, survivorReport.Describe());
        }

        Guid? recovery = BcdIdentifiers.TryParseObjectId(state.RecoveryId, out var recoveryId)
            ? recoveryId
            : null;
        var resolved = BcdRetirementTargetResolver.Resolve(
            boot1,
            BcdIdentifiers.RequireConcreteObjectId(state.Boot2BcdObjectId, "Boot 2"),
            recovery,
            afterBcd,
            state.Boot1Identity,
            state.Boot2Identity);

        if (!resolved.Passed || resolved.Target is null)
        {
            var message =
                "Pre-delete BCD resolve failed during Phase 2C resume. No bcdedit /delete was started." +
                Environment.NewLine + resolved.Report.Describe();
            _coordinator.MarkFailed(state, message);
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
        }

        try
        {
            var bcdResult = await _executor.DeleteBoot1BcdEntryAsync(state, resolved.Report, explicitOptIn: true);
            state.BcdDeletionPerformed = true;
            state = _coordinator.Transition(
                state,
                RetirementStatus.BcdUpdated,
                bcdResult.Message);
        }
        catch (Exception exception) when (exception is RetirementExecutionException or RetirementNotImplementedException)
        {
            _coordinator.MarkFailed(state, exception.Message);
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, exception.Message);
        }

        return await HandoffAsync(state, request, survivorReport.Describe());
    }

    private async Task<RecoveryRunResult> HandoffAsync(
        RetirementState state,
        RecoveryRunRequest request,
        string priorDetail)
    {
        if (request.DryRun)
        {
            return new RecoveryRunResult(
                RecoveryRunOutcome.DryRunCompleted,
                priorDetail + Environment.NewLine + "Dry run: no boot change or restart was made.");
        }

        await _bootManager.SetNextBootAsync(state.Boot2Id);

        if (state.Status == RetirementStatus.Boot1Retired)
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
                $"Boot 2 entry {state.Boot2Id} could not be re-read after setting the boot sequence.";
            _coordinator.MarkFailed(state, message);
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
        }

        state = _coordinator.Transition(
            state,
            RetirementStatus.Verified,
            $"Boot 2 entry re-read after the BCD update: {verification.Describe()}.");

        await _bootManager.RestartAsync(_options.RestartDelaySeconds);

        return new RecoveryRunResult(
            RecoveryRunOutcome.HandoffScheduled,
            priorDetail + Environment.NewLine +
            $"Boot 2 ({state.Boot2Id}) set as the next boot and a restart was scheduled in " +
            $"{_options.RestartDelaySeconds} second(s).");
    }

    private TargetIdentification IdentifyTarget(RetirementState state)
    {
        if (state.Boot1Identity is null || state.Boot2Identity is null)
        {
            return TargetIdentification.Failed(new ValidationReport("Production target resolve"));
        }

        var observedBoot1 = _diskValidator.TryObserveByGptId(
            state.Boot1Identity.GptPartitionId,
            "Production observation of Boot 1 by recorded GPT unique partition GUID",
            out _);
        if (observedBoot1 is null)
        {
            var report = new ValidationReport("Production target resolve");
            report.Fail("boot1-observed-by-gpt", "Boot 1 GPT GUID was not found.");
            return TargetIdentification.Failed(report);
        }

        var reportGate = _diskValidator.ValidateRetirementTarget(
            state.Boot1Identity,
            observedBoot1,
            state.Boot2Identity);

        return new TargetIdentification(
            reportGate.Passed,
            observedBoot1,
            reportGate);
    }

    private sealed record TargetIdentification(
        bool Passed,
        PartitionIdentity? ObservedBoot1,
        ValidationReport Report)
    {
        public string Describe() => Report.Describe();

        public static TargetIdentification Failed(ValidationReport report) =>
            new(false, null, report);
    }
}
