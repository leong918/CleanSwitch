using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

/// <summary>
/// Phase 2C BCD delete engine. Production always constructs this with
/// <paramref name="bcdOperationsImplemented"/> false.
/// </summary>
public sealed class DestructiveBcdRetirementEngine
{
    private readonly IBcdStoreSource _store;
    private readonly IDestructiveBcdCommand _command;
    private readonly IOperationLog _log;
    private readonly bool _implemented;

    public DestructiveBcdRetirementEngine(
        IBcdStoreSource store,
        IDestructiveBcdCommand command,
        IOperationLog? log,
        bool bcdOperationsImplemented)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _log = log ?? NullOperationLog.Instance;
        _implemented = bcdOperationsImplemented;
    }

    public async Task<RetirementExecutionResult> ExecuteAsync(
        RetirementState state,
        bool explicitOptIn,
        ValidationReport priorValidation)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(priorValidation);

        _log.Info(
            "bcd-executor",
            "Phase 2C gates: " +
            $"BcdOperationsImplemented={_implemented} explicitOptIn={explicitOptIn} " +
            $"validation.Passed={priorValidation.Passed}");

        if (!_implemented)
        {
            throw new RetirementNotImplementedException(
                "Phase 2C BCD deletion is disabled (BcdOperationsImplemented is false). " +
                "No bcdedit /delete was started.");
        }

        if (!explicitOptIn)
        {
            throw new RetirementExecutionException(
                "Refusing Phase 2C: explicitOptIn is false. No bcdedit /delete was started.");
        }

        if (!priorValidation.Passed)
        {
            throw new RetirementExecutionException(
                "Refusing Phase 2C: prior validation did not pass. No bcdedit /delete was started.");
        }

        BcdRetirementStateRequirements.ValidateForDestructiveExecution(state);

        var boot1 = BcdIdentifiers.RequireConcreteObjectId(state.Boot1BcdObjectId, "Boot 1");
        var boot2 = BcdIdentifiers.RequireConcreteObjectId(state.Boot2BcdObjectId, "Boot 2");
        Guid? recovery = BcdIdentifiers.TryParseObjectId(state.RecoveryId, out var recoveryId)
            ? recoveryId
            : null;

        BcdSnapshot before;
        try
        {
            before = await _store.CaptureAsync();
        }
        catch (Exception exception) when (exception is not RetirementExecutionException)
        {
            throw new RetirementExecutionException(
                "Live BCD enumeration failed. Fail closed. No bcdedit /delete was started. " +
                exception.Message,
                exception);
        }

        var resolved = BcdRetirementTargetResolver.Resolve(
            boot1,
            boot2,
            recovery,
            before,
            state.Boot1Identity,
            state.Boot2Identity);
        _log.Info("bcd-resolver", resolved.Report.Describe());

        if (!resolved.Passed || resolved.Target is null)
        {
            throw new RetirementExecutionException(
                "Pre-delete BCD resolve failed. No bcdedit /delete was started." +
                Environment.NewLine + resolved.Report.Describe());
        }

        _log.Info(
            "bcd-executor",
            "Every pre-delete BCD guard result is listed above. Invoking bcdedit /delete next. " +
            resolved.Target.Describe());

        DestructiveCommandResult commandResult;
        try
        {
            commandResult = await _command.ExecuteAsync(resolved.Target);
        }
        catch (Exception exception) when (exception is not RetirementExecutionException)
        {
            throw new RetirementExecutionException(
                "BCD executor threw. Treating as hard failure. Not logging a successful deletion. " +
                exception.Message,
                exception);
        }

        _log.Write(
            commandResult.Succeeded ? OperationLogLevel.Info : OperationLogLevel.Warning,
            "bcd-executor",
            $"BCD command exitCode={commandResult.ExitCode} command={commandResult.CommandLine}");

        if (!commandResult.Succeeded)
        {
            throw new RetirementExecutionException(
                "bcdedit /delete exited non-zero. Not logging a successful deletion. " +
                $"exitCode={commandResult.ExitCode}");
        }

        BcdSnapshot after;
        try
        {
            after = await _store.CaptureAsync();
        }
        catch (Exception exception) when (exception is not RetirementExecutionException)
        {
            throw new RetirementExecutionException(
                "Post-delete BCD enumeration failed. Not advancing retirement state. " +
                exception.Message,
                exception);
        }

        var verify = BcdRetirementTargetResolver.VerifyAfterDelete(
            boot1,
            boot2,
            resolved.ApprovedSurvivorIds,
            before,
            after);
        _log.Info("bcd-executor", verify.Describe());

        if (!verify.Passed)
        {
            throw new RetirementExecutionException(
                "Post-delete BCD verification failed. Not advancing retirement state. " +
                "Not logging a successful deletion." +
                Environment.NewLine + verify.Describe());
        }

        var success =
            "Boot 1 BCD object GUID is gone after a successful bcdedit /delete and post-delete verify. " +
            $"target={resolved.Target.Describe()}";
        _log.Info("bcd-executor", success);
        return new RetirementExecutionResult
        {
            Kind = RetirementExecutionKind.Succeeded,
            DestructiveDeletionOccurred = false,
            Message = success
        };
    }
}
