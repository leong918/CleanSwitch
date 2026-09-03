using CleanSwitch.Models;
using CleanSwitch.Recovery;

namespace CleanSwitch.Services;

/// <summary>
/// Contexts have deliberately different state-location rules. Creating a new operation
/// may use the running Windows volume as a conservative Boot 1 boundary. Existing schema-v2
/// operations instead use the operation's persisted Boot 1 GPT identity. Operator abandon
/// is a non-destructive cleanup path and never authorises recovery execution.
/// </summary>
public enum RetirementStateAccessContext
{
    CreateNewOperation,
    ExistingOperation,
    OperatorAbandon
}

/// <summary>
/// Proves that the volume hosting retirement state is not the retiring Boot 1 partition.
/// Drive letters, mount points and Win32 volume GUIDs are intentionally excluded.
/// </summary>
public static class StateVolumeSafetyValidator
{
    public static void ValidateForNewOperation(
        PartitionIdentity? resolvedStateVolume,
        PartitionIdentity retiringBoot1,
        GptLayoutSnapshot live)
    {
        ArgumentNullException.ThrowIfNull(retiringBoot1);
        ArgumentNullException.ThrowIfNull(live);

        var liveStateVolume = ResolveCompleteIdentity(resolvedStateVolume, "state volume", live);
        var liveBoot1 = ResolveCompleteIdentity(retiringBoot1, "retiring Boot 1", live);
        RefuseSameOrAmbiguousVolume(liveStateVolume, liveBoot1, "new retirement operation");
    }

    public static void ValidateExistingSchema2(
        RetirementState state,
        PartitionIdentity? resolvedStateVolume,
        GptLayoutSnapshot live)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(live);

        if (state.SchemaVersion != RetirementState.CurrentSchemaVersion)
        {
            throw new RetirementStorageException(
                $"Schema-v2 state-location validation cannot validate schemaVersion {state.SchemaVersion}.");
        }

        var liveStateVolume = ResolveCompleteIdentity(resolvedStateVolume, "resolved state volume", live);
        var persistedStateVolume = ResolveCompleteIdentity(state.StateVolumeIdentity, "persisted state volume", live);
        RequireSamePartition(
            liveStateVolume,
            persistedStateVolume,
            "The state file is no longer on the schema-v2 volume recorded when the operation was created.");

        var requireLiveBoot1 = !state.DestructiveDeletionPerformed;
        if (requireLiveBoot1)
        {
            var liveBoot1 = ResolveCompleteIdentity(state.Boot1Identity, "persisted retiring Boot 1", live);
            RefuseSameOrAmbiguousVolume(liveStateVolume, liveBoot1, "existing schema-v2 operation");
            return;
        }

        // After a confirmed Phase 2B delete, Boot 1 is expected to be absent. Its persisted
        // identity must still be complete, and a copied state file with the same GPT id is
        // never accepted as "different" merely because another field disagrees.
        RequireComplete(state.Boot1Identity, "persisted retiring Boot 1");
        if (!state.Boot1Identity!.TryGetGptId(out var boot1Gpt))
        {
            throw new RetirementStorageException("Persisted retiring Boot 1 GPT identity is invalid. Fail closed.");
        }

        if (liveStateVolume.PartitionGptId == boot1Gpt)
        {
            throw new RetirementStorageException(
                "The state volume has the persisted retiring Boot 1 GPT partition id. " +
                "Post-delete state-location validation refuses this ambiguous or unsafe identity.");
        }
    }

    public static void ValidateLegacy(bool stateVolumeIsRunningWindows, bool allowStateOnSystemVolume)
    {
        if (stateVolumeIsRunningWindows && !allowStateOnSystemVolume)
        {
            throw new RetirementStorageException(
                "Legacy retirement state has no complete persisted Boot 1 GPT identity. " +
                "Its state location is the running Windows volume, so CleanSwitch cannot prove that it " +
                "survives retirement. Fail closed.");
        }
    }

    private static LivePartition ResolveCompleteIdentity(
        PartitionIdentity? expected,
        string role,
        GptLayoutSnapshot live)
    {
        RequireComplete(expected, role);

        if (!expected!.TryGetGptId(out var gpt))
        {
            throw new RetirementStorageException($"The {role} GPT partition id is invalid. Fail closed.");
        }

        var matches = live.WithGptId(gpt);
        if (matches.Count != 1)
        {
            throw new RetirementStorageException(
                $"The {role} GPT partition id {VolumeLocator.FormatGptId(gpt)} matched {matches.Count} " +
                "live partitions. Exactly one match is required. Fail closed.");
        }

        var match = matches[0];
        if (!IdentityMatchesLive(expected!, match))
        {
            throw new RetirementStorageException(
                $"The {role} identity does not exactly match the live GPT partition. " +
                "Disk GPT id, partition GPT id, start offset, size and GPT type must all match. Fail closed.");
        }

        return match;
    }

    private static void RequireComplete(PartitionIdentity? identity, string role)
    {
        if (identity is null)
        {
            throw new RetirementStorageException($"The {role} identity is missing. Fail closed.");
        }

        var missing = RetirementStateIdentityRequirements.MissingDestructiveFields(identity, role);
        if (missing.Count > 0)
        {
            throw new RetirementStorageException(
                $"The {role} identity is incomplete or untrusted. Missing: {string.Join("; ", missing)}. " +
                "Fail closed.");
        }
    }

    private static bool IdentityMatchesLive(PartitionIdentity expected, LivePartition live)
    {
        var diskMatches = expected.TryGetDiskGptId(out var diskGpt) &&
                          live.DiskGptId is Guid liveDiskGpt &&
                          diskGpt == liveDiskGpt;
        var typeMatches = GptPartitionTypes.TryParse(expected.GptPartitionType, out var type) &&
                          live.PartitionType is Guid liveType &&
                          type == liveType;

        return expected.TryGetGptId(out var gpt) &&
               gpt == live.PartitionGptId &&
               diskMatches &&
               expected.PartitionStartingOffset == live.StartingOffset &&
               expected.PartitionSizeBytes == live.SizeBytes &&
               typeMatches;
    }

    private static void RequireSamePartition(LivePartition left, LivePartition right, string failure)
    {
        if (left.PartitionGptId != right.PartitionGptId ||
            left.DiskGptId != right.DiskGptId ||
            left.StartingOffset != right.StartingOffset ||
            left.SizeBytes != right.SizeBytes ||
            left.PartitionType != right.PartitionType)
        {
            throw new RetirementStorageException(failure + " Fail closed.");
        }
    }

    private static void RefuseSameOrAmbiguousVolume(
        LivePartition stateVolume,
        LivePartition retiringBoot1,
        string context)
    {
        if (stateVolume.PartitionGptId == retiringBoot1.PartitionGptId)
        {
            throw new RetirementStorageException(
                $"The state volume is the persisted retiring Boot 1 volume during {context}. " +
                "The operation would destroy its own state. Refusing.");
        }

        // Both sides were independently and uniquely re-resolved and matched complete GPT
        // geometry. Different GPT partition ids therefore prove distinct partitions.
    }
}
