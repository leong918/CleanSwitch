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
        RetirementStatus.TargetValidated,
        RetirementStatus.Boot2Validated,
        RetirementStatus.Boot1Retired,
        RetirementStatus.BcdUpdated,
        RetirementStatus.Verified,
        RetirementStatus.Complete
    ];

    /// <summary>
    /// Phase 2A runs a shortened, non-destructive route. These edges are declared
    /// explicitly so that "skipping deletion" is an auditable decision instead of a
    /// silently tolerated gap in the sequence.
    /// </summary>
    private static readonly (RetirementStatus From, RetirementStatus To, string Why)[] Phase2ASkipEdges =
    [
        (RetirementStatus.RecoveryStarted, RetirementStatus.Boot2Validated,
            "Phase 2A: no partition is deleted, so the deletion target is only reported, never validated for destruction."),
        (RetirementStatus.Boot2Validated, RetirementStatus.BcdUpdated,
            "Phase 2A: BOOT1_RETIRED is skipped because Phase 2A never retires anything.")
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

        // Any non-terminal state may fail or be aborted. FAILED may be re-entered so that a
        // later error can overwrite an earlier one, and may be aborted by an operator.
        foreach (var status in Enum.GetValues<RetirementStatus>())
        {
            if (status is RetirementStatus.Complete or RetirementStatus.Aborted)
            {
                continue;
            }

            table[status].Add(RetirementStatus.Failed);
            table[status].Add(RetirementStatus.Aborted);
        }

        // A failed run may be retried from the beginning of the recovery-side work.
        table[RetirementStatus.Failed].Add(RetirementStatus.RecoveryStarted);

        return table;
    }
}
