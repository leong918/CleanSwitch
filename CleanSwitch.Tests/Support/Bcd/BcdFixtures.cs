using CleanSwitch.Models;
using CleanSwitch.Recovery;

namespace CleanSwitch.Tests.Support.Bcd;

internal static class BcdFixtures
{
    public static readonly Guid Boot1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1");
    public static readonly Guid Boot2 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb2");
    public static readonly Guid Recovery = Guid.Parse("cccccccc-cccc-cccc-cccc-ccccccccccc3");
    public static readonly Guid ExtraLoader = Guid.Parse("dddddddd-dddd-dddd-dddd-ddddddddddd4");

    public static BcdEntryIdentity Loader(
        Guid id,
        string description,
        BcdObjectKind kind = BcdObjectKind.WindowsLoader,
        string path = @"\Windows\system32\winload.efi",
        string device = @"partition=\Device\HarddiskVolume3") =>
        new()
        {
            ObjectId = id,
            FormattedId = BcdIdentifiers.Format(id),
            Description = description,
            Path = path,
            Device = device,
            OsDevice = device,
            Type = "Windows Boot Loader",
            Kind = kind
        };

    public static BcdEntryIdentity BootManager() =>
        new()
        {
            ObjectId = BcdIdentifiers.BootManagerId,
            FormattedId = BcdIdentifiers.Format(BcdIdentifiers.BootManagerId),
            Description = "Windows Boot Manager",
            Path = @"\EFI\Microsoft\Boot\bootmgfw.efi",
            Type = "Windows Boot Manager",
            Kind = BcdObjectKind.BootManager
        };

    public static BcdSnapshot StandardSnapshot(
        IReadOnlyList<BcdEntryIdentity>? entries = null,
        Guid? current = null,
        Guid? defaultId = null) =>
        new(
            entries ??
            [
                BootManager(),
                Loader(Boot1, "Windows 11"),
                Loader(Boot2, "Windows 11"),
                Loader(Recovery, "Windows Recovery Environment", BcdObjectKind.RecoveryLoader, device: "ramdisk=[unknown]\\Recovery\\winre.wim"),
                Loader(ExtraLoader, "Other Windows")
            ],
            current ?? Recovery,
            defaultId ?? Boot2,
            bootManagerPresent: true,
            []);

    public static RetirementState CompleteState(
        int schemaVersion = 2,
        string? boot1Bcd = null,
        string? boot2Bcd = null) =>
        new()
        {
            SchemaVersion = schemaVersion,
            Status = RetirementStatus.Boot1Retired,
            Boot1Id = "{fc583d40-aaaa-aaaa-aaaa-aaaaaaaaaaaa}",
            Boot2Id = "{fc583d44-aaaa-aaaa-aaaa-aaaaaaaaaaaa}",
            RecoveryId = BcdIdentifiers.Format(Recovery),
            Boot1BcdObjectId = boot1Bcd ?? BcdIdentifiers.Format(Boot1),
            Boot2BcdObjectId = boot2Bcd ?? BcdIdentifiers.Format(Boot2)
        };
}
