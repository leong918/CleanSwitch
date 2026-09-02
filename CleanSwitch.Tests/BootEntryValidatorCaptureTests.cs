using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support;

namespace CleanSwitch.Tests;

public sealed class BootEntryValidatorCaptureTests
{
    [Fact]
    public void ApplyPartitionTableIdentity_copies_disk_gpt_offset_size_and_type()
    {
        var identity = new PartitionIdentity
        {
            BcdDevice = "partition=C:",
            Source = "should be replaced"
        };

        var located = new LocatedVolume
        {
            VolumeGuidPath = @"\\?\Volume{eab2ae6c-4d1b-4181-873c-3b8f06a1e465}\",
            MountPoints = ["C:\\"],
            DriveType = VolumeDriveType.Fixed,
            DiskNumber = 0,
            PartitionNumber = 3,
            GptPartitionGuid = PinnedRetirementTargets.Boot1GptId,
            GptPartitionId = PinnedRetirementTargets.Boot1Gpt,
            GptPartitionType = GptPartitionTypes.BasicData,
            DiskGptUniqueId = RetirementFixtures.DiskGptId,
            PartitionStartingOffset = RetirementFixtures.Boot1Offset,
            PartitionSizeBytes = RetirementFixtures.Boot1Size,
            Outcome = VolumeIdentityOutcome.Identified
        };

        BootEntryValidator.ApplyPartitionTableIdentity(
            identity,
            located,
            "letter used only to locate; fields from the partition table");

        Assert.Equal("partition=C:", identity.BcdDevice);
        Assert.Equal(PinnedRetirementTargets.Boot1Gpt, identity.GptPartitionId, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(VolumeLocator.FormatGptId(RetirementFixtures.DiskGptId), identity.DiskGptUniqueId, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(VolumeLocator.FormatGptId(GptPartitionTypes.BasicData), identity.GptPartitionType, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(RetirementFixtures.Boot1Offset, identity.PartitionStartingOffset);
        Assert.Equal(RetirementFixtures.Boot1Size, identity.PartitionSizeBytes);
        Assert.Equal(0, identity.DiskNumber);
        Assert.Equal(3, identity.PartitionNumber);
        Assert.Contains("partition table", identity.Source, StringComparison.OrdinalIgnoreCase);

        RetirementStateIdentityRequirements.ValidateForNewPending(
            "{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1}",
            "{bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb2}",
            identity,
            RetirementFixtures.Boot2Identity());
    }
}
