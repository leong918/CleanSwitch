using CleanSwitch.Models;

namespace CleanSwitch.Recovery;

/// <summary>
/// GUID-authoritative pre-delete resolver. Display names and aliases never select a target.
/// </summary>
public static class BcdRetirementTargetResolver
{
    public static BcdResolveResult Resolve(
        Guid expectedBoot1,
        Guid expectedBoot2,
        Guid? recoveryObjectId,
        BcdSnapshot live,
        PartitionIdentity? expectedBoot1Volume = null,
        PartitionIdentity? expectedBoot2Volume = null)
    {
        var report = new ValidationReport("Phase 2C BCD target resolve");

        if (live.Warnings.Count > 0 && live.Entries.Count == 0)
        {
            report.Fail("bcd-enumerable", "BCD store could not be enumerated safely.");
            return Fail(report);
        }

        if (live.CurrentResolution == BcdAliasResolution.Unresolved)
        {
            report.Fail(
                "current-alias-resolved",
                "{current} could not be resolved to a concrete GUID. Refusing.");
            return Fail(report);
        }

        report.Pass(
            "current-alias-resolved",
            live.CurrentResolution == BcdAliasResolution.Absent
                ? "{current} is absent in this store (no running OS loader)."
                : $"{{current}} resolved to {BcdIdentifiers.Format(live.CurrentObjectId!.Value)}.");

        if (live.DefaultResolution != BcdAliasResolution.Resolved || live.DefaultObjectId is null)
        {
            report.Fail(
                "default-alias-resolved",
                "{default} must resolve to a concrete GUID before deletion.");
            return Fail(report);
        }

        report.Pass(
            "default-alias-resolved",
            $"{{default}} resolved to {BcdIdentifiers.Format(live.DefaultObjectId.Value)}.");

        report.Add(
            "bcd-enumerable",
            live.Entries.Count > 0,
            live.Entries.Count > 0
                ? $"Live BCD snapshot has {live.Entries.Count} object(s)."
                : "Live BCD snapshot is empty. Refusing.");

        if (expectedBoot1 == expectedBoot2)
        {
            report.Fail("boot1-distinct-from-boot2", "Stored Boot 1 and Boot 2 BCD object GUIDs are the same.");
            return Fail(report);
        }

        report.Pass(
            "boot1-distinct-from-boot2",
            $"Boot 1 {BcdIdentifiers.Format(expectedBoot1)} differs from Boot 2 {BcdIdentifiers.Format(expectedBoot2)}.");

        if (BcdIdentifiers.IsProtectedObject(expectedBoot1))
        {
            report.Fail("target-not-protected", "Stored Boot 1 BCD object is a protected well-known object.");
            return Fail(report);
        }

        report.Pass("target-not-protected", "Stored Boot 1 BCD object is not {bootmgr}/firmware/memdiag.");

        var boot1 = live.WithObjectId(expectedBoot1);
        report.Add(
            "boot1-guid-unique",
            boot1.Count == 1,
            boot1.Count == 1
                ? "Exactly one live BCD object has the stored Boot 1 GUID."
                : boot1.Count == 0
                    ? "Stored Boot 1 BCD object GUID is absent."
                    : $"Stored Boot 1 BCD object GUID matched {boot1.Count} objects. Refusing to choose.");

        if (boot1.Count != 1)
        {
            return Fail(report);
        }

        var boot2 = live.WithObjectId(expectedBoot2);
        report.Add(
            "boot2-guid-unique",
            boot2.Count == 1,
            boot2.Count == 1
                ? "Exactly one live BCD object has the stored Boot 2 GUID."
                : "Stored Boot 2 BCD object GUID is not uniquely present.");

        if (boot2.Count != 1)
        {
            return Fail(report);
        }

        var target = boot1[0];
        if (target.IdentifierWasAlias)
        {
            report.Fail("target-is-concrete-guid", "Boot 1 identifier is still an alias. Refusing.");
            return Fail(report);
        }

        report.Pass("target-is-concrete-guid", $"Boot 1 resolved to {target.FormattedId}.");
        report.Pass(
            "display-name-ignored",
            $"Description '{target.Description}' was not used to select the target.");

        AddKindGuards(report, target);
        AddInstallationIdentityGuards(report, target, expectedBoot1Volume, expectedBoot2Volume);

        report.Add(
            "target-is-not-current",
            live.CurrentResolution == BcdAliasResolution.Absent || live.CurrentObjectId != expectedBoot1,
            live.CurrentObjectId == expectedBoot1
                ? "Boot 1 BCD object is the resolved {{current}} loader. Refusing."
                : "Boot 1 BCD object is not the running {{current}} loader.");

        report.Add(
            "default-is-exact-boot2",
            live.DefaultResolution == BcdAliasResolution.Resolved && live.DefaultObjectId == expectedBoot2,
            live.DefaultObjectId == expectedBoot2
                ? "{default} resolves exactly to the Boot 2 survivor."
                : $"{{default}} must resolve exactly to Boot 2 {BcdIdentifiers.Format(expectedBoot2)}; observed " +
                  (live.DefaultObjectId is null ? "<unresolved>." : BcdIdentifiers.Format(live.DefaultObjectId.Value) + "."));

        report.Add(
            "target-is-not-recovery-guid",
            recoveryObjectId is null || recoveryObjectId != expectedBoot1,
            recoveryObjectId == expectedBoot1
                ? "Boot 1 BCD object is the recovery entry. Refusing."
                : "Boot 1 BCD object is not the recovery entry.");

        report.Add(
            "bootmgr-present",
            live.BootManagerPresent,
            live.BootManagerPresent
                ? "{bootmgr} is present."
                : "{bootmgr} is missing. Refusing.");

        if (!report.Passed)
        {
            return Fail(report);
        }

        var approved = new HashSet<Guid>(live.ConcreteObjectIds());
        approved.Remove(expectedBoot1);
        report.Pass("target-pinned-for-this-execution", $"Pinned for this execution only: {target.FormattedId}");
        return new BcdResolveResult
        {
            Passed = true,
            Target = new ResolvedBcdDeletionTarget(expectedBoot1),
            Report = report,
            ApprovedSurvivorIds = approved
        };
    }

