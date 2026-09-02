using CleanSwitch.Models;
using CleanSwitch.Recovery;

namespace CleanSwitch.Tests.Support;

internal static class RetirementFixtures
{
    public static readonly Guid DiskGptId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    public static readonly Guid MsrGptId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public const long Boot1Offset = 1_048_576L * 300;
    public const long Boot1Size = 750_000_000_000L;
    public const long Boot2Offset = 1_048_576L * 800_000;
    public const long Boot2Size = 247_000_000_000L;

    public static PartitionIdentity Boot1Identity(
        int disk = PinnedRetirementTargets.Boot1Disk,
        int partition = PinnedRetirementTargets.Boot1Partition,
        string? gpt = PinnedRetirementTargets.Boot1Gpt,
        string? diskGpt = null,
        long? offset = Boot1Offset,
        long? size = Boot1Size,
        string? type = null,
        string? letter = "C:\\") =>
        new()
        {
            DiskNumber = disk,
            PartitionNumber = partition,
            GptPartitionId = gpt,
            DiskGptUniqueId = diskGpt ?? VolumeLocator.FormatGptId(DiskGptId),
            PartitionStartingOffset = offset,
            PartitionSizeBytes = size,
            GptPartitionType = type ?? VolumeLocator.FormatGptId(GptPartitionTypes.BasicData),
            ObservedDriveLetter = letter,
            Source = "test"
        };

    public static PartitionIdentity Boot2Identity(
        string? letter = "D:\\") =>
        new()
        {
            DiskNumber = PinnedRetirementTargets.Boot2Disk,
            PartitionNumber = PinnedRetirementTargets.Boot2Partition,
            GptPartitionId = PinnedRetirementTargets.Boot2Gpt,
            DiskGptUniqueId = VolumeLocator.FormatGptId(DiskGptId),
            PartitionStartingOffset = Boot2Offset,
            PartitionSizeBytes = Boot2Size,
            GptPartitionType = VolumeLocator.FormatGptId(GptPartitionTypes.BasicData),
            ObservedDriveLetter = letter,
            Source = "test"
        };

    public static LivePartition Partition(
        Guid gpt,
        int disk,
        int partition,
        Guid type,
        long offset,
        long size,
        Guid? diskGpt = null,
        bool running = false,
        string? mount = null) =>
        new()
        {
            PartitionGptId = gpt,
            DiskGptId = diskGpt ?? DiskGptId,
            DiskNumber = disk,
            PartitionNumber = partition,
            PartitionType = type,
            StartingOffset = offset,
            SizeBytes = size,
            IsRunningSystemVolume = running,
            MountPoint = mount
        };

    public static IReadOnlyList<LivePartition> StandardPartitions(string? boot1Mount = "C:\\") =>
    [
        Partition(
            Guid.Parse(PinnedRetirementTargets.EfiGpt),
            PinnedRetirementTargets.EfiDisk,
            PinnedRetirementTargets.EfiPartition,
            GptPartitionTypes.EfiSystem,
            1_048_576,
            100_000_000),
        Partition(
            MsrGptId,
            PinnedRetirementTargets.MsrDisk,
            PinnedRetirementTargets.MsrPartition,
            GptPartitionTypes.MicrosoftReserved,
            101_048_576,
            16_000_000),
        Partition(
            PinnedRetirementTargets.Boot1GptId,
            PinnedRetirementTargets.Boot1Disk,
            PinnedRetirementTargets.Boot1Partition,
            GptPartitionTypes.BasicData,
            Boot1Offset,
            Boot1Size,
            mount: boot1Mount),
        Partition(
            Guid.Parse(PinnedRetirementTargets.Boot1WinReGpt),
            PinnedRetirementTargets.Boot1WinReDisk,
            PinnedRetirementTargets.Boot1WinRePartition,
            GptPartitionTypes.MicrosoftRecovery,
            Boot1Offset + Boot1Size,
            800_000_000),
        Partition(
            PinnedRetirementTargets.Boot2GptId,
            PinnedRetirementTargets.Boot2Disk,
            PinnedRetirementTargets.Boot2Partition,
            GptPartitionTypes.BasicData,
            Boot2Offset,
            Boot2Size,
            mount: "D:\\"),
        Partition(
            Guid.Parse(PinnedRetirementTargets.Boot2WinReGpt),
            PinnedRetirementTargets.Boot2WinReDisk,
            PinnedRetirementTargets.Boot2WinRePartition,
            GptPartitionTypes.MicrosoftRecovery,
            Boot2Offset + Boot2Size,
            800_000_000)
    ];

    public static GptLayoutSnapshot StandardLayout(
        IReadOnlyList<LivePartition>? partitions = null,
        Guid? runningSystemGptId = null) =>
        new(
            partitions ?? StandardPartitions(),
            runningSystemGptId ?? Guid.Parse(PinnedRetirementTargets.Boot2WinReGpt),
            []);

    public static GptLayoutSnapshot Without(this GptLayoutSnapshot layout, Guid gpt) =>
        new(layout.Partitions.Where(part => part.PartitionGptId != gpt).ToList(), layout.RunningSystemGptId, layout.Warnings);

    public static GptLayoutSnapshot Replacing(this GptLayoutSnapshot layout, Guid gpt, LivePartition replacement) =>
        new(
            layout.Partitions.Select(part => part.PartitionGptId == gpt ? replacement : part).ToList(),
            layout.RunningSystemGptId,
            layout.Warnings);

    public static GptLayoutSnapshot Adding(this GptLayoutSnapshot layout, LivePartition extra) =>
        new([.. layout.Partitions, extra], layout.RunningSystemGptId, layout.Warnings);

    public static ValidationReport PassingValidation()
    {
        var report = new ValidationReport("test prior validation");
        report.Pass("prior", "passed");
        return report;
    }

    public static ValidationReport FailingValidation()
    {
        var report = new ValidationReport("test prior validation");
        report.Fail("prior", "did not pass");
        return report;
    }

    public static CleanSwitchOptions Options(bool enableDestructive = false) =>
        new()
        {
            EnableDestructiveRetirement = enableDestructive,
            Boot2Guid = "{fc583d44-a29c-11f1-b0e3-e548a1d3146f}"
        };
}
