using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

/// <summary>
/// Thrown by every destructive path in <see cref="RetirementExecutor"/>. The type exists so
/// callers cannot mistake "not implemented" for "nothing to do".
/// </summary>
public sealed class RetirementNotImplementedException : Exception
{
    public RetirementNotImplementedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The only place that will ever be allowed to remove Boot 1.
/// <para>
/// This build implements the deletion <em>plan</em> (which partition, which diskpart
/// script, which verify-after checks). It does not start diskpart, PowerShell,
/// format, or any other process that could change a partition.
/// </para>
/// Three independent guards must all be true before a future live run could execute,
/// and the first one is a hard-coded flag that is <c>false</c> in this build:
///   1. <see cref="DestructiveOperationsImplemented"/> is false.
///   2. The caller must pass <c>explicitOptIn: true</c>.
///   3. <c>CleanSwitch:EnableDestructiveRetirement</c> must be true in appsettings.json.
/// Validation must also have passed. A careless future call that forgets any of these
/// cannot proceed.
/// </summary>
public sealed class RetirementExecutor
{
    /// <summary>
    /// Hard switch for live deletion. Stays false until the dry-run plan has been reviewed
    /// on this test PC and a later change deliberately flips it.
    /// </summary>
    private static readonly bool DestructiveOperationsImplemented = false;

    private readonly CleanSwitchOptions _options;
    private readonly IOperationLog _log;

    public RetirementExecutor(CleanSwitchOptions options, IOperationLog? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? NullOperationLog.Instance;
    }

    /// <summary>
    /// Reports whether a live destructive run could even be attempted. Planning is always
    /// available; this flag is deliberately not a permission to act.
    /// </summary>
    public bool IsDestructiveRetirementAvailable => DestructiveOperationsImplemented;

