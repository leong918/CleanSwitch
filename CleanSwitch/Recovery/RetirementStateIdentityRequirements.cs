using CleanSwitch.Models;

namespace CleanSwitch.Recovery;

/// <summary>
/// Destructive execution requires a complete GPT identity in the saved state.
/// Missing disk GPT id, offset, size, or type is a hard refusal. Values are never
/// inferred or backfilled from the live disk.
/// </summary>
public static class RetirementStateIdentityRequirements
{
    public const string MustRegenerateMessage =
        "Retirement state is incomplete and must be regenerated. " +
        "Destructive execution refuses to infer or backfill disk GPT identity, " +
        "partition start offset, partition size, or GPT type. " +
        "Create a new PENDING state with RETIRE SYSTEM so these fields are recorded " +
        "from the live GPT table.";

    public static void ValidateForDestructiveExecution(
        PartitionIdentity expectedBoot1,
        PartitionIdentity expectedBoot2)
    {
        ArgumentNullException.ThrowIfNull(expectedBoot1);
        ArgumentNullException.ThrowIfNull(expectedBoot2);

        var missing = new List<string>();
        missing.AddRange(MissingDestructiveFields(expectedBoot1, "Boot 1"));
        missing.AddRange(MissingDestructiveFields(expectedBoot2, "Boot 2"));

        if (missing.Count == 0)
        {
            return;
        }

        throw new RetirementExecutionException(
            MustRegenerateMessage +
            Environment.NewLine +
            "Missing: " + string.Join("; ", missing) +
            Environment.NewLine +
            "No disk command was started.");
    }

    public static IReadOnlyList<string> MissingDestructiveFields(PartitionIdentity identity, string role)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(identity.GptPartitionId) ||
            !VolumeLocator.TryParseGptId(identity.GptPartitionId, out _))
        {
            missing.Add($"{role} GPT unique partition id");
        }

        if (string.IsNullOrWhiteSpace(identity.DiskGptUniqueId) ||
            !VolumeLocator.TryParseGptId(identity.DiskGptUniqueId, out _))
        {
            missing.Add($"{role} disk GPT identity");
        }

        if (identity.PartitionStartingOffset is null)
        {
            missing.Add($"{role} partition start offset");
        }

        if (identity.PartitionSizeBytes is null)
        {
            missing.Add($"{role} partition size");
        }

        if (!GptPartitionTypes.TryParse(identity.GptPartitionType, out var type) || type == Guid.Empty)
        {
            missing.Add($"{role} GPT type");
        }

        return missing;
    }
}
