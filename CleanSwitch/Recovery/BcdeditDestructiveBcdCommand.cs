using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

/// <summary>
/// Production BCD delete. The command line is always <c>bcdedit /delete {{GUID}}</c>.
/// Display names are never used. Optional <paramref name="storePath"/> is only for an
/// isolated temp store; production constructs this with a null store path.
/// </summary>
public sealed class BcdeditDestructiveBcdCommand : IDestructiveBcdCommand
{
    private readonly IOperationLog _log;
    private readonly string? _storePath;

    public BcdeditDestructiveBcdCommand(IOperationLog? log = null, string? storePath = null)
    {
        _log = log ?? NullOperationLog.Instance;
        _storePath = storePath;
    }

    public async Task<DestructiveCommandResult> ExecuteAsync(ResolvedBcdDeletionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!BcdIdentifiers.TryParseObjectId(target.FormattedId, out var parsed) || parsed != target.ObjectId)
        {
            throw new RetirementExecutionException(
                "BCD delete target is not a concrete object GUID. Refusing to start bcdedit.");
        }

        if (BcdIdentifiers.IsProtectedObject(target.ObjectId) || BcdIdentifiers.IsAlias(target.FormattedId))
        {
            throw new RetirementExecutionException(
                $"Refusing to delete protected or alias BCD identity {target.FormattedId}.");
        }

        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(_storePath))
        {
            arguments.Add("/store");
            arguments.Add(_storePath);
        }

        arguments.Add("/delete");
        arguments.Add(target.FormattedId);

        _log.Info(
            "bcdedit",
            "Phase 2C command about to start. " +
            $"objectId={target.FormattedId} store={_storePath ?? "(system BCD)"}");

        var process = await LoggedProcess.RunAsync("bcdedit.exe", arguments, _log);
        return new DestructiveCommandResult(process.ExitCode, process.StdOut, process.StdErr, process.CommandLine);
    }
}
