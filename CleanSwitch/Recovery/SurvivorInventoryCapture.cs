using CleanSwitch.Models;

namespace CleanSwitch.Recovery;

/// <summary>
/// Captures pre-delete GPT and BCD survivor inventories written at PHASE_2B_READY.
/// </summary>
public static class SurvivorInventoryCapture
{
    public static void ApplyToState(RetirementState state, BcdSnapshot bcd, IGptLayoutSource layout)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(bcd);
        ArgumentNullException.ThrowIfNull(layout);

        state.SurvivorBcdObjectIds = bcd.ConcreteObjectIds()
            .Select(BcdIdentifiers.Format)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var boot1 = BcdIdentifiers.RequireConcreteObjectId(state.Boot1BcdObjectId, "Boot 1");
        var boot2 = BcdIdentifiers.RequireConcreteObjectId(state.Boot2BcdObjectId, "Boot 2");
        var exclusive = BcdBoot1DependencyGraph.ComputeExclusive(bcd, boot1, boot2, state.Boot1Identity);
        state.Boot1ExclusiveBcdObjectIds = exclusive
            .Select(BcdIdentifiers.Format)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var layoutSnapshot = layout.Capture();
        state.SurvivorGptSnapshot = layoutSnapshot.Partitions
            .Select(partition => new GptPartitionSnapshot
            {
                PartitionGptId = BcdIdentifiers.Format(partition.PartitionGptId),
                DiskGptUniqueId = partition.DiskGptId is Guid diskGpt
                    ? BcdIdentifiers.Format(diskGpt)
                    : null,
                GptPartitionType = partition.PartitionType is Guid type
                    ? BcdIdentifiers.Format(type)
                    : null,
                PartitionStartingOffset = partition.StartingOffset,
                PartitionSizeBytes = partition.SizeBytes
            })
            .ToList();
    }
}
