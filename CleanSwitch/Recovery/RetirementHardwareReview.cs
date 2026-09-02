using System.Text;
using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

/// <summary>
/// Read-only Phase 2B + 2C hardware review. Never constructs or starts a
/// destructive disk or BCD process.
/// </summary>
public sealed class RetirementHardwareReview
{
    public const string MustRegenerateMessage = "Retirement state must be regenerated.";

    private readonly IGptLayoutSource _layout;
    private readonly IBcdStoreSource _bcd;
    private readonly IOperationLog _log;
    private readonly IRetirementIdentitySet _identities;

    public RetirementHardwareReview(
        IGptLayoutSource layout,
        IBcdStoreSource bcd,
        IOperationLog? log = null,
        IRetirementIdentitySet? identities = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _bcd = bcd ?? throw new ArgumentNullException(nameof(bcd));
        _log = log ?? NullOperationLog.Instance;
        _identities = identities ?? PinnedRetirementIdentitySet.Instance;
    }

    public RetirementHardwareReviewResult Run(RetirementState? state)
    {
        _log.Info("hardware-review", "Starting read-only production hardware review. No disk or BCD command will be started.");

        var schemaLabel = state is null ? "(missing)" : state.SchemaVersion.ToString();
        if (!TryAcceptState(state, out var stateError))
        {
            var failed = RetirementHardwareReviewResult.Rejected(schemaLabel, stateError);
            _log.Warn("hardware-review", failed.Describe());
            return failed;
        }

        var phase2B = ReviewPhase2B(state!);
        var phase2C = ReviewPhase2C(state!, phase2B.PartitionStillPresent);
        var result = RetirementHardwareReviewResult.FromPhases(schemaLabel, phase2B, phase2C);
        _log.Info("hardware-review", result.Describe());
        return result;
    }

    private static bool TryAcceptState(RetirementState? state, out string error)
    {
        if (state is null)
        {
            error = MustRegenerateMessage + " No retirement-state.json was found.";
            return false;
        }

        var missing = new List<string>();
        if (state.SchemaVersion < BcdRetirementStateRequirements.RequiredSchemaVersion)
        {
            missing.Add($"schemaVersion {state.SchemaVersion} (need {BcdRetirementStateRequirements.RequiredSchemaVersion})");
        }

        if (!BcdIdentifiers.TryParseObjectId(state.Boot1BcdObjectId, out var boot1) ||
            BcdIdentifiers.IsProtectedObject(boot1))
        {
            missing.Add("Boot1BcdObjectId (concrete BCD object GUID)");
        }

        if (!BcdIdentifiers.TryParseObjectId(state.Boot2BcdObjectId, out var boot2) ||
            BcdIdentifiers.IsProtectedObject(boot2))
        {
            missing.Add("Boot2BcdObjectId (concrete BCD object GUID)");
        }

        if (state.Boot1Identity is null)
        {
            missing.Add("Boot1Identity");
        }
        else
        {
            missing.AddRange(RetirementStateIdentityRequirements.MissingDestructiveFields(state.Boot1Identity, "Boot 1"));
        }

        if (state.Boot2Identity is null)
        {
            missing.Add("Boot2Identity");
        }
        else
        {
            missing.AddRange(RetirementStateIdentityRequirements.MissingDestructiveFields(state.Boot2Identity, "Boot 2"));
        }

        if (missing.Count == 0)
        {
            error = string.Empty;
            return true;
        }

        error = MustRegenerateMessage +
                " Legacy or incomplete state is refused." +
                Environment.NewLine +
                "Missing or invalid: " + string.Join("; ", missing);
        return false;
    }

    private Phase2BReview ReviewPhase2B(RetirementState state)
    {
        GptLayoutSnapshot live;
        try
        {
            live = _layout.Capture();
        }
        catch (Exception exception)
        {
            return Phase2BReview.Fail(
                "Live GPT enumeration failed. " + exception.Message,
                partitionStillPresent: false);
        }

        var resolved = DestructiveTargetResolver.Resolve(
            state.Boot1Identity!,
            state.Boot2Identity!,
            live,
            _identities);

        if (!resolved.Passed || resolved.Target is null)
        {
            return new Phase2BReview
            {
                Passed = false,
                PartitionStillPresent = live.WithGptId(ParseOrEmpty(state.Boot1Identity?.GptPartitionId)).Count == 1,
                Detail = resolved.Report.Describe(),
                HypotheticalScript = string.Empty,
                PinnedDisk = null,
                PinnedPartition = null
            };
        }

        var target = resolved.Target;
        var script =
            $"select disk {target.DiskNumber}{Environment.NewLine}" +
            $"select partition {target.PartitionNumber}{Environment.NewLine}" +
            "delete partition override";

        return new Phase2BReview
        {
            Passed = true,
            PartitionStillPresent = true,
            PinnedDisk = target.DiskNumber,
            PinnedPartition = target.PartitionNumber,
            HypotheticalScript = script,
            Detail = resolved.Report.Describe() +
                     Environment.NewLine +
                     $"Pinned disk/partition that WOULD be passed to diskpart: disk {target.DiskNumber} partition {target.PartitionNumber}" +
                     Environment.NewLine +
                     "Exact script that WOULD be used (not written, not executed):" +
                     Environment.NewLine + script
        };
    }

