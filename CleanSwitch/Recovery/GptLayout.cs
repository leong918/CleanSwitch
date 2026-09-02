using CleanSwitch.Models;

namespace CleanSwitch.Recovery;

/// <summary>One live GPT partition, taken from the partition table (not from a drive letter).</summary>
public sealed class LivePartition
{
    public required Guid PartitionGptId { get; init; }

    public Guid? DiskGptId { get; init; }

    public required int DiskNumber { get; init; }

    public required int PartitionNumber { get; init; }

    public Guid? PartitionType { get; init; }

    public required long StartingOffset { get; init; }

    public required long SizeBytes { get; init; }

    public bool IsRunningSystemVolume { get; init; }

    /// <summary>Informational only. Never used to choose a deletion target.</summary>
    public string? MountPoint { get; init; }

    public string Describe() =>
        $"disk={DiskNumber} partition={PartitionNumber} gpt={VolumeLocator.FormatGptId(PartitionGptId)} " +
        $"diskGpt={(DiskGptId is null ? "(none)" : VolumeLocator.FormatGptId(DiskGptId.Value))} " +
        $"type={(PartitionType is null ? "(unknown)" : GptPartitionTypes.Describe(PartitionType))} " +
        $"offset={StartingOffset} size={SizeBytes}" +
        (IsRunningSystemVolume ? " [RUNNING]" : string.Empty);
}

/// <summary>A frozen view of the live GPT layout for one resolve/execute attempt.</summary>
public sealed class GptLayoutSnapshot
{
    public GptLayoutSnapshot(
        IReadOnlyList<LivePartition> partitions,
        Guid? runningSystemGptId,
        IReadOnlyList<string> warnings)
    {
        Partitions = partitions;
        RunningSystemGptId = runningSystemGptId;
        Warnings = warnings;
    }

    public IReadOnlyList<LivePartition> Partitions { get; }

    public Guid? RunningSystemGptId { get; }

    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyList<LivePartition> WithGptId(Guid gptId) =>
        Partitions.Where(partition => partition.PartitionGptId == gptId).ToList();
}

/// <summary>Supplies a live GPT layout. Production reads the machine; tests inject a fake.</summary>
public interface IGptLayoutSource
{
    GptLayoutSnapshot Capture();
}

/// <summary>Production layout source: partition table via <see cref="VolumeLocator"/>.</summary>
public sealed class VolumeLocatorGptLayoutSource : IGptLayoutSource
{
    public GptLayoutSnapshot Capture()
    {
        var located = VolumeLocator.Enumerate();
        var running = located.Volumes.FirstOrDefault(volume => volume.IsRunningSystemVolume);
        Guid? runningGpt = running?.GptPartitionGuid;

        var disks = located.Volumes
            .Select(volume => volume.DiskNumber)
            .Where(number => number is not null)
            .Select(number => number!.Value)
            .Distinct()
            .ToList();

        var partitions = new List<LivePartition>();
        var seen = new HashSet<Guid>();

        foreach (var disk in disks)
        {
            foreach (var row in VolumeLocator.ReadGptTable(disk))
            {
                if (!seen.Add(row.GptPartitionId))
                {
                    continue;
                }

                var volume = located.Volumes.FirstOrDefault(candidate =>
                    candidate.GptPartitionGuid == row.GptPartitionId);

                partitions.Add(new LivePartition
                {
                    PartitionGptId = row.GptPartitionId,
                    DiskGptId = row.DiskGptUniqueId,
                    DiskNumber = row.DiskNumber,
                    PartitionNumber = row.PartitionNumber,
                    PartitionType = row.GptPartitionType,
                    StartingOffset = row.StartingOffset,
                    SizeBytes = row.SizeBytes,
                    IsRunningSystemVolume = volume?.IsRunningSystemVolume ?? false,
                    MountPoint = volume?.PrimaryMountPoint
                });
            }
        }

        return new GptLayoutSnapshot(partitions, runningGpt, located.Warnings);
    }
}

/// <summary>
/// Read-only GPT snapshot of exactly one disk. Used by the VHD integration test so the
/// physical NVMe is never part of the resolve/verify set.
/// </summary>
public sealed class SingleDiskGptLayoutSource : IGptLayoutSource
{
    private readonly int _diskNumber;

    public SingleDiskGptLayoutSource(int diskNumber)
    {
        _diskNumber = diskNumber;
    }

    public GptLayoutSnapshot Capture()
    {
        var located = VolumeLocator.Enumerate();
        var runningOnThisDisk = located.Volumes.FirstOrDefault(volume =>
            volume.IsRunningSystemVolume && volume.DiskNumber == _diskNumber);

        var partitions = VolumeLocator.ReadGptTable(_diskNumber)
            .Select(row =>
            {
                var volume = located.Volumes.FirstOrDefault(candidate =>
                    candidate.GptPartitionGuid == row.GptPartitionId);
                return new LivePartition
                {
                    PartitionGptId = row.GptPartitionId,
                    DiskGptId = row.DiskGptUniqueId,
                    DiskNumber = row.DiskNumber,
                    PartitionNumber = row.PartitionNumber,
                    PartitionType = row.GptPartitionType,
                    StartingOffset = row.StartingOffset,
                    SizeBytes = row.SizeBytes,
                    IsRunningSystemVolume = volume?.IsRunningSystemVolume ?? false,
                    MountPoint = volume?.PrimaryMountPoint
                };
            })
            .ToList();

        return new GptLayoutSnapshot(partitions, runningOnThisDisk?.GptPartitionGuid, located.Warnings);
    }
}

public static class PartitionIdentityExtensions
{
    public static bool TryGetGptId(this PartitionIdentity identity, out Guid gptId) =>
        VolumeLocator.TryParseGptId(identity.GptPartitionId, out gptId);

    public static bool TryGetDiskGptId(this PartitionIdentity identity, out Guid diskGptId) =>
        VolumeLocator.TryParseGptId(identity.DiskGptUniqueId, out diskGptId);
}
