using System.Text;
using CleanSwitch.Models;

namespace CleanSwitch.Recovery;

/// <summary>
/// One step that a future live deletion would take. <see cref="Executed"/> is always false
/// in this build: the plan is printed, never sent to diskpart or PowerShell.
/// </summary>
public sealed class PlannedDeletionStep
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>True for the partition-remove step. False for identify / verify / preserve checks.</summary>
    public bool IsDestructive { get; init; }

    public bool Executed { get; init; }
}

/// <summary>
/// Report-only description of the Boot 1 partition that Phase 2B would remove.
/// Building this object never starts a process and never changes a disk.
/// </summary>
public sealed class RetirementDeletionPlan
{
    public bool TargetIdentified { get; init; }

    /// <summary>
    /// True only when every safety guard would allow live deletion, including an enabled
    /// <c>DestructiveOperationsImplemented</c> compile-time profile gate.
    /// </summary>
    public bool ExecutionAuthorised { get; init; }

    public string RefusalReason { get; init; } = string.Empty;

    public int? DiskNumber { get; init; }

    public int? PartitionNumber { get; init; }

    public string? GptPartitionId { get; init; }

    public string? GptPartitionType { get; init; }

    public string? Size { get; init; }

    public string? FileSystem { get; init; }

    public string? ObservedMount { get; init; }

    public string? Boot1BcdId { get; init; }

    public string? Boot2GptPartitionId { get; init; }

    /// <summary>
    /// The diskpart script that a future live run would send. Never written to a file and
    /// never piped to <c>diskpart.exe</c> in this build.
    /// </summary>
    public string DiskpartScript { get; init; } = string.Empty;

    public IReadOnlyList<string> GuardLines { get; init; } = [];

    public IReadOnlyList<PlannedDeletionStep> Steps { get; init; } = [];

    public string Describe()
    {
        var text = new StringBuilder();
        text.AppendLine("======== DELETION PLAN (REPORT ONLY — NOTHING WAS DELETED) ========");

        if (!TargetIdentified)
        {
            text.AppendLine("Would remove: (no target named — plan is incomplete)");
            text.AppendLine($"Reason: {RefusalReason}");
        }
        else
        {
            text.AppendLine("Would remove this Boot 1 partition:");
            text.AppendLine($"  Disk                : {DiskNumber}");
            text.AppendLine($"  Partition           : {PartitionNumber}");
            text.AppendLine($"  GPT unique id       : {GptPartitionId}");
            text.AppendLine($"  GPT type            : {FormatType(GptPartitionType)}");
            text.AppendLine($"  Size                : {Size ?? "(unknown)"}");
            text.AppendLine($"  File system         : {FileSystem ?? "(unknown)"}");
            text.AppendLine($"  Mount (informational): {ObservedMount ?? "(none)"}");
            text.AppendLine("Would preserve:");
            text.AppendLine($"  Boot 2 GPT unique id: {Boot2GptPartitionId ?? "(not recorded)"}");
            text.AppendLine("  EFI System, MSR, and both Recovery partitions");
            text.AppendLine(
                $"  Boot 1 BCD loader   : {Boot1BcdId ?? "(unknown)"} (Phase 2C; not this step)");
        }

        if (Steps.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Planned steps (none of these ran):");
            foreach (var step in Steps)
            {
                var tag = step.IsDestructive ? "DESTRUCTIVE — NOT RUN" : "NOT RUN";
                text.AppendLine($"  [{tag}] {step.Name}: {step.Description}");
            }
        }

        if (!string.IsNullOrWhiteSpace(DiskpartScript))
        {
            text.AppendLine();
            text.AppendLine("Planned diskpart script (NOT sent to diskpart.exe):");
            foreach (var line in DiskpartScript.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                text.AppendLine("  " + line);
            }
        }

        if (GuardLines.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Safety guards (live delete requires every line to be true):");
            foreach (var line in GuardLines)
            {
                text.AppendLine("  " + line);
            }
        }

        text.AppendLine();
        text.AppendLine($"Execution authorised : {ExecutionAuthorised}");
        text.AppendLine($"Outcome              : {RefusalReason}");
        text.AppendLine("================================================================");
        return text.ToString().TrimEnd();
    }

    private static string FormatType(string? gptType)
    {
        if (!GptPartitionTypes.TryParse(gptType, out var type) || type == Guid.Empty)
        {
            return gptType ?? "(unknown)";
        }

        return $"{GptPartitionTypes.Describe(type)} ({VolumeLocator.FormatGptId(type)})";
    }
}
