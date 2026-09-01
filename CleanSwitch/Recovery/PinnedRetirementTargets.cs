namespace CleanSwitch.Recovery;

/// <summary>
/// This test PC's partition table, pinned so live deletion cannot follow a drive letter
/// or a tampered state file to any other volume.
/// <para>
/// Live delete is refused unless the re-resolved target is exactly Boot 1 and Boot 2 is
/// still exactly Boot 2. ESP, MSR and both Recovery partitions are listed so a future
/// change cannot "accidentally" treat them as the target.
/// </para>
/// </summary>
public static class PinnedRetirementTargets
{
    public const int Boot1Disk = 0;
    public const int Boot1Partition = 3;
    public const string Boot1Gpt = "{eab2ae6c-4d1b-4181-873c-3b8f06a1e465}";

    public const int Boot2Disk = 0;
    public const int Boot2Partition = 5;
    public const string Boot2Gpt = "{4a16be66-dfc5-4b2a-bf95-a7d7d4d2e6fb}";

    public const int EfiDisk = 0;
    public const int EfiPartition = 1;
    public const string EfiGpt = "{2d168deb-a7d0-4580-9a99-c8220f1559e5}";

    public const int MsrDisk = 0;
    public const int MsrPartition = 2;

    public const int Boot1WinReDisk = 0;
    public const int Boot1WinRePartition = 4;
    public const string Boot1WinReGpt = "{ded053b0-a130-4aee-a47b-66e520fb853b}";

    public const int Boot2WinReDisk = 0;
    public const int Boot2WinRePartition = 6;
    public const string Boot2WinReGpt = "{2c26f280-e758-4f5e-9dc6-1083cc7aeba8}";

    public static readonly Guid Boot1GptId = Guid.Parse(Boot1Gpt);
    public static readonly Guid Boot2GptId = Guid.Parse(Boot2Gpt);

    public static bool IsPinnedBoot1(int disk, int partition, Guid gpt) =>
        disk == Boot1Disk && partition == Boot1Partition && gpt == Boot1GptId;

    public static bool IsPinnedBoot2(int disk, int partition, Guid gpt) =>
        disk == Boot2Disk && partition == Boot2Partition && gpt == Boot2GptId;

    public static bool IsProtectedPartition(int disk, int partition) =>
        (disk == EfiDisk && partition == EfiPartition) ||
        (disk == MsrDisk && partition == MsrPartition) ||
        (disk == Boot1WinReDisk && partition == Boot1WinRePartition) ||
        (disk == Boot2WinReDisk && partition == Boot2WinRePartition) ||
        (disk == Boot2Disk && partition == Boot2Partition);

    public static string DescribeBoot1() =>
        $"Boot 1 - Main, disk {Boot1Disk} partition {Boot1Partition}, GPT {Boot1Gpt}";

    public static string DescribeBoot2() =>
        $"Boot 2 - Clean, disk {Boot2Disk} partition {Boot2Partition}, GPT {Boot2Gpt}";
}
