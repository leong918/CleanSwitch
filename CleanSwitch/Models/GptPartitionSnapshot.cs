namespace CleanSwitch.Models;

/// <summary>
/// One GPT partition row captured before Phase 2B delete for post-delete verification.
/// </summary>
public sealed class GptPartitionSnapshot
{
    public string? PartitionGptId { get; set; }

    public string? DiskGptUniqueId { get; set; }

    public string? GptPartitionType { get; set; }

    public long? PartitionStartingOffset { get; set; }

    public long? PartitionSizeBytes { get; set; }
}