    public static ValidationReport VerifyAfterDelete(
        Guid boot1,
        Guid boot2,
        IReadOnlySet<Guid> approvedSurvivors,
        BcdSnapshot before,
        BcdSnapshot after)
    {
        var report = new ValidationReport("Post-delete BCD verification");
        report.Add(
            "boot1-bcd-gone",
            after.WithObjectId(boot1).Count == 0,
            after.WithObjectId(boot1).Count == 0
                ? "Boot 1 BCD object GUID is absent."
                : "Boot 1 BCD object GUID is still present.");

        report.Add(
            "boot2-bcd-present",
            after.WithObjectId(boot2).Count == 1,
            after.WithObjectId(boot2).Count == 1
                ? "Boot 2 BCD object GUID is still unique."
                : $"Boot 2 BCD object GUID matched {after.WithObjectId(boot2).Count}.");

        report.Add(
            "bootmgr-present",
            after.BootManagerPresent,
            after.BootManagerPresent ? "{bootmgr} is still present." : "{bootmgr} disappeared.");

        var defaultOk = after.DefaultResolution == BcdAliasResolution.Resolved && after.DefaultObjectId == boot2;
        report.Add(
            "default-is-exact-boot2",
            defaultOk,
            after.DefaultObjectId is null
                ? "{{default}} could not be resolved after delete."
                : defaultOk
                    ? $"{{default}} resolves exactly to Boot 2 {BcdIdentifiers.Format(boot2)}."
                    : $"{{default}} resolves to {BcdIdentifiers.Format(after.DefaultObjectId.Value)}, not exact Boot 2 {BcdIdentifiers.Format(boot2)}.");

        var beforeIds = before.ConcreteObjectIds();
        var afterIds = after.ConcreteObjectIds();
        var unexpectedMissing = beforeIds
            .Where(id => id != boot1 && !afterIds.Contains(id))
            .ToList();
        report.Add(
            "no-unrelated-object-missing",
            unexpectedMissing.Count == 0,
            unexpectedMissing.Count == 0
                ? "No unrelated BCD objects disappeared."
                : "Unrelated BCD object(s) disappeared: " +
                  string.Join(", ", unexpectedMissing.Select(BcdIdentifiers.Format)));

        var unexpectedNew = afterIds.Where(id => !beforeIds.Contains(id) && id != boot1).ToList();
        report.Add(
            "no-unexpected-object",
            unexpectedNew.Count == 0,
            unexpectedNew.Count == 0
                ? "No unexpected BCD objects appeared."
                : "Unexpected BCD object(s): " + string.Join(", ", unexpectedNew.Select(BcdIdentifiers.Format)));

        return report;
    }

