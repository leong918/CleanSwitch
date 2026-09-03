using System.Text;
using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

/// <summary>
/// Thrown when a destructive entry point is reached while live deletion is disabled.
/// Callers must not treat this as "nothing to do".
/// </summary>
public sealed class RetirementNotImplementedException : Exception
{
    public RetirementNotImplementedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The only place that may remove Boot 1.
/// <para>
/// Live deletion is compiled in <see cref="DestructiveRetirementEngine"/> but is
/// unreachable until every guard is true. Safe builds compile the implementation gates
/// off; the explicit live-test build profile compiles them on for controlled integration
/// and authorized real-machine testing.
/// </para>
/// Guards, all required:
///   1. <see cref="DestructiveOperationsImplemented"/> (compile-time profile gate)
///   2. <c>explicitOptIn</c> from the caller (<c>--execute-deletion</c>)
///   3. <c>CleanSwitch:EnableDestructiveRetirement</c> in appsettings.json
///   4. <c>validation.Passed</c>
///   5. Re-resolved target exactly matches the schema-v2 PENDING Boot 1 identity
///   6. Boot 2 still exactly matches the schema-v2 PENDING survivor identity
/// </summary>
public sealed class RetirementExecutor
{
    private static readonly bool DestructiveOperationsImplemented =
        ProductionRetirementGates.DestructiveOperationsImplemented;

    private static readonly bool BcdOperationsImplemented =
        ProductionRetirementGates.BcdOperationsImplemented;

    private readonly CleanSwitchOptions _options;
    private readonly IOperationLog _log;
    private readonly IGptLayoutSource _layout;
    private readonly DestructiveRetirementEngine _engine;
    private readonly DestructiveBcdRetirementEngine _bcdEngine;

    public RetirementExecutor(
        CleanSwitchOptions options,
        IOperationLog? log = null,
        IGptLayoutSource? layout = null,
        IDestructiveDiskCommand? diskCommand = null,
        IBcdStoreSource? bcdStore = null,
        IDestructiveBcdCommand? bcdCommand = null,
        IBootManager? bootManager = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? NullOperationLog.Instance;
        _layout = layout ?? new VolumeLocatorGptLayoutSource();
        _engine = new DestructiveRetirementEngine(
            _options,
            _layout,
            diskCommand ?? new DiskpartDestructiveDiskCommand(_log),
            _log,
            DestructiveOperationsImplemented);
        _bcdEngine = new DestructiveBcdRetirementEngine(
            bcdStore ?? new BootManagerBcdStoreSource(bootManager ?? new WindowsBootManager(_log)),
            bcdCommand ?? new BcdeditDestructiveBcdCommand(_log),
            _log,
            BcdOperationsImplemented);
    }

    public bool IsDestructiveRetirementAvailable => DestructiveOperationsImplemented;

    public bool IsBcdRetirementAvailable => BcdOperationsImplemented;

    public bool IsConfigEnabled => _options.EnableDestructiveRetirement;

    /// <summary>
    /// Builds a report of the Boot 1 partition that would be removed. Read-only.
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
        var authorised = AreLiveGuardsSatisfied(explicitOptIn, validation.Passed);

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
            return Incomplete(mismatch, guards, boot1BcdId, boot2?.GptPartitionId);
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

        var liveMatches = _layout.Capture().WithGptId(gptId);
        if (liveMatches.Count == 0)
        {
            return Incomplete(
                "Boot 1 GPT unique id was not found as a unique partition in the live GPT layout.",
                guards,
                boot1BcdId,
                boot2?.GptPartitionId);
        }

        if (liveMatches.Count != 1)
        {
            return Incomplete(
                $"Boot 1 GPT unique id matched {liveMatches.Count} partitions. Refusing to choose.",
                guards,
                boot1BcdId,
                boot2?.GptPartitionId);
        }

        var live = liveMatches[0];
        var disk = live.DiskNumber;
        var partition = live.PartitionNumber;

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
            live.PartitionType is null ? observedBoot1.GptPartitionType : VolumeLocator.FormatGptId(live.PartitionType.Value),
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

        var gpt = VolumeLocator.FormatGptId(live.PartitionGptId);
        var type = live.PartitionType is null
            ? observedBoot1.GptPartitionType
            : VolumeLocator.FormatGptId(live.PartitionType.Value);

