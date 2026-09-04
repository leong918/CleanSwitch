using CleanSwitch.Models;

namespace CleanSwitch.Services;

/// <summary>
/// The only place that decides whether a retirement state transition is legal.
/// Every edge is declared here; anything not declared is rejected.
/// </summary>
public static class RetirementStateMachine
{
    /// <summary>
    /// The full Phase 2B/2C happy path, in order.
    /// </summary>
    public static readonly RetirementStatus[] HappyPath =
    [
        RetirementStatus.Pending,
        RetirementStatus.RecoveryStarted,
        RetirementStatus.Phase2BReady,
        RetirementStatus.DestructiveIntent,
        RetirementStatus.Boot1Retired,
        RetirementStatus.BcdUpdated,
        RetirementStatus.Verified,
        RetirementStatus.Complete
    ];

    /// <summary>
    /// Production live execution may capture survivors and delete from RECOVERY_STARTED.
    /// </summary>
    private static readonly (RetirementStatus From, RetirementStatus To, string Why)[] ProductionSkipEdges =
    [
        (RetirementStatus.RecoveryStarted, RetirementStatus.Phase2BReady,
            "Production execution captures survivors after pre-delete review."),
        (RetirementStatus.Phase2BReady, RetirementStatus.DestructiveIntent,
            "Durable destructive intent was recorded before the disk command."),
        (RetirementStatus.DestructiveIntent, RetirementStatus.Boot1Retired,
            "Post-command GPT reconciliation proved only Boot 1 was removed.")
    ];

    /// <summary>
    /// Phase 2B-identify still skips deletion. TARGET_VALIDATED is required.
    /// BOOT1_RETIRED is skipped because deletion is not implemented.
    /// </summary>
    private static readonly (RetirementStatus From, RetirementStatus To, string Why)[] Phase2ASkipEdges =
    [
        (RetirementStatus.RecoveryStarted, RetirementStatus.TargetValidated,
            "Read-only identification recorded the exact Boot 1 target."),
        (RetirementStatus.TargetValidated, RetirementStatus.Boot2Validated,
            "Read-only validation proved the Boot 2 entry before a non-destructive handoff."),
        (RetirementStatus.Boot2Validated, RetirementStatus.BcdUpdated,
            "BOOT1_RETIRED is skipped because live deletion is disabled in this build.")
    ];

    private static readonly Dictionary<RetirementStatus, HashSet<RetirementStatus>> Allowed = BuildTable();

    public static bool IsLegal(RetirementStatus from, RetirementStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static bool IsPhase2ASkip(RetirementStatus from, RetirementStatus to) =>
        Phase2ASkipEdges.Any(edge => edge.From == from && edge.To == to);

    public static string? DescribePhase2ASkip(RetirementStatus from, RetirementStatus to) =>
        Phase2ASkipEdges.FirstOrDefault(edge => edge.From == from && edge.To == to).Why;

    public static IReadOnlyCollection<RetirementStatus> LegalTargets(RetirementStatus from) =>
        Allowed.TryGetValue(from, out var targets)
            ? targets.ToArray()
            : [];

    public static string DescribeLegalTargets(RetirementStatus from)
    {
        var targets = LegalTargets(from);
        return targets.Count == 0
            ? "<none, this is a terminal state>"
            : string.Join(", ", targets.Select(RetirementStatusNames.ToWire).OrderBy(name => name, StringComparer.Ordinal));
    }

    private static Dictionary<RetirementStatus, HashSet<RetirementStatus>> BuildTable()
    {
        var table = new Dictionary<RetirementStatus, HashSet<RetirementStatus>>();

        foreach (var status in Enum.GetValues<RetirementStatus>())
        {
            table[status] = [];
        }

        for (var index = 0; index < HappyPath.Length - 1; index++)
        {
            table[HappyPath[index]].Add(HappyPath[index + 1]);
        }

        foreach (var edge in Phase2ASkipEdges)
        {
            table[edge.From].Add(edge.To);
        }

        foreach (var edge in ProductionSkipEdges)
        {
            table[edge.From].Add(edge.To);
        }

        foreach (var status in new[]
                 {
                     RetirementStatus.Pending,
                     RetirementStatus.RecoveryStarted,
                     RetirementStatus.TargetValidated,
                     RetirementStatus.Boot2Validated,
                     RetirementStatus.Phase2BReady,
                     RetirementStatus.Failed
                 })
        {
            table[status].Add(RetirementStatus.Failed);
            table[status].Add(RetirementStatus.Aborted);
            table[status].Add(RetirementStatus.RecoveryRequired);
        }

        table[RetirementStatus.DestructiveIntent].Add(RetirementStatus.RecoveryRequired);
        table[RetirementStatus.Boot1Retired].Add(RetirementStatus.RecoveryRequired);
        table[RetirementStatus.BcdUpdated].Add(RetirementStatus.RecoveryRequired);
        table[RetirementStatus.Verified].Add(RetirementStatus.RecoveryRequired);

        return table;
    }
}