    /// <summary>
    /// Builds a report of the Boot 1 partition that would be removed. Read-only: enumerates
    /// volumes to confirm size and identity. Never starts diskpart or changes a disk.
    /// </summary>
    public RetirementDeletionPlan BuildDeletionPlan(
        PartitionIdentity expectedBoot1,
        PartitionIdentity observedBoot1,
        PartitionIdentity? boot2,
        ValidationReport validation,
        bool explicitOptIn,
        string? boot1BcdId = null)
    {
        ArgumentNullException.ThrowIfNull(expectedBoot1);
        ArgumentNullException.ThrowIfNull(observedBoot1);
        ArgumentNullException.ThrowIfNull(validation);

        var guards = DescribeGuards(explicitOptIn, validation.Passed);
        var authorised = DestructiveOperationsImplemented &&
                         explicitOptIn &&
                         _options.EnableDestructiveRetirement &&
                         validation.Passed;

        if (!validation.Passed)
        {
            return Incomplete(
                "TARGET validation did not pass, so no deletion target is named.",
                guards,
                boot1BcdId,
                boot2?.GptPartitionId);
        }

        if (!IdentifiersAgree(expectedBoot1, observedBoot1, out var mismatch))
        {
            return Incomplete(
                mismatch,
                guards,
                boot1BcdId,
                boot2?.GptPartitionId);
        }

        if (observedBoot1.DiskNumber is null || observedBoot1.PartitionNumber is null ||
            string.IsNullOrWhiteSpace(observedBoot1.GptPartitionId))
        {
            return Incomplete(
                "Observed Boot 1 identity is missing disk number, partition number, or GPT unique id.",
                guards,
                boot1BcdId,
                boot2?.GptPartitionId);
        }

        if (!VolumeLocator.TryParseGptId(observedBoot1.GptPartitionId, out var gptId))
        {
            return Incomplete(
                $"Observed GPT unique id '{observedBoot1.GptPartitionId}' is not a GUID.",
                guards,
                boot1BcdId,
                boot2?.GptPartitionId);
        }

        var live = VolumeLocator.TryFindUniqueByGptId(gptId, out var locateError);
        if (live is null)
        {
            return Incomplete(
                locateError ?? "Boot 1 GPT unique id was not found as a unique volume in this environment.",
                guards,
                boot1BcdId,
                boot2?.GptPartitionId);
        }

        if (live.DiskNumber is not int disk || live.PartitionNumber is not int partition)
        {
            return Incomplete(
                $"Live volume for {observedBoot1.GptPartitionId} has no disk/partition number.",
                guards,
                boot1BcdId,
                boot2?.GptPartitionId);
        }

        if (disk != observedBoot1.DiskNumber || partition != observedBoot1.PartitionNumber)
        {
            return Incomplete(
                $"Live volume for {observedBoot1.GptPartitionId} is disk {disk}/" +
                $"partition {partition}, not the recorded disk {observedBoot1.DiskNumber}/" +
                $"partition {observedBoot1.PartitionNumber}. Refusing to name a deletion target.",
                guards,
                boot1BcdId,
                boot2?.GptPartitionId);
        }

        GptPartitionTypes.TryParse(
            live.GptPartitionType is null ? observedBoot1.GptPartitionType : VolumeLocator.FormatGptId(live.GptPartitionType.Value),
            out var liveType);

        if (liveType == GptPartitionTypes.EfiSystem ||
            liveType == GptPartitionTypes.MicrosoftReserved ||
            liveType == GptPartitionTypes.MicrosoftRecovery)
        {
            return Incomplete(
                $"Live GPT type is {GptPartitionTypes.Describe(liveType)}. Protected partitions are never a deletion target.",
                guards,
                boot1BcdId,
                boot2?.GptPartitionId);
        }

        if (liveType != GptPartitionTypes.BasicData)
        {
            return Incomplete(
                $"Live GPT type is {GptPartitionTypes.Describe(liveType)}, not Basic Data. Refusing to name a deletion target.",
                guards,
                boot1BcdId,
                boot2?.GptPartitionId);
        }

        if (boot2?.GptPartitionId is not null &&
            VolumeLocator.TryParseGptId(boot2.GptPartitionId, out var boot2Gpt) &&
            boot2Gpt == gptId)
        {
            return Incomplete(
                "Observed Boot 1 GPT unique id is the same as Boot 2. Refusing to name a deletion target.",
                guards,
                boot1BcdId,
                boot2.GptPartitionId);
        }

        var gpt = live.GptPartitionId ?? observedBoot1.GptPartitionId;
        var type = live.GptPartitionType is null
            ? observedBoot1.GptPartitionType
            : VolumeLocator.FormatGptId(live.GptPartitionType.Value);

        var script = string.Join(
            Environment.NewLine,
            [
                $"select disk {disk}",
                $"select partition {partition}",
                "delete partition override"
            ]);

        var steps = new List<PlannedDeletionStep>
        {
            new()
            {
                Name = "re-observe",
                Description =
                    $"Look up GPT unique id {gpt} with VolumeLocator (read-only). Require exactly one match " +
                    $"on disk {disk} partition {partition}.",
                IsDestructive = false,
                Executed = false
            },
            new()
            {
                Name = "re-validate",
                Description =
                    "Require Basic Data, not the running volume, not ESP/MSR/Recovery, not Boot 2.",
                IsDestructive = false,
                Executed = false
            },
            new()
            {
                Name = "delete-partition",
                Description =
                    $"diskpart: select disk {disk}; select partition {partition}; delete partition override.",
                IsDestructive = true,
                Executed = false
            },
            new()
            {
                Name = "confirm-gone",
                Description =
                    $"Re-enumerate volumes. GPT unique id {gpt} must be absent. " +
                    $"Boot 2 GPT unique id {boot2?.GptPartitionId ?? "(unknown)"} must still be present.",
                IsDestructive = false,
                Executed = false
            }
        };

        var plan = new RetirementDeletionPlan
        {
            TargetIdentified = true,
            ExecutionAuthorised = authorised,
            RefusalReason = authorised
                ? "Guards would allow live deletion, but this build never starts diskpart."
                : "REFUSED. No process was started. No partition was changed.",
            DiskNumber = disk,
            PartitionNumber = partition,
            GptPartitionId = gpt,
            GptPartitionType = type,
            Size = LocatedVolume.FormatSize(live.SizeBytes),
            FileSystem = live.FileSystem,
            ObservedMount = live.PrimaryMountPoint ?? observedBoot1.ObservedDriveLetter,
            Boot1BcdId = boot1BcdId,
            Boot2GptPartitionId = boot2?.GptPartitionId,
            DiskpartScript = script,
            GuardLines = guards,
            Steps = steps
        };

        _log.Info("executor", plan.Describe());
        return plan;
    }

    /// <summary>
    /// Live deletion entry point. Always builds and logs the plan first, then refuses to
    /// execute while <see cref="DestructiveOperationsImplemented"/> is false. Even if that
    /// flag is later flipped, this build still does not invoke diskpart.
    /// </summary>
    public Task RetireBoot1Async(
        PartitionIdentity target,
        ValidationReport validation,
        bool explicitOptIn)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(validation);

        var plan = BuildDeletionPlan(
            expectedBoot1: target,
            observedBoot1: target,
            boot2: null,
            validation: validation,
            explicitOptIn: explicitOptIn);

