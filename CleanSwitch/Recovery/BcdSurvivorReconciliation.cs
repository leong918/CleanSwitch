using CleanSwitch.Models;

namespace CleanSwitch.Recovery;

/// <summary>
/// Post-Phase-2B BCD observation. Verifies Boot 1 loader is gone, Boot 2 and bootmgr remain,
/// and every required survivor from the pre-delete inventory is still present except objects
/// in the retired Boot 1 dependency graph.
/// </summary>
public static class BcdSurvivorReconciliation
{
    public static ValidationReport VerifyAfterBoot1PartitionDelete(
        RetirementState state,
        BcdSnapshot after,
        BcdSnapshot? before = null)
    {
        var report = new ValidationReport("Resume BCD survivor verification");

        var boot1 = BcdIdentifiers.RequireConcreteObjectId(state.Boot1BcdObjectId, "Boot 1");
        var boot2 = BcdIdentifiers.RequireConcreteObjectId(state.Boot2BcdObjectId, "Boot 2");
        var boot1ExclusiveIds = BcdBoot1DependencyGraph.ResolveExclusiveIds(state, after, before);

        report.Add(
            "boot1-bcd-absent",
            after.WithObjectId(boot1).Count == 0,
            after.WithObjectId(boot1).Count == 0
                ? "Intended Boot 1 BCD object GUID is absent."
                : "Boot 1 BCD object GUID is still present.");

        report.Add(
            "boot2-bcd-unique",
            after.WithObjectId(boot2).Count == 1,
            after.WithObjectId(boot2).Count == 1
                ? "Boot 2 BCD object GUID is still unique."
                : $"Boot 2 BCD object GUID matched {after.WithObjectId(boot2).Count}.");

        report.Add(
            "bootmgr-present",
            after.BootManagerPresent,
            after.BootManagerPresent ? "{bootmgr} is still present." : "{bootmgr} is missing.");

        var defaultOk = after.DefaultObjectId is Guid resolvedDefault &&
                        (resolvedDefault == boot2 || IsApprovedSurvivor(resolvedDefault, state, boot1ExclusiveIds, boot1));
        report.Add(
            "default-is-approved-survivor",
            defaultOk,
            after.DefaultObjectId is null
                ? "{default} could not be resolved after delete."
                : defaultOk
                    ? $"{{default}} resolves to {BcdIdentifiers.Format(after.DefaultObjectId.Value)}."
                    : $"{{default}} resolves to {BcdIdentifiers.Format(after.DefaultObjectId.Value)}, which is not an approved survivor.");

        var required = RequiredSurvivorIds(state, boot1ExclusiveIds, boot1);
        var missing = required.Where(id => after.WithObjectId(id).Count == 0).ToList();
        report.Add(
            "required-bcd-survivors-present",
            missing.Count == 0,
            missing.Count == 0
                ? "Every required surviving BCD object GUID is still present."
                : "Required surviving BCD object(s) missing: " +
                  string.Join(", ", missing.Select(BcdIdentifiers.Format)));

        return report;
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
