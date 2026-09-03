using CleanSwitch.Models;

namespace CleanSwitch.Recovery;

/// <summary>
/// Post-Phase-2B BCD observation. Verifies Boot 1 loader is gone, Boot 2 and bootmgr remain,
/// and every required survivor from the pre-delete inventory is still present except objects
/// in the retired Boot 1 dependency graph.
/// </summary>
public static class BcdSurvivorReconciliation
{
    /// <summary>
    /// Phase 2C pre-delete observation. Boot 1 may still be present exactly once because
    /// this runs before bcdedit /delete; every survivor/default invariant must already hold.
    /// </summary>
    public static ValidationReport VerifyBeforeBoot1BcdDelete(
        RetirementState state,
        BcdSnapshot live,
        BcdSnapshot? before = null)
    {
        var report = new ValidationReport("Pre-delete BCD survivor verification");
        var boot1 = BcdIdentifiers.RequireConcreteObjectId(state.Boot1BcdObjectId, "Boot 1");
        var boot1Count = live.WithObjectId(boot1).Count;
        report.Add(
            "boot1-bcd-unambiguous",
            boot1Count <= 1,
            boot1Count <= 1
                ? $"Boot 1 BCD object matched {boot1Count}; present is expected before delete, absent is an idempotent resume."
                : $"Boot 1 BCD object GUID matched {boot1Count} entries. Refusing.");
        AddSurvivorChecks(report, state, live, before, boot1);
        return report;
    }

    public static ValidationReport VerifyAfterBoot1PartitionDelete(
        RetirementState state,
        BcdSnapshot after,
        BcdSnapshot? before = null)
    {
        var report = new ValidationReport("Resume BCD survivor verification");

        var boot1 = BcdIdentifiers.RequireConcreteObjectId(state.Boot1BcdObjectId, "Boot 1");
        report.Add(
            "boot1-bcd-absent",
            after.WithObjectId(boot1).Count == 0,
            after.WithObjectId(boot1).Count == 0
                ? "Intended Boot 1 BCD object GUID is absent."
                : "Boot 1 BCD object GUID is still present.");

        AddSurvivorChecks(report, state, after, before, boot1);
        return report;
    }

    private static void AddSurvivorChecks(
        ValidationReport report,
        RetirementState state,
        BcdSnapshot live,
        BcdSnapshot? before,
        Guid boot1)
    {
        var boot2 = BcdIdentifiers.RequireConcreteObjectId(state.Boot2BcdObjectId, "Boot 2");
        var boot1ExclusiveIds = BcdBoot1DependencyGraph.ResolveExclusiveIds(state, live, before);

        report.Add(
            "boot2-bcd-unique",
            live.WithObjectId(boot2).Count == 1,
            live.WithObjectId(boot2).Count == 1
                ? "Boot 2 BCD object GUID is still unique."
                : $"Boot 2 BCD object GUID matched {live.WithObjectId(boot2).Count}.");

        report.Add(
            "bootmgr-present",
            live.BootManagerPresent,
            live.BootManagerPresent ? "{bootmgr} is still present." : "{bootmgr} is missing.");

        var defaultOk = live.DefaultObjectId is Guid resolvedDefault &&
                        (resolvedDefault == boot2 || IsApprovedSurvivor(resolvedDefault, state, boot1ExclusiveIds, boot1));
        report.Add(
            "default-is-approved-survivor",
            defaultOk,
            live.DefaultObjectId is null
                ? "{default} could not be resolved after delete."
                : defaultOk
                    ? $"{{default}} resolves to {BcdIdentifiers.Format(live.DefaultObjectId.Value)}."
                    : $"{{default}} resolves to {BcdIdentifiers.Format(live.DefaultObjectId.Value)}, which is not an approved survivor.");

        var required = RequiredSurvivorIds(state, boot1ExclusiveIds, boot1);
        var missing = required.Where(id => live.WithObjectId(id).Count == 0).ToList();
        report.Add(
            "required-bcd-survivors-present",
            missing.Count == 0,
            missing.Count == 0
                ? "Every required surviving BCD object GUID is still present."
                : "Required surviving BCD object(s) missing: " +
                  string.Join(", ", missing.Select(BcdIdentifiers.Format)));
    }

    public static IReadOnlySet<Guid> RequiredSurvivorIds(
        RetirementState state,
        IReadOnlySet<Guid> boot1ExclusiveIds,
        Guid boot1BcdObjectId)
    {
        if (state.SurvivorBcdObjectIds is not { Count: > 0 } persisted)
        {
            return new HashSet<Guid>();
        }

        var required = new HashSet<Guid>();
        foreach (var raw in persisted)
        {
            if (!BcdIdentifiers.TryParseObjectId(raw, out var objectId) ||
                objectId == boot1BcdObjectId ||
                boot1ExclusiveIds.Contains(objectId))
            {
                continue;
            }

            required.Add(objectId);
        }

        return required;
    }

    private static bool IsApprovedSurvivor(
        Guid objectId,
        RetirementState state,
        IReadOnlySet<Guid> boot1ExclusiveIds,
        Guid boot1BcdObjectId) =>
        RequiredSurvivorIds(state, boot1ExclusiveIds, boot1BcdObjectId).Contains(objectId);
}