    private Phase2CReview ReviewPhase2C(RetirementState state, bool boot1PartitionStillPresent)
    {
        BcdSnapshot live;
        try
        {
            live = _bcd.CaptureAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            return Phase2CReview.Fail("Live BCD enumeration failed. " + exception.Message);
        }

        if (live.CurrentResolution != BcdAliasResolution.Resolved || live.CurrentObjectId is null)
        {
            return Phase2CReview.Fail(
                "{current} could not be resolved to a concrete GUID on the live BCD store. " +
                "Hardware review fail-closes; this is not an isolated file store.",
                live);
        }

        var boot1 = BcdIdentifiers.RequireConcreteObjectId(state.Boot1BcdObjectId, "Boot 1");
        var boot2 = BcdIdentifiers.RequireConcreteObjectId(state.Boot2BcdObjectId, "Boot 2");
        Guid? recovery = BcdIdentifiers.TryParseObjectId(state.RecoveryId, out var recoveryId)
            ? recoveryId
            : null;

        BcdResolveResult resolved;
        try
        {
            resolved = BcdRetirementTargetResolver.Resolve(
                boot1,
                boot2,
                recovery,
                live,
                state.Boot1Identity,
                state.Boot2Identity);
        }
        catch (RetirementExecutionException exception)
        {
            return Phase2CReview.Fail(exception.Message, live);
        }

        var boot1Entry = live.WithObjectId(boot1).FirstOrDefault();
        var boot2Entry = live.WithObjectId(boot2).FirstOrDefault();
        var extra = new List<string>();

        if (boot1PartitionStillPresent &&
            boot1Entry is not null &&
            !BcdIdentifiers.TryParseEmbeddedGuid(boot1Entry.OsDevice, out _) &&
            !BcdIdentifiers.TryParseEmbeddedGuid(boot1Entry.Device, out _))
        {
            extra.Add(
                "Boot 1 partition is still present. BCD device/osdevice has no parseable GPT GUID. " +
                "Drive letters were ignored. Identity remains the stored BCD object GUID.");
        }

        var passed = resolved.Passed;
        var detail = resolved.Report.Describe();
        if (extra.Count > 0)
        {
            detail += Environment.NewLine + string.Join(Environment.NewLine, extra);
        }

        return new Phase2CReview
        {
            Passed = passed,
            Detail = detail,
            Boot1BcdObjectId = BcdIdentifiers.Format(boot1),
            Boot2BcdObjectId = BcdIdentifiers.Format(boot2),
            CurrentObjectId = live.CurrentObjectId is Guid current
                ? BcdIdentifiers.Format(current)
                : null,
            DefaultObjectId = live.DefaultObjectId is Guid defaultId
                ? BcdIdentifiers.Format(defaultId)
                : null,
            Boot1Entry = boot1Entry,
            Boot2Entry = boot2Entry,
            HypotheticalCommand = passed
                ? $"bcdedit.exe /delete {BcdIdentifiers.Format(boot1)}"
                : string.Empty
        };
    }

    private static Guid ParseOrEmpty(string? raw) =>
        BcdIdentifiers.TryParseEmbeddedGuid(raw, out var id) ? id : Guid.Empty;
}

public sealed class Phase2BReview
{
    public required bool Passed { get; init; }

    public required bool PartitionStillPresent { get; init; }

    public required string Detail { get; init; }

    public string HypotheticalScript { get; init; } = string.Empty;

    public int? PinnedDisk { get; init; }

    public int? PinnedPartition { get; init; }

    public static Phase2BReview Fail(string detail, bool partitionStillPresent) =>
        new()
        {
            Passed = false,
            PartitionStillPresent = partitionStillPresent,
            Detail = detail
        };
}

public sealed class Phase2CReview
{
    public required bool Passed { get; init; }

    public required string Detail { get; init; }

    public string? Boot1BcdObjectId { get; init; }

    public string? Boot2BcdObjectId { get; init; }

    public string? CurrentObjectId { get; init; }

    public string? DefaultObjectId { get; init; }

    public BcdEntryIdentity? Boot1Entry { get; init; }

    public BcdEntryIdentity? Boot2Entry { get; init; }

    public string HypotheticalCommand { get; init; } = string.Empty;

