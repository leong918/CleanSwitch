namespace CleanSwitch.Recovery;

/// <summary>
/// The numbers diskpart may use for this one invocation. They are taken from a live GPT
/// lookup and are not reused on the next run.
/// </summary>
public sealed record ResolvedDeletionTarget(
    Guid TargetGptId,
    Guid? DiskGptId,
    int DiskNumber,
    int PartitionNumber,
    Guid? PartitionType,
    long StartingOffset,
    long SizeBytes)
{
    public string Describe() =>
        $"gpt={VolumeLocator.FormatGptId(TargetGptId)} disk={DiskNumber} partition={PartitionNumber} " +
        $"diskGpt={(DiskGptId is null ? "(none)" : VolumeLocator.FormatGptId(DiskGptId.Value))} " +
        $"type={GptPartitionTypes.Describe(PartitionType)} offset={StartingOffset} size={SizeBytes}";
}

public sealed record DestructiveCommandResult(int ExitCode, string StdOut, string StdErr, string CommandLine)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Abstraction over the child process that removes a partition. Production talks to
/// diskpart; tests inject a fake that never touches a disk.
/// </summary>
public interface IDestructiveDiskCommand
{
    Task<DestructiveCommandResult> ExecuteAsync(ResolvedDeletionTarget target);
}
