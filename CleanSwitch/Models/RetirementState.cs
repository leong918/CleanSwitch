using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleanSwitch.Models;

/// <summary>
/// Lifecycle of a "retire Boot 1" operation. The wire format is the SCREAMING_SNAKE
/// name produced by <see cref="RetirementStatusNames"/>, not the C# identifier.
/// </summary>
[JsonConverter(typeof(RetirementStatusJsonConverter))]
public enum RetirementStatus
{
    Pending,
    RecoveryStarted,
    TargetValidated,
    Boot2Validated,
    Phase2BReady,
    DestructiveIntent,
    Boot1Retired,
    BcdUpdated,
    Verified,
    Complete,
    Failed,
    Aborted,
    RecoveryRequired
}

public static class RetirementStatusNames
{
    private static readonly (RetirementStatus Status, string Wire)[] Map =
    [
        (RetirementStatus.Pending, "PENDING"),
        (RetirementStatus.RecoveryStarted, "RECOVERY_STARTED"),
        (RetirementStatus.TargetValidated, "TARGET_VALIDATED"),
        (RetirementStatus.Boot2Validated, "BOOT2_VALIDATED"),
        (RetirementStatus.Phase2BReady, "PHASE_2B_READY"),
        (RetirementStatus.DestructiveIntent, "DESTRUCTIVE_INTENT"),
        (RetirementStatus.Boot1Retired, "BOOT1_RETIRED"),
        (RetirementStatus.BcdUpdated, "BCD_UPDATED"),
        (RetirementStatus.Verified, "VERIFIED"),
        (RetirementStatus.Complete, "COMPLETE"),
        (RetirementStatus.Failed, "FAILED"),
        (RetirementStatus.Aborted, "ABORTED"),
        (RetirementStatus.RecoveryRequired, "RECOVERY_REQUIRED")
    ];

    public static string ToWire(RetirementStatus status) =>
        Map.First(entry => entry.Status == status).Wire;

    public static bool TryParse(string? value, out RetirementStatus status)
    {
        status = RetirementStatus.Pending;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        foreach (var entry in Map)
        {
            if (string.Equals(entry.Wire, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                status = entry.Status;
                return true;
            }
        }

        return false;
    }
}

public sealed class RetirementStatusJsonConverter : JsonConverter<RetirementStatus>
{
    public override RetirementStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        if (!RetirementStatusNames.TryParse(raw, out var status))
        {
            throw new JsonException($"'{raw}' is not a known CleanSwitch retirement status.");
        }

        return status;
    }

    public override void Write(Utf8JsonWriter writer, RetirementStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(RetirementStatusNames.ToWire(value));
}

/// <summary>
/// One audited state transition. Kept in the state file so an operator can reconstruct
/// exactly how far the operation got before a power loss.
/// </summary>
public sealed class RetirementTransition
{
    public RetirementStatus From { get; set; }

    public RetirementStatus To { get; set; }

    public DateTimeOffset AtUtc { get; set; }

    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Durable record of a retirement operation. This file is the only thing that survives
/// a reboot into recovery, so it carries everything needed to resume or to stop safely.
/// </summary>
public sealed class RetirementState
{
    public const int CurrentSchemaVersion = 4;

    public const int MinimumReadableSchemaVersion = 1;

    public const string RetireBoot1Operation = "RETIRE_BOOT1";

    [JsonPropertyOrder(0)]
    public string Operation { get; set; } = RetireBoot1Operation;

    [JsonPropertyOrder(1)]
    public RetirementStatus Status { get; set; } = RetirementStatus.Pending;

    [JsonPropertyOrder(2)]
    public string Boot1Id { get; set; } = string.Empty;

    [JsonPropertyOrder(3)]
    public string Boot2Id { get; set; } = string.Empty;

    [JsonPropertyOrder(4)]
    public string RecoveryId { get; set; } = string.Empty;

    [JsonPropertyOrder(5)]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [JsonPropertyOrder(6)]
    public DateTimeOffset UpdatedAtUtc { get; set; }

    [JsonPropertyOrder(7)]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Implementation phase that wrote this record ("2A", "2B-identify", "2B", ...).</summary>
    [JsonPropertyOrder(8)]
    public string Phase { get; set; } = "2B-identify";

    /// <summary>
    /// False for every Phase 2A run. Phase 2B flips this only after a real deletion.
    /// A resumed run treats <c>true</c> as "do not attempt deletion again".
    /// </summary>
    [JsonPropertyOrder(9)]
    public bool DestructiveDeletionPerformed { get; set; }

    public string HandoffAuthorizationState { get; set; } = HandoffAuthorizationStates.None;

    public string? HandoffAuthorizationToken { get; set; }