    public static Phase2CReview Fail(string detail, BcdSnapshot? live = null) =>
        new()
        {
            Passed = false,
            Detail = detail,
            CurrentObjectId = live?.CurrentObjectId is Guid current ? BcdIdentifiers.Format(current) : null,
            DefaultObjectId = live?.DefaultObjectId is Guid defaultId ? BcdIdentifiers.Format(defaultId) : null
        };
}

public sealed class RetirementHardwareReviewResult
{
    public required string SchemaLabel { get; init; }

    public required bool Phase2BReviewPassed { get; init; }

    public required bool Phase2CReviewPassed { get; init; }

    public bool OverallPassed => Phase2BReviewPassed && Phase2CReviewPassed;

    public required string Detail { get; init; }

    public Phase2BReview? Phase2B { get; init; }

    public Phase2CReview? Phase2C { get; init; }

    public static RetirementHardwareReviewResult Rejected(string schemaLabel, string error) =>
        new()
        {
            SchemaLabel = schemaLabel,
            Phase2BReviewPassed = false,
            Phase2CReviewPassed = false,
            Detail = error,
            Phase2B = Phase2BReview.Fail(error, partitionStillPresent: false),
            Phase2C = Phase2CReview.Fail(error)
        };

    public static RetirementHardwareReviewResult FromPhases(
        string schemaLabel,
        Phase2BReview phase2B,
        Phase2CReview phase2C) =>
        new()
        {
            SchemaLabel = schemaLabel,
            Phase2BReviewPassed = phase2B.Passed,
            Phase2CReviewPassed = phase2C.Passed,
            Detail = string.Empty,
            Phase2B = phase2B,
            Phase2C = phase2C
        };

    public string Describe()
    {
        var text = new StringBuilder();
        text.AppendLine("======== PRODUCTION HARDWARE REVIEW ========");
        text.AppendLine($"State schema: {SchemaLabel}");
        text.AppendLine($"Phase 2B: {(Phase2BReviewPassed ? "PASS" : "FAIL")}");
        text.AppendLine($"Phase 2C: {(Phase2CReviewPassed ? "PASS" : "FAIL")}");
        text.AppendLine("Disk command executed: False");
        text.AppendLine("BCD command executed: False");
        text.AppendLine($"Overall: {(OverallPassed ? "PASS" : "FAIL")}");
        text.AppendLine("==========================================");
        text.AppendLine();
        text.AppendLine("This review never constructs or starts a destructive process.");
        text.AppendLine();

        if (!string.IsNullOrWhiteSpace(Detail) && Phase2B is null)
        {
            text.AppendLine(Detail);
            return text.ToString().TrimEnd();
        }

        text.AppendLine("----- Phase 2B (GPT / disk identity) -----");
        text.AppendLine(Phase2B?.Detail ?? Detail);
        if (Phase2B is { Passed: true, HypotheticalScript: var script } &&
            !string.IsNullOrWhiteSpace(script))
        {
            text.AppendLine();
            text.AppendLine("Hypothetical diskpart script (NOT executed):");
            text.AppendLine(script);
        }

        text.AppendLine();
        text.AppendLine("----- Phase 2C (BCD identity) -----");
        if (Phase2C is not null)
        {
            text.AppendLine($"Boot1BcdObjectId     : {Phase2C.Boot1BcdObjectId ?? "(missing)"}");
            text.AppendLine($"Boot2BcdObjectId     : {Phase2C.Boot2BcdObjectId ?? "(missing)"}");
            text.AppendLine($"resolved current GUID: {Phase2C.CurrentObjectId ?? "(unresolved)"}");
            text.AppendLine($"resolved default GUID: {Phase2C.DefaultObjectId ?? "(unresolved)"}");
            text.AppendLine($"Boot 1 BCD device    : {ValueOrNone(Phase2C.Boot1Entry?.Device)}");
            text.AppendLine($"Boot 1 BCD osdevice  : {ValueOrNone(Phase2C.Boot1Entry?.OsDevice)}");
            text.AppendLine($"Boot 1 BCD path      : {ValueOrNone(Phase2C.Boot1Entry?.Path)}");
            text.AppendLine($"Boot 2 BCD device    : {ValueOrNone(Phase2C.Boot2Entry?.Device)}");
            text.AppendLine($"Boot 2 BCD osdevice  : {ValueOrNone(Phase2C.Boot2Entry?.OsDevice)}");
            text.AppendLine($"Boot 2 BCD path      : {ValueOrNone(Phase2C.Boot2Entry?.Path)}");
            text.AppendLine();
            text.AppendLine(Phase2C.Detail);
            if (Phase2C.Passed && !string.IsNullOrWhiteSpace(Phase2C.HypotheticalCommand))
            {
                text.AppendLine();
                text.AppendLine("Exact command that WOULD be executed later (NOT constructed, NOT started):");
                text.AppendLine("  " + Phase2C.HypotheticalCommand);
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string ValueOrNone(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(none)" : value;
}