        var script = BuildDiskpartScript(disk, partition);
        var plan = new RetirementDeletionPlan
        {
            TargetIdentified = true,
            ExecutionAuthorised = authorised,
            RefusalReason = authorised
                ? "Guards would allow live deletion from RetireBoot1Async. Review mode / dry-run do not start diskpart."
                : "REFUSED. No process was started. No partition was changed.",
            DiskNumber = disk,
            PartitionNumber = partition,
            GptPartitionId = gpt,
            GptPartitionType = type,
            Size = LocatedVolume.FormatSize(live.SizeBytes),
            FileSystem = null,
            ObservedMount = live.MountPoint ?? observedBoot1.ObservedDriveLetter,
            Boot1BcdId = boot1BcdId,
            Boot2GptPartitionId = boot2?.GptPartitionId,
            DiskpartScript = script,
            GuardLines = guards,
            Steps =
            [
                new()
                {
                    Name = "re-observe",
                    Description =
                        $"Look up GPT unique id {gpt} from the live GPT layout (read-only). Require exactly one match " +
                        $"on disk {disk} partition {partition}.",
                    IsDestructive = false
                },
                new()
                {
                    Name = "re-validate",
                    Description =
                        "Require pinned Boot 1, Basic Data, not running WinRE, not ESP/MSR/Recovery, not Boot 2.",
                    IsDestructive = false
                },
                new()
                {
                    Name = "delete-partition",
                    Description =
                        $"diskpart.exe /s <temp script>: select disk {disk}; select partition {partition}; " +
                        "delete partition override.",
                    IsDestructive = true
                },
                new()
                {
                    Name = "confirm-gone",
                    Description =
                        $"Re-enumerate. GPT {gpt} must be absent. Boot 2 GPT " +
                        $"{boot2?.GptPartitionId ?? "(missing from operation)"} must still be unique.",
                    IsDestructive = false
                }
            ]
        };

        _log.Info("executor", plan.Describe());
        return plan;
    }

    /// <summary>
    /// Live deletion. Unreachable in this build: guard 1 is false, so this throws before
    /// the engine is reached. When a later change flips that flag, the engine re-enumerates
    /// GPT, re-resolves Boot 1 by unique id, re-checks every guard, then invokes the
    /// injected disk command.
    /// </summary>
    public async Task<RetirementExecutionResult> RetireBoot1Async(
        PartitionIdentity expectedBoot1,
        PartitionIdentity observedBoot1,
        PartitionIdentity boot2,
        ValidationReport validation,
        bool explicitOptIn)
    {
        ArgumentNullException.ThrowIfNull(expectedBoot1);
        ArgumentNullException.ThrowIfNull(observedBoot1);
        ArgumentNullException.ThrowIfNull(boot2);
        ArgumentNullException.ThrowIfNull(validation);

        var plan = BuildDeletionPlan(
            expectedBoot1,
            observedBoot1,
            boot2,
            validation,
            explicitOptIn);

        _log.Info(
            "executor",
            "RetireBoot1Async entered. " +
            $"target=[{observedBoot1.Describe()}] validationPassed={validation.Passed} " +
            $"explicitOptIn={explicitOptIn} enableDestructiveRetirement={_options.EnableDestructiveRetirement} " +
            $"destructiveOperationsImplemented={DestructiveOperationsImplemented}");

        if (!DestructiveOperationsImplemented)
        {
            throw new RetirementNotImplementedException(
                "Live Boot 1 deletion is disabled (DestructiveOperationsImplemented is false)." +
                Environment.NewLine +
                plan.Describe() +
                Environment.NewLine +
                "No disk was touched. diskpart.exe was not started.");
        }

        if (!explicitOptIn)
        {
            throw new RetirementExecutionException(
                "Refusing to retire Boot 1: explicitOptIn is false. Pass --execute-deletion at runtime." +
                Environment.NewLine + plan.Describe());
        }

        if (!_options.EnableDestructiveRetirement)
        {
            throw new RetirementExecutionException(
                "Refusing to retire Boot 1: CleanSwitch:EnableDestructiveRetirement is not true." +
                Environment.NewLine + plan.Describe());
        }

        if (!validation.Passed)
        {
            throw new RetirementExecutionException(
                "Refusing to retire Boot 1: the retirement target did not pass validation." +
                Environment.NewLine + plan.Describe());
        }

        return await _engine.ExecuteAsync(expectedBoot1, boot2, validation, explicitOptIn);
    }

