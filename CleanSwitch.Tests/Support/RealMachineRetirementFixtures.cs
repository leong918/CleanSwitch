using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support.Bcd;

namespace CleanSwitch.Tests.Support;

/// <summary>
/// Persisted identities and BCD GUIDs from the real Boot 2 machine after Phase 2B
/// (retirement-state.json @ BOOT1_RETIRED). Used for regression tests only.
/// </summary>
internal static class RealMachineRetirementFixtures
{
    public static readonly Guid Boot1Gpt = Guid.Parse("eab2ae6c-4d1b-4181-873c-3b8f06a1e465");
    public static readonly Guid Boot2Gpt = Guid.Parse("4a16be66-dfc5-4b2a-bf95-a7d7d4d2e6fb");
    public static readonly Guid Boot1Loader = Guid.Parse("fc583d40-a29c-11f1-b0e3-e548a1d3146f");
    public static readonly Guid Boot1Resume = Guid.Parse("fc583d3f-a29c-11f1-b0e3-e548a1d3146f");
    public static readonly Guid Boot1Recovery = Guid.Parse("fc583d41-a29c-11f1-b0e3-e548a1d3146f");
    public static readonly Guid Boot1WinReRamdisk = Guid.Parse("fc583d47-a29c-11f1-b0e3-e548a1d3146f");
    public static readonly Guid Boot2Loader = Guid.Parse("fc583d44-a29c-11f1-b0e3-e548a1d3146f");
    public static readonly Guid Boot2Recovery = Guid.Parse("fc583d45-a29c-11f1-b0e3-e548a1d3146f");
    public static readonly Guid Boot2Resume = Guid.Parse("fc583d43-a29c-11f1-b0e3-e548a1d3146f");
    public static readonly Guid BootMgr = Guid.Parse("9dea862c-5cdd-4e70-acc1-f32b344d4795");
    public static readonly Guid FirmwareBootMgr = Guid.Parse("a5a30fa2-3d06-4e9f-b5f4-a01df9d1fcba");

    public static readonly string[] PersistedSurvivorBcdObjectIds =
    [
        "{a5a30fa2-3d06-4e9f-b5f4-a01df9d1fcba}",
        "{9dea862c-5cdd-4e70-acc1-f32b344d4795}",
        "{fc583d41-a29c-11f1-b0e3-e548a1d3146f}",
        "{fc583d44-a29c-11f1-b0e3-e548a1d3146f}",
        "{fc583d45-a29c-11f1-b0e3-e548a1d3146f}",
        "{fc583d3f-a29c-11f1-b0e3-e548a1d3146f}",
        "{fc583d43-a29c-11f1-b0e3-e548a1d3146f}",
        "{b2721d73-1db4-4c62-bf78-c548a880142d}",
        "{0ce4991b-e6b3-4b16-b23c-5e0d9250e5d9}",
        "{4636856e-540f-4170-a130-a84776f4c654}",
        "{5189b25c-5558-4bf2-bca4-289b11bd29e2}",
        "{7ea2e1ac-2e61-4728-aaa3-896d9d0a9f0e}",
        "{6efb52bf-1766-41db-a6b3-0ee5eff72bd7}",
        "{7ff607e0-4395-11db-b0de-0800200c9a66}",
        "{1afa9c49-16ab-4a5c-901b-212802da9460}",
        "{fc583d46-a29c-11f1-b0e3-e548a1d3146f}",
        "{fc583d47-a29c-11f1-b0e3-e548a1d3146f}"
    ];

    public static RetirementState Boot1RetiredState(BcdSnapshot before)
    {
        var state = new RetirementState
        {
            Status = RetirementStatus.Boot1Retired,
            DestructiveDeletionPerformed = true,
            BcdDeletionPerformed = false,
            Boot1BcdObjectId = BcdIdentifiers.Format(Boot1Loader),
            Boot2BcdObjectId = BcdIdentifiers.Format(Boot2Loader),
            Boot1Identity = new PartitionIdentity
            {
                DiskNumber = 0,
                PartitionNumber = 3,
                GptPartitionId = BcdIdentifiers.Format(Boot1Gpt),
                DiskGptUniqueId = "{5fc0204d-ca71-447b-a7ad-c2f88f654e1a}",
                PartitionStartingOffset = 227540992,
                PartitionSizeBytes = 226434744320,
                GptPartitionType = VolumeLocator.FormatGptId(GptPartitionTypes.BasicData),
                ObservedDriveLetter = "C:\\",
                Source = "real-machine regression fixture"
            },
            Boot2Identity = new PartitionIdentity
            {
                DiskNumber = 0,
                PartitionNumber = 6,
                GptPartitionId = BcdIdentifiers.Format(Boot2Gpt),
                DiskGptUniqueId = "{5fc0204d-ca71-447b-a7ad-c2f88f654e1a}",
                PartitionStartingOffset = 751845769216,
                PartitionSizeBytes = 247539433472,
                GptPartitionType = VolumeLocator.FormatGptId(GptPartitionTypes.BasicData),
                ObservedDriveLetter = "D:\\",
                Source = "real-machine regression fixture"
            },
            SurvivorBcdObjectIds = [.. PersistedSurvivorBcdObjectIds]
        };

        SurvivorInventoryCapture.ApplyToState(state, before, new FakeGptLayoutSource(new GptLayoutSnapshot([], null, [])));
        return state;
    }

