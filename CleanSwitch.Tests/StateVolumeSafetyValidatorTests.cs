using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Services;

namespace CleanSwitch.Tests;

public sealed class StateVolumeSafetyValidatorTests
{
    private static readonly Guid DiskGpt = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid OtherDiskGpt = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
    private static readonly Guid Boot1Gpt = Guid.Parse("3a11ea37-200c-4804-8d69-1ea92d452a40");
    private static readonly Guid Boot2Gpt = Guid.Parse("4a16be66-dfc5-4b2a-bf95-a7d7d4d2e6fb");
    private static readonly Guid DataGpt = Guid.Parse("47c8a288-ae3d-4aca-b1ab-d4deceae9d02");
    private const long Boot1Offset = 227_540_992;
    private const long Boot1Size = 225_615_806_464;
    private const long Boot2Offset = 751_845_769_216;
    private const long Boot2Size = 247_461_838_848;
    private const long DataOffset = 226_662_285_312;
    private const long DataSize = 524_286_951_424;

    [Fact]
    public void New_operation_rejects_state_volume_equal_to_boot1()
    {
        var layout = Layout(running: Boot1Gpt);

        var exception = Assert.Throws<RetirementStorageException>(() =>
            StateVolumeSafetyValidator.ValidateForNewOperation(
                Identity(Boot1Gpt, Boot1Offset, Boot1Size, "Z:\\"),
                Identity(Boot1Gpt, Boot1Offset, Boot1Size, "C:\\"),
                layout));

        Assert.Contains("state volume is the persisted retiring Boot 1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void New_operation_allows_independent_partition5()
    {
        StateVolumeSafetyValidator.ValidateForNewOperation(
            Identity(DataGpt, DataOffset, DataSize, "E:\\"),
            Identity(Boot1Gpt, Boot1Offset, Boot1Size, "C:\\"),
            Layout(running: Boot1Gpt));
    }

    [Fact]
    public void Boot2_can_read_schema2_state_hosted_on_boot2_survivor()
    {
        var stateVolume = Identity(Boot2Gpt, Boot2Offset, Boot2Size, "C:\\");
        var state = Pending(stateVolume, Identity(Boot1Gpt, Boot1Offset, Boot1Size, "D:\\"));

        StateVolumeSafetyValidator.ValidateExistingSchema2(
            state,
            stateVolume,
            Layout(running: Boot2Gpt));
    }

    [Fact]
    public void Existing_schema2_rejects_state_volume_equal_to_persisted_boot1()
    {
        var stateVolume = Identity(Boot1Gpt, Boot1Offset, Boot1Size, "C:\\");
        var state = Pending(stateVolume, Identity(Boot1Gpt, Boot1Offset, Boot1Size, "C:\\"));

        Assert.Throws<RetirementStorageException>(() =>
            StateVolumeSafetyValidator.ValidateExistingSchema2(
                state,
                stateVolume,
                Layout(running: Boot2Gpt)));
    }

    [Fact]
    public void Existing_schema2_allows_independent_partition5()
    {
        var stateVolume = Identity(DataGpt, DataOffset, DataSize, "E:\\");
        var state = Pending(stateVolume, Identity(Boot1Gpt, Boot1Offset, Boot1Size, "D:\\"));

        StateVolumeSafetyValidator.ValidateExistingSchema2(
            state,
            stateVolume,
            Layout(running: Boot2Gpt));
    }

    [Fact]
    public void Drive_letter_swap_does_not_change_identity_result()
    {
        var capturedStateVolume = Identity(Boot2Gpt, Boot2Offset, Boot2Size, "E:\\");
        var state = Pending(capturedStateVolume, Identity(Boot1Gpt, Boot1Offset, Boot1Size, "C:\\"));
        var currentlyResolved = Identity(Boot2Gpt, Boot2Offset, Boot2Size, "C:\\");

        StateVolumeSafetyValidator.ValidateExistingSchema2(
            state,
            currentlyResolved,
            Layout(running: Boot2Gpt, boot1Mount: "D:\\", boot2Mount: "C:\\"));
    }

    [Fact]
    public void Same_partition_gpt_with_different_disk_gpt_fails_closed()
    {
        var stateVolume = Identity(DataGpt, DataOffset, DataSize, "E:\\", DiskGpt);
        var state = Pending(stateVolume, Identity(Boot1Gpt, Boot1Offset, Boot1Size, "D:\\", DiskGpt));
        var live = new GptLayoutSnapshot(
            [
                Partition(DataGpt, DataOffset, DataSize, diskGpt: DiskGpt),
                Partition(Boot1Gpt, Boot1Offset, Boot1Size, disk: 1, diskGpt: OtherDiskGpt)
            ],
            null,
            []);

        Assert.Throws<RetirementStorageException>(() =>
            StateVolumeSafetyValidator.ValidateExistingSchema2(state, stateVolume, live));
    }

    [Fact]
    public void Missing_boot1_gpt_fails_closed()
    {
        var stateVolume = Identity(DataGpt, DataOffset, DataSize, "E:\\");
        var incompleteBoot1 = Identity(Boot1Gpt, Boot1Offset, Boot1Size, "D:\\");
        incompleteBoot1.GptPartitionId = null;
        var state = Pending(stateVolume, incompleteBoot1);

        Assert.Throws<RetirementStorageException>(() =>
            StateVolumeSafetyValidator.ValidateExistingSchema2(
                state,
                stateVolume,
                Layout(running: Boot2Gpt)));
    }

    [Fact]
    public void Ambiguous_live_boot1_gpt_fails_closed()
    {
        var stateVolume = Identity(DataGpt, DataOffset, DataSize, "E:\\");
        var state = Pending(stateVolume, Identity(Boot1Gpt, Boot1Offset, Boot1Size, "D:\\"));
        var normal = Layout(running: Boot2Gpt);
        var duplicate = Partition(Boot1Gpt, Boot1Offset + 4096, Boot1Size, disk: 1, diskGpt: OtherDiskGpt);
        var ambiguous = new GptLayoutSnapshot([.. normal.Partitions, duplicate], Boot2Gpt, []);

        Assert.Throws<RetirementStorageException>(() =>
            StateVolumeSafetyValidator.ValidateExistingSchema2(state, stateVolume, ambiguous));
    }

    [Fact]
    public void Production_layout_builder_preserves_duplicate_gpt_rows_and_exact_volume_association()
    {
        var firstVolume = Located(Boot1Gpt, DiskGpt, disk: 0, partition: 3, running: true);
        var secondVolume = Located(Boot1Gpt, OtherDiskGpt, disk: 1, partition: 9, running: false);
        var located = new VolumeLocatorResult([firstVolume, secondVolume], []);

        var snapshot = VolumeLocatorGptLayoutSource.BuildSnapshot(
            located,
            disk => disk == 0
                ? [GptRow(Boot1Gpt, DiskGpt, disk: 0, partition: 3, Boot1Offset, Boot1Size)]
                : [GptRow(Boot1Gpt, OtherDiskGpt, disk: 1, partition: 9, Boot1Offset + 4096, Boot1Size)]);

        var matches = snapshot.WithGptId(Boot1Gpt);
        Assert.Equal(2, matches.Count);
        Assert.True(matches.Single(part => part.DiskNumber == 0).IsRunningSystemVolume);
        Assert.False(matches.Single(part => part.DiskNumber == 1).IsRunningSystemVolume);
    }

    [Fact]
    public void Legacy_state_keeps_conservative_running_volume_policy()
    {
        Assert.Throws<RetirementStorageException>(() =>
            StateVolumeSafetyValidator.ValidateLegacy(
                stateVolumeIsRunningWindows: true,
                allowStateOnSystemVolume: false));

        StateVolumeSafetyValidator.ValidateLegacy(
            stateVolumeIsRunningWindows: false,
            allowStateOnSystemVolume: false);
    }

    [Fact]
    public void Boot1_retired_state_can_be_read_after_confirmed_delete_when_host_is_distinct()
    {
        var stateVolume = Identity(DataGpt, DataOffset, DataSize, "E:\\");
        var state = Pending(stateVolume, Identity(Boot1Gpt, Boot1Offset, Boot1Size, "D:\\"));
        state.Status = RetirementStatus.Boot1Retired;
        state.DestructiveDeletionPerformed = true;
        var withoutBoot1 = new GptLayoutSnapshot(
            Layout(running: Boot2Gpt).Partitions.Where(part => part.PartitionGptId != Boot1Gpt).ToList(),
            Boot2Gpt,
            []);

        StateVolumeSafetyValidator.ValidateExistingSchema2(state, stateVolume, withoutBoot1);
    }

    [Fact]
    public void Copied_schema2_state_whose_recorded_host_differs_from_actual_host_fails_closed()
    {
        var recorded = Identity(Boot2Gpt, Boot2Offset, Boot2Size, "C:\\");
        var actual = Identity(DataGpt, DataOffset, DataSize, "E:\\");
        var state = Pending(recorded, Identity(Boot1Gpt, Boot1Offset, Boot1Size, "D:\\"));

        Assert.Throws<RetirementStorageException>(() =>
            StateVolumeSafetyValidator.ValidateExistingSchema2(state, actual, Layout(running: Boot2Gpt)));
    }

    [Fact]
    public void Allow_state_on_system_volume_defaults_false()
    {
        Assert.False(new CleanSwitchOptions().AllowStateOnSystemVolume);
    }

    private static RetirementState Pending(PartitionIdentity stateVolume, PartitionIdentity boot1) =>
        new()
        {
            SchemaVersion = RetirementState.CurrentSchemaVersion,
            Status = RetirementStatus.Pending,
            Phase = "2B-identify",
            Boot1Identity = boot1,
            StateVolumeIdentity = stateVolume
        };

    private static PartitionIdentity Identity(
        Guid gpt,
        long offset,
        long size,
        string letter,
        Guid? diskGpt = null) =>
        new()
        {
            DiskNumber = 0,
            PartitionNumber = gpt == Boot1Gpt ? 3 : gpt == DataGpt ? 5 : 7,
            GptPartitionId = VolumeLocator.FormatGptId(gpt),
            DiskGptUniqueId = VolumeLocator.FormatGptId(diskGpt ?? DiskGpt),
            PartitionStartingOffset = offset,
            PartitionSizeBytes = size,
            GptPartitionType = VolumeLocator.FormatGptId(GptPartitionTypes.BasicData),
            ObservedDriveLetter = letter,
            Source = "test"
        };

    private static GptLayoutSnapshot Layout(
        Guid running,
        string boot1Mount = "C:\\",
        string boot2Mount = "D:\\") =>
        new(
            [
                Partition(Boot1Gpt, Boot1Offset, Boot1Size, running == Boot1Gpt, boot1Mount),
                Partition(Boot2Gpt, Boot2Offset, Boot2Size, running == Boot2Gpt, boot2Mount),
                Partition(DataGpt, DataOffset, DataSize, false, "E:\\")
            ],
            running,
            []);

    private static LivePartition Partition(
        Guid gpt,
        long offset,
        long size,
        bool running = false,
        string? mount = null,
        int disk = 0,
        Guid? diskGpt = null) =>
        new()
        {
            PartitionGptId = gpt,
            DiskGptId = diskGpt ?? DiskGpt,
            DiskNumber = disk,
            PartitionNumber = gpt == Boot1Gpt ? 3 : gpt == DataGpt ? 5 : 7,
            PartitionType = GptPartitionTypes.BasicData,
            StartingOffset = offset,
            SizeBytes = size,
            IsRunningSystemVolume = running,
            MountPoint = mount
        };

    private static LocatedVolume Located(
        Guid gpt,
        Guid diskGpt,
        int disk,
        int partition,
        bool running) =>
        new()
        {
            VolumeGuidPath = $@"\\?\Volume{{{Guid.NewGuid():D}}}\",
            MountPoints = [running ? "C:\\" : "E:\\"],
            DriveType = VolumeDriveType.Fixed,
            DiskNumber = disk,
            PartitionNumber = partition,
            GptPartitionId = VolumeLocator.FormatGptId(gpt),
            GptPartitionGuid = gpt,
            GptPartitionType = GptPartitionTypes.BasicData,
            DiskGptUniqueId = diskGpt,
            PartitionStartingOffset = Boot1Offset,
            PartitionSizeBytes = Boot1Size,
            IsRunningSystemVolume = running,
            Outcome = VolumeIdentityOutcome.Identified
        };

    private static LocatedGptPartition GptRow(
        Guid gpt,
        Guid diskGpt,
        int disk,
        int partition,
        long offset,
        long size) =>
        new()
        {
            DiskNumber = disk,
            DiskGptUniqueId = diskGpt,
            PartitionNumber = partition,
            GptPartitionId = gpt,
            GptPartitionType = GptPartitionTypes.BasicData,
            StartingOffset = offset,
            SizeBytes = size
        };
}
