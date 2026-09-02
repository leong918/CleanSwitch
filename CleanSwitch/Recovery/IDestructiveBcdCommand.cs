namespace CleanSwitch.Recovery;

/// <summary>
/// Abstraction over <c>bcdedit /delete {{GUID}}</c>. Production talks to the system store;
/// tests inject a fake or a <c>/store</c>-bound command against a temp file.
/// </summary>
public interface IDestructiveBcdCommand
{
    Task<DestructiveCommandResult> ExecuteAsync(ResolvedBcdDeletionTarget target);
}