        _log.Warn(
            "executor",
            "RetireBoot1Async was called. Live deletion is not enabled. " +
            $"target=[{target.Describe()}] validationPassed={validation.Passed} " +
            $"explicitOptIn={explicitOptIn} enableDestructiveRetirement={_options.EnableDestructiveRetirement} " +
            $"destructiveOperationsImplemented={DestructiveOperationsImplemented}");

        if (!DestructiveOperationsImplemented)
        {
            throw new RetirementNotImplementedException(
                "Live Boot 1 deletion is not enabled in this build " +
                "(DestructiveOperationsImplemented is false)." +
                Environment.NewLine +
                plan.Describe() +
                Environment.NewLine +
                "No disk was touched. diskpart.exe was not started.");
        }

        if (!explicitOptIn)
        {
            throw new RetirementNotImplementedException(
                "Refusing to retire Boot 1: the caller did not pass explicitOptIn." +
                Environment.NewLine + plan.Describe());
        }

        if (!_options.EnableDestructiveRetirement)
        {
            throw new RetirementNotImplementedException(
                "Refusing to retire Boot 1: CleanSwitch:EnableDestructiveRetirement is not true." +
                Environment.NewLine + plan.Describe());
        }

        if (!validation.Passed)
        {
            throw new RetirementNotImplementedException(
                "Refusing to retire Boot 1: the retirement target did not pass validation." +
                Environment.NewLine + plan.Describe());
        }

        // Guard 1 is false in this build, so this line is unreachable. It exists so that
        // flipping the flag still cannot start diskpart until a later change writes the
        // live runner and removes this throw.
        throw new RetirementNotImplementedException(
            "Live deletion commands are not present in this build. The plan was reported only. " +
            "diskpart.exe was not started." +
            Environment.NewLine + plan.Describe());
    }

    /// <summary>
    /// NOT IMPLEMENTED. Phase 2C would remove the Boot 1 loader object from the BCD store
    /// after the partition has gone. Always throws.
    /// </summary>
    public Task DeleteBoot1BcdEntryAsync(string boot1Guid, bool explicitOptIn)
    {
        _log.Warn(
            "executor",
            $"DeleteBoot1BcdEntryAsync was called for {boot1Guid} (explicitOptIn={explicitOptIn}). " +
            "Phase 2C is NOT IMPLEMENTED; refused. No BCD object was deleted.");

        throw new RetirementNotImplementedException(
            "NOT IMPLEMENTED: removing the Boot 1 BCD entry ('bcdedit /delete') is Phase 2C work. " +
            "No BCD object was deleted.");
    }

    private IReadOnlyList<string> DescribeGuards(bool explicitOptIn, bool validationPassed) =>
    [
        $"DestructiveOperationsImplemented = {DestructiveOperationsImplemented} (must be true)",
        $"EnableDestructiveRetirement     = {_options.EnableDestructiveRetirement} (must be true)",
        $"explicitOptIn                   = {explicitOptIn} (must be true)",
        $"validation.Passed               = {validationPassed} (must be true)"
    ];

    private static RetirementDeletionPlan Incomplete(
        string reason,
        IReadOnlyList<string> guards,
        string? boot1BcdId,
        string? boot2Gpt) =>
        new()
        {
            TargetIdentified = false,
            ExecutionAuthorised = false,
            RefusalReason = reason,
            Boot1BcdId = boot1BcdId,
            Boot2GptPartitionId = boot2Gpt,
            GuardLines = guards
        };

    private static bool IdentifiersAgree(
        PartitionIdentity expected,
        PartitionIdentity observed,
        out string mismatch)
    {
        mismatch = string.Empty;

        if (expected.DiskNumber is null || expected.PartitionNumber is null ||
            observed.DiskNumber is null || observed.PartitionNumber is null)
        {
            mismatch = "Disk number and partition number are required on both expected and observed identities.";
            return false;
        }

        if (expected.DiskNumber != observed.DiskNumber ||
            expected.PartitionNumber != observed.PartitionNumber)
        {
            mismatch =
                $"Expected disk {expected.DiskNumber}/partition {expected.PartitionNumber} does not match " +
                $"observed disk {observed.DiskNumber}/partition {observed.PartitionNumber}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(expected.GptPartitionId) ||
            string.IsNullOrWhiteSpace(observed.GptPartitionId))
        {
            mismatch = "GPT unique id is required on both expected and observed identities.";
            return false;
        }

        if (!VolumeLocator.TryParseGptId(expected.GptPartitionId, out var expectedGpt) ||
            !VolumeLocator.TryParseGptId(observed.GptPartitionId, out var observedGpt) ||
            expectedGpt != observedGpt)
        {
            mismatch =
                $"Expected GPT unique id {expected.GptPartitionId} does not match observed {observed.GptPartitionId}.";
            return false;
        }

        return true;
    }
}
