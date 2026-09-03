using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

/// <summary>
/// Live deletion engine. Production always constructs this with
/// <paramref name="destructiveOperationsImplemented"/> false. Tests may pass true
/// together with a fake disk command so no real disk is touched.
/// </summary>
public sealed class DestructiveRetirementEngine
{
    private readonly IGptLayoutSource _layout;
    private readonly IDestructiveDiskCommand _command;
    private readonly IOperationLog _log;
    private readonly IRetirementIdentitySet? _identities;
    private readonly bool _implemented;
    private readonly bool _configEnabled;

    public DestructiveRetirementEngine(
        CleanSwitchOptions options,
        IGptLayoutSource layout,
        IDestructiveDiskCommand command,
        IOperationLog? log,
        bool destructiveOperationsImplemented,
        IRetirementIdentitySet? identities = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _log = log ?? NullOperationLog.Instance;
        _identities = identities;
        _implemented = destructiveOperationsImplemented;
        _configEnabled = options.EnableDestructiveRetirement;
    }

    public async Task<RetirementExecutionResult> ExecuteAsync(
        PartitionIdentity expectedBoot1,
        PartitionIdentity expectedBoot2,
        ValidationReport priorValidation,
        bool explicitOptIn)
    {
        ArgumentNullException.ThrowIfNull(expectedBoot1);
        ArgumentNullException.ThrowIfNull(expectedBoot2);
        ArgumentNullException.ThrowIfNull(priorValidation);

        LogGates(explicitOptIn, priorValidation.Passed);

        if (!_implemented)
        {
            throw new RetirementNotImplementedException(
                "Live Boot 1 deletion is disabled (DestructiveOperationsImplemented is false). " +
                "No disk command was started.");
        }

        if (!explicitOptIn)
        {
            throw new RetirementExecutionException(
                "Refusing to retire Boot 1: explicitOptIn is false. Pass --execute-deletion.");
        }

        if (!_configEnabled)
        {
            throw new RetirementExecutionException(
                "Refusing to retire Boot 1: CleanSwitch:EnableDestructiveRetirement is not true.");
        }

        if (!priorValidation.Passed)
        {
            throw new RetirementExecutionException(
                "Refusing to retire Boot 1: prior validation did not pass.");
        }

        RetirementStateIdentityRequirements.ValidateForDestructiveExecution(expectedBoot1, expectedBoot2);

        GptLayoutSnapshot before;
        try
        {
            before = _layout.Capture();
        }
        catch (Exception exception) when (exception is not RetirementExecutionException)
        {
            throw new RetirementExecutionException(
                "Live GPT enumeration failed. Fail closed. No disk command was started. " +
                exception.Message,
                exception);
        }

        var identities = _identities ??
                         RetirementIdentitySet.FromPersistedOperation(expectedBoot1, expectedBoot2, before);
        var resolved = DestructiveTargetResolver.Resolve(expectedBoot1, expectedBoot2, before, identities);
        _log.Info("resolver", resolved.Report.Describe());

        if (!resolved.Passed || resolved.Target is null)
        {
            throw new RetirementExecutionException(
                "Pre-delete GPT resolve failed. No disk command was started." +
                Environment.NewLine + resolved.Report.Describe());
        }

        _log.Info(
            "executor",
            "Pre-delete target (re-resolved from GPT, numbers pinned for this execution only): " +
            resolved.Target.Describe());
        _log.Info(
            "executor",
            "Every pre-delete guard result is listed above. Invoking the disk command next.");

        DestructiveCommandResult commandResult;
        try
        {
            commandResult = await _command.ExecuteAsync(resolved.Target);
        }
        catch (Exception exception) when (exception is not RetirementExecutionException)
        {
            throw new RetirementExecutionException(
                "Destructive executor threw. Treating as hard failure. " +
                "Not logging a successful deletion. " + exception.Message,
                exception);
        }

        _log.Write(
            commandResult.Succeeded ? OperationLogLevel.Info : OperationLogLevel.Warning,
            "executor",
            $"Destructive command exitCode={commandResult.ExitCode} command={commandResult.CommandLine} " +
            $"stdout={Flatten(commandResult.StdOut)} stderr={Flatten(commandResult.StdErr)}");

        if (!commandResult.Succeeded)
        {
            throw new RetirementExecutionException(
                "Destructive command exited non-zero. Not logging a successful deletion. " +
                $"exitCode={commandResult.ExitCode}");
        }

        GptLayoutSnapshot after;
        try
        {
            after = _layout.Capture();
        }
        catch (Exception exception) when (exception is not RetirementExecutionException)
        {
            throw new RetirementExecutionException(
                "Post-delete GPT enumeration failed. Not logging a successful deletion. " +
                "Not advancing retirement state. " + exception.Message,
                exception);
        }

        var verify = DestructiveTargetResolver.VerifyAfterDelete(
            resolved.Target.TargetGptId,
            identities.Boot2GptId,
            identities.ProtectedGptIds,
            before,
            after);
        _log.Info("executor", verify.Describe());

        if (!verify.Passed)
        {
            throw new RetirementExecutionException(
                "Post-delete GPT verification failed. Not advancing retirement state. " +
                "Not logging a successful deletion." +
                Environment.NewLine + verify.Describe());
        }

        var success =
            "Boot 1 GPT unique id is gone after a successful child process and post-delete verify. " +
            $"destructiveDeletionOccurred=true target={resolved.Target.Describe()}";
        _log.Info("executor", success);
        return new RetirementExecutionResult
        {
            Kind = RetirementExecutionKind.Succeeded,
            DestructiveDeletionOccurred = true,
            Message = success
        };
    }

    private void LogGates(bool explicitOptIn, bool validationPassed)
    {
        _log.Info(
            "executor",
            "Live-delete gates: " +
            $"DestructiveOperationsImplemented={_implemented} " +
            $"EnableDestructiveRetirement={_configEnabled} " +
            $"explicitOptIn={explicitOptIn} " +
            $"validation.Passed={validationPassed}");
    }

    private static string Flatten(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "<empty>"
            : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