    private static void AddKindGuards(ValidationReport report, BcdEntryIdentity target)
    {
        report.Add(
            "target-is-not-bootmgr",
            target.Kind != BcdObjectKind.BootManager && target.ObjectId != BcdIdentifiers.BootManagerId,
            target.Kind == BcdObjectKind.BootManager
                ? "Target is {bootmgr}. Refusing."
                : "Target is not {bootmgr}.");
        report.Add(
            "target-is-not-firmware",
            target.Kind is not (BcdObjectKind.FirmwareBootManager or BcdObjectKind.FirmwareObject),
            target.Kind is BcdObjectKind.FirmwareBootManager or BcdObjectKind.FirmwareObject
                ? "Target is a firmware BCD object. Refusing."
                : "Target is not a firmware object.");
        report.Add(
            "target-is-not-recovery-object",
            target.Kind != BcdObjectKind.RecoveryLoader,
            target.Kind == BcdObjectKind.RecoveryLoader
                ? "Target is a recovery BCD object. Refusing."
                : "Target is not a recovery object.");
        report.Add(
            "target-is-windows-loader",
            target.Kind == BcdObjectKind.WindowsLoader ||
            (target.Kind == BcdObjectKind.Unknown && target.Path.Contains("winload", StringComparison.OrdinalIgnoreCase)),
            target.Kind == BcdObjectKind.WindowsLoader
                ? "Target is a Windows OSLOADER."
                : $"Target kind is {target.Kind}. Refusing unless it is a Windows loader.");
    }

    private static void AddInstallationIdentityGuards(
        ValidationReport report,
        BcdEntryIdentity target,
        PartitionIdentity? expectedBoot1Volume,
        PartitionIdentity? expectedBoot2Volume)
    {
        Guid? liveGpt = null;
        if (BcdIdentifiers.TryParseEmbeddedGuid(target.OsDevice, out var fromOs))
        {
            liveGpt = fromOs;
        }
        else if (BcdIdentifiers.TryParseEmbeddedGuid(target.Device, out var fromDevice))
        {
            liveGpt = fromDevice;
        }

        var expectedBoot1 = BcdIdentifiers.TryParseEmbeddedGuid(expectedBoot1Volume?.GptPartitionId, out var boot1Gpt)
            ? boot1Gpt
            : (Guid?)null;
        var expectedBoot2 = BcdIdentifiers.TryParseEmbeddedGuid(expectedBoot2Volume?.GptPartitionId, out var boot2Gpt)
            ? boot2Gpt
            : (Guid?)null;

        if (liveGpt is Guid parsed)
        {
            report.Add(
                "boot1-device-not-boot2-gpt",
                expectedBoot2 is null || parsed != expectedBoot2,
                expectedBoot2 is not null && parsed == expectedBoot2
                    ? "Boot 1 BCD device GPT matches the stored Boot 2 installation. Refusing."
                    : "Boot 1 BCD device does not point at Boot 2's GPT partition.");

            if (expectedBoot1 is Guid storedBoot1)
            {
                report.Add(
                    "boot1-device-matches-stored-gpt",
                    parsed == storedBoot1,
                    parsed == storedBoot1
                        ? "Boot 1 BCD device GPT matches the stored Boot 1 installation."
                        : "Boot 1 BCD device GPT does not match the stored Boot 1 installation. Refusing.");
            }
            else
            {
                report.Pass(
                    "boot1-device-gpt-not-authorizing",
                    "BCD device has a GPT GUID but state has no stored Boot 1 GPT to compare. " +
                    "The BCD object GUID remains the deletion identity.");
            }

            return;
        }

        report.Pass(
            "boot1-device-has-no-parseable-gpt",
            "BCD device/osdevice has no parseable GPT GUID " +
            $"(device='{(string.IsNullOrWhiteSpace(target.Device) ? "<none>" : target.Device)}'). " +
            "Drive letters are ignored. The stored BCD object GUID is the identity.");
    }

    private static BcdResolveResult Fail(ValidationReport report) =>
        new() { Passed = false, Target = null, Report = report };
}