    /// <summary>
    /// Resume helper: Boot 1's GPT is already gone and Boot 2 is still present.
    /// Never starts diskpart. Does not require live-delete guards.
    /// </summary>
    public RetirementExecutionResult AcknowledgeAlreadyDeleted(
        PartitionIdentity expectedBoot1,
        PartitionIdentity boot2)
    {
        ArgumentNullException.ThrowIfNull(expectedBoot1);
        ArgumentNullException.ThrowIfNull(boot2);

        var snapshot = ReadOperationSnapshot(expectedBoot1, boot2);
        if (snapshot.Boot2Count != 1)
        {
            throw new RetirementExecutionException(
                "Cannot treat Boot 1 as already deleted: Boot 2 GPT is not uniquely present. " +
                $"boot2Matches={snapshot.Boot2Count}. No diskpart was started.");
        }

        if (snapshot.Boot1Count > 1)
        {
            throw new RetirementExecutionException(
                "Boot 1 GPT matched more than one volume. Refusing to continue. No diskpart was started.");
        }

        if (snapshot.Boot1Count == 1)
        {
            throw new RetirementExecutionException(
                "Boot 1 GPT is still present, so this is not an already-deleted resume. " +
                "No diskpart was started.");
        }

        var message =
            "Boot 1 GPT unique id is absent and Boot 2 GPT unique id is still unique. " +
            "Treating deletion as already done. diskpart was not started. " +
            $"destructiveDeletionOccurred=false";
        _log.Info("executor", message);
        return new RetirementExecutionResult
        {
            Kind = RetirementExecutionKind.AlreadyGone,
            DestructiveDeletionOccurred = false,
            Message = message
        };
    }

    public RetirementExecutionResult AcknowledgeAlreadyRecorded()
    {
        const string message =
            "State already has destructiveDeletionPerformed or status BOOT1_RETIRED+. " +
            "Skipping diskpart. destructiveDeletionOccurred=false";
        _log.Info("executor", message);
        return new RetirementExecutionResult
        {
            Kind = RetirementExecutionKind.AlreadyRecorded,
            DestructiveDeletionOccurred = false,
            Message = message
        };
    }

    /// <summary>Read-only presence of this operation's persisted Boot 1 / Boot 2 GPT ids.</summary>
    public PinnedVolumeSnapshot ReadOperationSnapshot(PartitionIdentity boot1Identity, PartitionIdentity boot2Identity)
    {
        if (!boot1Identity.TryGetGptId(out var boot1Gpt) || !boot2Identity.TryGetGptId(out var boot2Gpt))
        {
            throw new RetirementExecutionException("Boot 1 or Boot 2 persisted GPT identity is invalid.");
        }

        var live = _layout.Capture();
        var boot1 = live.WithGptId(boot1Gpt);
        var boot2 = live.WithGptId(boot2Gpt);
        return new PinnedVolumeSnapshot(boot1.Count, boot2.Count, null, null);
    }