    public static BcdSnapshot PreDeleteBcdSnapshot() =>
        new(
            [
                Loader(BootMgr, "Windows Boot Manager", BcdObjectKind.BootManager, resumeObject: Boot2Resume),
                Loader(Boot1Loader, "Boot 1 - Main", resumeObject: Boot1Resume, recoverySequence: Boot1Recovery, device: "partition=C:"),
                Loader(
                    Boot1Recovery,
                    "Windows Recovery Environment",
                    BcdObjectKind.RecoveryLoader,
                    device: $"ramdisk=[F:]\\Recovery\\WindowsRE\\Winre.wim,{BcdIdentifiers.Format(Boot1WinReRamdisk)}",
                    osDevice: $"ramdisk=[F:]\\Recovery\\WindowsRE\\Winre.wim,{BcdIdentifiers.Format(Boot1WinReRamdisk)}"),
                RamdiskOptions(Boot1WinReRamdisk),
                Loader(Boot2Loader, "Boot 2 - Clean", resumeObject: Boot2Resume, recoverySequence: Boot2Recovery, device: "partition=D:"),
                Loader(Boot2Recovery, "Windows Recovery Environment", BcdObjectKind.RecoveryLoader, device: "ramdisk=[HarddiskVolume7]"),
                Resume(Boot1Resume, "partition=C:", recoverySequence: Boot1Recovery),
                Resume(Boot2Resume, "partition=D:", recoverySequence: Boot2Recovery),
                Loader(FirmwareBootMgr, "Firmware Boot Manager", BcdObjectKind.FirmwareBootManager)
            ],
            Boot1Recovery,
            Boot2Loader,
            bootManagerPresent: true,
            [],
            BcdAliasResolution.Resolved,
            BcdAliasResolution.Resolved);

    public static BcdSnapshot PostDeleteBcdSnapshot() =>
        new(
            [
                Loader(BootMgr, "Windows Boot Manager", BcdObjectKind.BootManager, resumeObject: Boot2Resume),
                Loader(
                    Boot1Recovery,
                    "Windows Recovery Environment",
                    BcdObjectKind.RecoveryLoader,
                    device: $"ramdisk=[F:]\\Recovery\\WindowsRE\\Winre.wim,{BcdIdentifiers.Format(Boot1WinReRamdisk)}",
                    osDevice: $"ramdisk=[F:]\\Recovery\\WindowsRE\\Winre.wim,{BcdIdentifiers.Format(Boot1WinReRamdisk)}"),
                RamdiskOptions(Boot1WinReRamdisk),
                Loader(Boot2Loader, "Boot 2 - Clean", resumeObject: Boot2Resume, recoverySequence: Boot2Recovery, device: "partition=D:"),
                Loader(Boot2Recovery, "Windows Recovery Environment", BcdObjectKind.RecoveryLoader, device: "ramdisk=[HarddiskVolume7]"),
                Resume(Boot2Resume, "partition=D:", recoverySequence: Boot2Recovery),
                Loader(FirmwareBootMgr, "Firmware Boot Manager", BcdObjectKind.FirmwareBootManager)
            ],
            Boot1Recovery,
            Boot2Loader,
            bootManagerPresent: true,
            [],
            BcdAliasResolution.Resolved,
            BcdAliasResolution.Resolved);

    private static BcdEntryIdentity Loader(
        Guid id,
        string description,
        BcdObjectKind kind = BcdObjectKind.WindowsLoader,
        Guid? resumeObject = null,
        Guid? recoverySequence = null,
        string device = "partition=X:",
        string? osDevice = null) =>
        new()
        {
            ObjectId = id,
            FormattedId = BcdIdentifiers.Format(id),
            Description = description,
            Path = kind == BcdObjectKind.BootManager
                ? @"\EFI\Microsoft\Boot\bootmgfw.efi"
                : @"\WINDOWS\system32\winload.efi",
            Device = device,
            OsDevice = osDevice ?? device,
            ResumeObject = resumeObject is null ? string.Empty : BcdIdentifiers.Format(resumeObject.Value),
            RecoverySequence = recoverySequence is null ? string.Empty : BcdIdentifiers.Format(recoverySequence.Value),
            Kind = kind
        };

    private static BcdEntryIdentity Resume(Guid id, string device, Guid recoverySequence) =>
        new()
        {
            ObjectId = id,
            FormattedId = BcdIdentifiers.Format(id),
            Description = "Windows Resume Application",
            Path = @"\WINDOWS\system32\winresume.efi",
            Device = device,
            OsDevice = device,
            RecoverySequence = BcdIdentifiers.Format(recoverySequence),
            Kind = BcdObjectKind.ResumeLoader
        };

    private static BcdEntryIdentity RamdiskOptions(Guid id) =>
        new()
        {
            ObjectId = id,
            FormattedId = BcdIdentifiers.Format(id),
            Description = "Ramdisk Options",
            Device = "partition=F:",
            OsDevice = "partition=F:",
            Kind = BcdObjectKind.Other
        };
}
