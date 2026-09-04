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
    private readonly IRecoveryRuntimeProof _runtimeProof;

    public ProductionRetirementExecution(
        IBootManager bootManager,
        IRetirementCoordinator coordinator,
        DiskValidator diskValidator,
        RetirementExecutor executor,
        RetirementHardwareReview hardwareReview,
        IBcdStoreSource bcdStore,
        IGptLayoutSource layout,
        CleanSwitchOptions options,
        IOperationLog log,
        IRecoveryRuntimeProof? runtimeProof = null)
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
        _runtimeProof = runtimeProof ?? new WindowsRecoveryRuntimeProof(bcdStore);
    }

    public async Task<RecoveryRunResult> RunAsync(RetirementState state, RecoveryRunRequest request)
    {
        if (state.Status is RetirementStatus.Failed or RetirementStatus.Aborted or RetirementStatus.RecoveryRequired or RetirementStatus.Complete)
        {
            return new RecoveryRunResult(
                RecoveryRunOutcome.Failed,
                $"Production retirement is interlocked for {RetirementStatusNames.ToWire(state.Status)}. No mutation was attempted.");
        }

        if (!request.DryRun && request.ExecuteDeletion)
            RetirementExecutionAuthorization.RequireCommitted(
                state, _options, request.OperationToken, await _runtimeProof.CaptureAsync());

        if (state.Status == RetirementStatus.DestructiveIntent)
            return await ResumeDestructiveIntentAsync(state, request);

        if (state.Status == RetirementStatus.Pending)
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

        if (state.Status is not (RetirementStatus.RecoveryStarted or RetirementStatus.Phase2BReady))
            return InterlockRecoveryRequired(state,
                $"Production execution cannot continue safely from {RetirementStatusNames.ToWire(state.Status)}.");

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

        if (state.Status == RetirementStatus.RecoveryStarted)
        {
            SurvivorInventoryCapture.ApplyToState(state, beforeBcd, _layout);
            state.Phase = "2B-ready";
            state = _coordinator.Transition(
                state,
                RetirementStatus.Phase2BReady,
                "Pre-execution review passed. Survivor inventory captured before any delete.");
        }

        var identification = IdentifyTarget(state);
        if (!identification.Passed || identification.ObservedBoot1 is null)
        {
            var message = "TARGET validation FAILED before Phase 2B delete." +
                          Environment.NewLine + identification.Describe();
            _coordinator.MarkFailed(state, message);
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
        }

        if (request.DryRun)
        {
            return new RecoveryRunResult(
                RecoveryRunOutcome.DryRunCompleted,
                review.Describe() + Environment.NewLine +
                "Dry run: destructive intent was not persisted and no disk command was started.");
        }

        state.DestructiveIntentGptSnapshot = DestructiveIntentReconciliation.Capture(_layout.Capture());
        state.DestructiveIntentAtUtc = DateTimeOffset.UtcNow;
        state.Phase = "2B-destructive-intent";
        state = _coordinator.Transition(
            state,
            RetirementStatus.DestructiveIntent,
            "Durable destructive intent and exact pre-command GPT snapshot persisted.");

        var preCommandBcd = await _bcdStore.CaptureAsync();
        Boot2DefaultInvariant.Require(state, preCommandBcd, "immediately before Boot 1 partition deletion");

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

    private async Task<RecoveryRunResult> ResumeDestructiveIntentAsync(
        RetirementState state,
        RecoveryRunRequest request)
    {
        if (state.Boot1Identity is null || state.Boot2Identity is null ||
            !state.Boot1Identity.TryGetGptId(out var boot1Gpt) ||
            !state.Boot2Identity.TryGetGptId(out var boot2Gpt))
            return InterlockRecoveryRequired(state, "Destructive-intent resume lacks valid Boot 1 or Boot 2 GPT identity.");

        var live = _layout.Capture();
        var boot1Matches = live.WithGptId(boot1Gpt);
        var boot2Matches = live.WithGptId(boot2Gpt);
        if (boot1Matches.Count > 1 || boot2Matches.Count != 1)
            return InterlockRecoveryRequired(
                state,
                $"Destructive-intent resume is ambiguous: Boot1Matches={boot1Matches.Count}, Boot2Matches={boot2Matches.Count}.");

        if (boot1Matches.Count == 0)
        {
            var reconciliation = DestructiveIntentReconciliation.VerifyTargetAbsent(state, live);
            if (!reconciliation.Passed)
                return InterlockRecoveryRequired(state, reconciliation.Describe());

            var bcd = await _bcdStore.CaptureAsync();
            var defaultReport = Boot2DefaultInvariant.Verify(state, bcd, "post-destructive resume");
            if (!defaultReport.Passed)
                return InterlockRecoveryRequired(state, defaultReport.Describe());

            state.Phase = "2B-resumed-after-delete";
            state = _coordinator.RecordBoot1Retired(
                state,
                "Boot 1 was absent and every non-target GPT survivor reconciled against the durable destructive-intent snapshot.",
                deletionOccurred: true);
            return await ContinuePhase2CAsync(state, request, beforeBcd: null);
        }

        var review = _hardwareReview.Run(state);
        if (!review.OverallPassed)
            return InterlockRecoveryRequired(state, review.Describe());

        var identification = IdentifyTarget(state);
        if (!identification.Passed || identification.ObservedBoot1 is null)
            return InterlockRecoveryRequired(state, identification.Describe());

        if (request.DryRun)
            return new RecoveryRunResult(
                RecoveryRunOutcome.DryRunCompleted,
                "Boot 1 remains exactly present at the durable destructive-intent boundary; dry run did not retry deletion.");

        RetirementExecutionAuthorization.RequireCommitted(
            state, _options, request.OperationToken, await _runtimeProof.CaptureAsync());
        var preCommandBcd = await _bcdStore.CaptureAsync();
        Boot2DefaultInvariant.Require(state, preCommandBcd, "destructive-intent retry immediately before partition deletion");
        var result = await _executor.RetireBoot1Async(
            state.Boot1Identity,
            identification.ObservedBoot1,
            state.Boot2Identity,
            identification.Report,
            explicitOptIn: true);
        state.Phase = "2B-deleted-and-verified";
        state = _coordinator.RecordBoot1Retired(state, result.Message, result.DestructiveDeletionOccurred);
        return await ContinuePhase2CAsync(state, request, beforeBcd: null);
    }

    private RecoveryRunResult InterlockRecoveryRequired(RetirementState state, string reason)
    {
        var message = "RECOVERY_REQUIRED: " + reason;
        state.LastError = message;
        _coordinator.Transition(state, RetirementStatus.RecoveryRequired, message);
        _log.Warn("execution", message);
        return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
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

        var survivorReport = BcdSurvivorReconciliation.VerifyBeforeBoot1BcdDelete(
            state,
            afterBcd,
            beforeBcd);
        _log.Info("execution", survivorReport.Describe());

        var boot2Boundary = Boot2DefaultInvariant.Verify(state, afterBcd, "before Boot 1 BCD deletion");
        if (!boot2Boundary.Passed) return InterlockRecoveryRequired(state, boot2Boundary.Describe());

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
            var finalAlreadyAbsent = BcdSurvivorReconciliation.VerifyAfterBoot1PartitionDelete(
                state,
                afterBcd,
                beforeBcd);
            if (!finalAlreadyAbsent.Passed)
            {
                var message = "Phase 2C already-absent verification failed." +
                              Environment.NewLine + finalAlreadyAbsent.Describe();
                _coordinator.MarkFailed(state, message);
                return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
            }

            state.BcdDeletionPerformed = false;
            if (state.Status == RetirementStatus.Boot1Retired)
                state = _coordinator.Transition(state, RetirementStatus.BcdUpdated,
                    "Boot 1 BCD object was absent and exact Boot 2 loader/default verification passed.");
            else
                state = _coordinator.Persist(state);
            return await HandoffAsync(state, request, finalAlreadyAbsent.Describe());
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
            await _executor.DeleteBoot1BcdEntryAsync(state, resolved.Report, explicitOptIn: true);
            state.BcdDeletionPerformed = true;
            state = _coordinator.Persist(state);
        }
        catch (Exception exception) when (exception is RetirementExecutionException or RetirementNotImplementedException)
        {
            _coordinator.MarkFailed(state, exception.Message);
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, exception.Message);
        }

        BcdSnapshot finalBcd;
        try
        {
            finalBcd = await _bcdStore.CaptureAsync();
        }
        catch (Exception exception)
        {
            var message = "Post-delete survivor enumeration failed. Retirement state will not advance to VERIFIED. " +
                          exception.Message;
            _coordinator.MarkFailed(state, message);
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
        }

        var finalReport = BcdSurvivorReconciliation.VerifyAfterBoot1PartitionDelete(
            state,
            finalBcd,
            afterBcd);
        _log.Info("execution", finalReport.Describe());
        if (!finalReport.Passed)
        {
            var message = "Post-delete BCD survivor verification failed. Retirement state will not advance to VERIFIED." +
                          Environment.NewLine + finalReport.Describe();
            _coordinator.MarkFailed(state, message);
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
        }

        var finalBoot2 = Boot2DefaultInvariant.Verify(state, finalBcd, "after Boot 1 BCD deletion");
        if (!finalBoot2.Passed) return InterlockRecoveryRequired(state, finalBoot2.Describe());

        if (state.Status == RetirementStatus.Boot1Retired)
            state = _coordinator.Transition(state, RetirementStatus.BcdUpdated,
                "Boot 1 BCD cleanup and exact Boot 2 loader/default verification completed.");

        return await HandoffAsync(state, request, finalReport.Describe());
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

        if (state.Status == RetirementStatus.Boot1Retired)
        {
            state = _coordinator.Transition(
                state,
                RetirementStatus.BcdUpdated,
                "Boot 1 BCD cleanup and Boot 2 verification completed.");
        }

        var verification = await _bootManager.TryGetEntryAsync(state.Boot2Id);
        if (verification is null)
        {
            var message =
                $"Boot 2 entry {state.Boot2Id} could not be re-read after setting the boot sequence.";
            _coordinator.MarkFailed(state, message);
            return new RecoveryRunResult(RecoveryRunOutcome.Failed, message);
        }

        BcdSnapshot preRestartBcd;
        try
        {
            preRestartBcd = await _bcdStore.CaptureAsync();
        }
        catch (Exception exception)
        {
            return InterlockRecoveryRequired(state, "BCD enumeration failed immediately before reboot: " + exception.Message);
        }

        var preRestartDefault = Boot2DefaultInvariant.Verify(state, preRestartBcd, "immediately before reboot");
        if (!preRestartDefault.Passed)
            return InterlockRecoveryRequired(state, preRestartDefault.Describe());

        if (state.Status != RetirementStatus.Verified)
            state = _coordinator.Transition(
                state,
                RetirementStatus.Verified,
                $"Boot 2 entry re-read after the BCD update: {verification.Describe()}.");

        await _bootManager.SetNextBootAsync(state.Boot2Id);

        var armedBcd = await _bcdStore.CaptureAsync();
        var armedDefault = Boot2DefaultInvariant.Verify(state, armedBcd, "after one-shot Boot 2 arm");
        if (!armedDefault.Passed) return InterlockRecoveryRequired(state, armedDefault.Describe());

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