    public string DescribeLiveDeleteReview(bool executeDeletionSwitch)
    {
        var text = new StringBuilder();
        text.AppendLine("======== LIVE-DELETE REVIEW (NO DISK COMMAND EXECUTED) ========");
        text.AppendLine("Code path: RecoveryRunner -> RetirementExecutor.RetireBoot1Async");
        text.AppendLine("           -> DestructiveRetirementEngine -> IDestructiveDiskCommand");
        text.AppendLine("           -> DiskpartDestructiveDiskCommand (production) / fake (tests)");
        text.AppendLine();
        text.AppendLine("Exact command (only after every guard passes):");
        text.AppendLine("  diskpart.exe /s <WinRE temp>\\cleanswitch-retire-boot1.txt");
        text.AppendLine("Script contents (numbers are re-resolved from the persisted GPT identity):");
        text.AppendLine("  select disk <operation Boot 1 live disk>");
        text.AppendLine("  select partition <operation Boot 1 live partition>");
        text.AppendLine("  delete partition override");
        text.AppendLine("Drive letters are never written into the script.");
        text.AppendLine();
        text.AppendLine("Operation target: schema-v2 Boot 1 identity must match the live GPT lookup exactly.");
        text.AppendLine("Operation survivor: schema-v2 Boot 2 identity must remain unique and unchanged.");
        text.AppendLine("All non-target live GPT identities are preserved; ESP/MSR/Recovery type guards also fail closed.");
        text.AppendLine("  Boot 1 BCD loader - Phase 2C compiled, BcdOperationsImplemented=false, not deleted");
        text.AppendLine("  Boot 2 is not moved or extended. Freed space stays unallocated.");
        text.AppendLine();
        text.AppendLine("Guards required to reach diskpart (every line must be true):");
        foreach (var line in DescribeGuards(executeDeletionSwitch, validationPassed: true))
        {
            text.AppendLine("  " + line);
        }

        text.AppendLine("  target matches the accepted schema-v2 operation identity and live GPT geometry");
        text.AppendLine("  target is not Boot 2, ESP, MSR, Recovery, or the running WinRE volume");
        text.AppendLine("  destructiveDeletionPerformed is false (otherwise diskpart is skipped)");
        text.AppendLine();
        text.AppendLine("If deletion fails (non-zero exit, or Boot 1 GPT still present, or Boot 2 missing):");
        text.AppendLine("  state becomes FAILED, no BOOT1_RETIRED, no BCD change, no restart.");
        text.AppendLine();
        text.AppendLine("If power is lost immediately after a successful delete:");
        text.AppendLine("  Boot 1 GPT is gone. Next --recovery-run sees 0 Boot 1 matches and 1 Boot 2 match,");
        text.AppendLine("  calls AcknowledgeAlreadyDeleted (no diskpart), writes BOOT1_RETIRED, then hands off.");
        text.AppendLine();
        text.AppendLine("If power is lost after BOOT1_RETIRED:");
        text.AppendLine("  destructiveDeletionPerformed is true. Next --recovery-run skips diskpart and continues");
        text.AppendLine("  PENDING/RECOVERY_STARTED/TARGET_VALIDATED/BOOT2_VALIDATED -> BOOT1_RETIRED -> VERIFIED.");
        text.AppendLine();
        text.AppendLine($"This build: DestructiveOperationsImplemented={DestructiveOperationsImplemented}, " +
                        $"EnableDestructiveRetirement={_options.EnableDestructiveRetirement}, " +
                        $"--execute-deletion={executeDeletionSwitch}.");
        text.AppendLine("Live delete stays disabled until those are flipped in a later, explicit change.");
        text.AppendLine("================================================================");
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Phase 2C BCD delete. Unreachable in this build: <see cref="BcdOperationsImplemented"/>
    /// is false, so this throws before bcdedit starts.
    /// </summary>
    public async Task<RetirementExecutionResult> DeleteBoot1BcdEntryAsync(
        RetirementState state,
        ValidationReport validation,
        bool explicitOptIn)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(validation);

        _log.Info(
            "executor",
            "DeleteBoot1BcdEntryAsync entered. " +
            $"bcdOperationsImplemented={BcdOperationsImplemented} explicitOptIn={explicitOptIn} " +
            $"boot1Bcd={state.Boot1BcdObjectId ?? "(missing)"}");

        if (!BcdOperationsImplemented)
        {
            throw new RetirementNotImplementedException(
                "Phase 2C BCD deletion is disabled (BcdOperationsImplemented is false). " +
                "No BCD object was deleted. bcdedit.exe was not started.");
        }

        return await _bcdEngine.ExecuteAsync(state, explicitOptIn, validation);
    }

    private bool AreLiveGuardsSatisfied(bool explicitOptIn, bool validationPassed) =>
        DestructiveOperationsImplemented &&
        explicitOptIn &&
        _options.EnableDestructiveRetirement &&
        validationPassed;

    private IReadOnlyList<string> DescribeGuards(bool explicitOptIn, bool validationPassed) =>
    [
        $"DestructiveOperationsImplemented = {DestructiveOperationsImplemented} (must be true)",
        $"EnableDestructiveRetirement     = {_options.EnableDestructiveRetirement} (must be true)",
        $"explicitOptIn                   = {explicitOptIn} (must be true; --execute-deletion)",
        $"validation.Passed               = {validationPassed} (must be true)"
    ];

    private static string BuildDiskpartScript(int disk, int partition) =>
        string.Join(
            Environment.NewLine,
            [
                $"select disk {disk}",
                $"select partition {partition}",
                "delete partition override"
            ]);

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

public sealed record PinnedVolumeSnapshot(
    int Boot1Count,
    int Boot2Count,
    LocatedVolume? Boot1,
    LocatedVolume? Boot2);