    public string? HandoffAuthorizationBindingSha256 { get; set; }

    public string? HandoffRecoveryBcdObjectId { get; set; }

    public DateTimeOffset? HandoffArmedAtUtc { get; set; }

    public DateTimeOffset? HandoffCommittedAtUtc { get; set; }

    public DateTimeOffset? HandoffDisarmedAtUtc { get; set; }

    public DateTimeOffset? DestructiveIntentAtUtc { get; set; }

    public List<DestructiveIntentPartitionSnapshot>? DestructiveIntentGptSnapshot { get; set; }

    /// <summary>
    /// Phase 2C sets this after a successful <c>bcdedit /delete</c> and post-delete verify.
    /// </summary>
    [JsonPropertyOrder(10)]
    public bool BcdDeletionPerformed { get; set; }

    [JsonPropertyOrder(11)]
    public string MachineName { get; set; } = string.Empty;

    [JsonPropertyOrder(12)]
    public string? LastError { get; set; }

    /// <summary>Stable identifiers for Boot 1, recorded on Boot 1 at PENDING time.</summary>
    [JsonPropertyOrder(13)]
    public PartitionIdentity? Boot1Identity { get; set; }

    /// <summary>Stable identifiers for Boot 2, recorded on Boot 1 at PENDING time.</summary>
    [JsonPropertyOrder(14)]
    public PartitionIdentity? Boot2Identity { get; set; }

    /// <summary>Boot 1 identity as observed in recovery by GPT GUID lookup. Audit only.</summary>
    [JsonPropertyOrder(15)]
    public PartitionIdentity? Boot1IdentityObserved { get; set; }

    /// <summary>
    /// Stable identity of the volume this state file itself lives on, recorded when the
    /// operation was created on Boot 1. A later phase running in WinRE or on Boot 2 can
    /// compare it against the volume it actually read the file from, which is the only way
    /// to be sure the two are the same volume: drive letters and Win32 volume GUIDs are
    /// both reassigned per Windows instance.
    /// </summary>
    [JsonPropertyOrder(16)]
    public PartitionIdentity? StateVolumeIdentity { get; set; }

    /// <summary>
    /// Concrete BCD object GUID for Boot 1, recorded on Boot 1 before WinRE.
    /// Phase 2C deletes only this GUID. Never inferred from a display name.
    /// </summary>
    [JsonPropertyOrder(17)]
    public string? Boot1BcdObjectId { get; set; }

    /// <summary>
    /// Concrete BCD object GUID for Boot 2, recorded on Boot 1 before WinRE.
    /// Phase 2C must still see this GUID after Boot 1 is removed.
    /// </summary>
    [JsonPropertyOrder(18)]
    public string? Boot2BcdObjectId { get; set; }

    /// <summary>
    /// Non-target GPT partitions captured before Phase 2B delete. Used to verify no unrelated
    /// partition disappeared.
    /// </summary>
    [JsonPropertyOrder(19)]
    public List<GptPartitionSnapshot>? SurvivorGptSnapshot { get; set; }

    /// <summary>
    /// Concrete BCD object GUIDs present before any delete. Post-2B reconciliation excludes the
    /// retired Boot 1 dependency graph from required survivors.
    /// </summary>
    [JsonPropertyOrder(20)]
    public List<string>? SurvivorBcdObjectIds { get; set; }

    /// <summary>
    /// Boot-1-exclusive BCD object GUIDs captured before Phase 2B delete. These may disappear
    /// when the Boot 1 partition is removed and are excluded from post-2B survivor checks.
    /// </summary>
    [JsonPropertyOrder(21)]
    public List<string>? Boot1ExclusiveBcdObjectIds { get; set; }

    [JsonPropertyOrder(22)]
    public List<RetirementTransition> Transitions { get; set; } = [];

    public bool IsTerminal =>
        Status is RetirementStatus.Complete or RetirementStatus.Aborted or RetirementStatus.RecoveryRequired;
}

public static class HandoffAuthorizationStates
{
    public const string None = "NONE";
    public const string Preparing = "PREPARING";
    public const string Armed = "ARMED";
    public const string Committed = "COMMITTED";
    public const string Disarmed = "DISARMED";
    public const string RecoveryRequired = "RECOVERY_REQUIRED";
}

public sealed class DestructiveIntentPartitionSnapshot
{
    public required string PartitionGptId { get; set; }
    public string? DiskGptUniqueId { get; set; }
    public required int DiskNumber { get; set; }
    public required int PartitionNumber { get; set; }
    public string? GptPartitionType { get; set; }
    public required long StartingOffset { get; set; }
    public required long SizeBytes { get; set; }
    public bool IsRunningSystemVolume { get; set; }
}
