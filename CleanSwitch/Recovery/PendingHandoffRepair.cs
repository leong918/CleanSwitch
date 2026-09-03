using System.Text;
using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

/// <summary>
/// Narrow repair for an existing schema-v2 PENDING handoff whose only defect is that
/// Boot 1 is still the persistent default. It never writes retirement state, schedules
/// a boot, restarts, deletes a BCD object, or starts a disk command.
/// </summary>
public sealed class PendingHandoffRepair
{
    private readonly IGptLayoutSource _layout;
    private readonly IBcdStoreSource _bcd;
    private readonly IBootManager _bootManager;
    private readonly IOperationLog _log;

    public PendingHandoffRepair(
        IGptLayoutSource layout,
        IBcdStoreSource bcd,
        IBootManager bootManager,
        IOperationLog? log = null)
    {
        _layout = layout;
        _bcd = bcd;
        _bootManager = bootManager;
        _log = log ?? NullOperationLog.Instance;
    }

    public async Task<PendingHandoffRepairResult> ReviewAsync(RetirementState? state)
    {
        var report = new ValidationReport("PENDING handoff default repair");
        if (state is null)
        {
            report.Fail("state-present", "No retirement state was found.");
            return PendingHandoffRepairResult.Failed(report);
        }

        report.Add("schema-v2", state.SchemaVersion == 2, $"schemaVersion={state.SchemaVersion}; required=2.");
        report.Add(
            "status-pending",
            state.Status == RetirementStatus.Pending,
            $"status={RetirementStatusNames.ToWire(state.Status)}; required=PENDING.");
        report.Add(
            "disk-delete-not-performed",
            !state.DestructiveDeletionPerformed,
            $"destructiveDeletionPerformed={state.DestructiveDeletionPerformed}.");
        report.Add(
            "bcd-delete-not-performed",
            !state.BcdDeletionPerformed,
            $"bcdDeletionPerformed={state.BcdDeletionPerformed}.");

        var missing = new List<string>();
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

        report.Add(
            "persisted-identities-complete",
            missing.Count == 0,
            missing.Count == 0 ? "Boot 1 and Boot 2 persisted identities are complete." : string.Join("; ", missing));

        var boot1BcdOk = BcdIdentifiers.TryParseObjectId(state.Boot1BcdObjectId, out var boot1Bcd) &&
                         !BcdIdentifiers.IsProtectedObject(boot1Bcd);
        var boot2BcdOk = BcdIdentifiers.TryParseObjectId(state.Boot2BcdObjectId, out var boot2Bcd) &&
                         !BcdIdentifiers.IsProtectedObject(boot2Bcd);
        report.Add("boot1-bcd-concrete", boot1BcdOk, $"Boot 1 BCD={state.Boot1BcdObjectId ?? "(missing)"}.");
        report.Add("boot2-bcd-concrete", boot2BcdOk, $"Boot 2 BCD={state.Boot2BcdObjectId ?? "(missing)"}.");
        report.Add(
            "boot-loaders-distinct",
            boot1BcdOk && boot2BcdOk && boot1Bcd != boot2Bcd,
            "Boot 1 and Boot 2 must be distinct concrete BCD objects.");

        if (!report.Passed || state.Boot1Identity is null || state.Boot2Identity is null)
        {
            return PendingHandoffRepairResult.Failed(report);
        }

        GptLayoutSnapshot liveLayout;
        BcdSnapshot liveBcd;
        try
        {
            liveLayout = _layout.Capture();
            liveBcd = await _bcd.CaptureAsync();
        }
        catch (Exception exception)
        {
            report.Fail("live-enumeration", "Live GPT or BCD enumeration failed: " + exception.Message);
            return PendingHandoffRepairResult.Failed(report);
        }

        var diskResolve = DestructiveTargetResolver.Resolve(
            state.Boot1Identity,
            state.Boot2Identity,
            liveLayout);
        foreach (var check in diskResolve.Report.Checks)
        {
            report.Add("identity-" + check.Name, check.Passed, check.Detail);
        }

        report.Add(
            "bootmgr-present",
            liveBcd.BootManagerPresent,
            liveBcd.BootManagerPresent ? "{bootmgr} is present." : "{bootmgr} is missing.");
        report.Add(
            "default-resolved",
            liveBcd.DefaultResolution == BcdAliasResolution.Resolved && liveBcd.DefaultObjectId is not null,
            liveBcd.DefaultObjectId is Guid defaultId
                ? $"Current {{default}}={BcdIdentifiers.Format(defaultId)}."
                : "{default} did not resolve to a concrete GUID.");
        report.Add(
            "current-is-boot2-survivor",
            liveBcd.CurrentResolution == BcdAliasResolution.Resolved && liveBcd.CurrentObjectId == boot2Bcd,
            liveBcd.CurrentObjectId is Guid currentId
                ? $"Current loader={BcdIdentifiers.Format(currentId)}."
                : "{current} did not resolve to a concrete GUID.");

        AddLoaderGuard(report, "boot1", boot1Bcd, liveBcd);
        AddLoaderGuard(report, "boot2", boot2Bcd, liveBcd);

        var defaultIsBoot1 = liveBcd.DefaultObjectId == boot1Bcd;
        var defaultIsBoot2 = liveBcd.DefaultObjectId == boot2Bcd;
        report.Add(
            "default-is-known-operation-loader",
            defaultIsBoot1 || defaultIsBoot2,
            defaultIsBoot1
                ? "Boot 1 is currently default; the one allowed repair is required."
                : defaultIsBoot2
                    ? "Boot 2 is already default; repair is a safe no-op."
                    : "{default} is neither the persisted Boot 1 nor Boot 2 loader.");

        var command = $"bcdedit.exe /default {BcdIdentifiers.Format(boot2Bcd)}";
        var result = new PendingHandoffRepairResult(
            report.Passed,
            report.Passed && defaultIsBoot2,
            false,
            liveBcd.DefaultObjectId,
            boot2Bcd,
            command,
            report);
        _log.Info("pending-handoff-repair", result.Describe(reviewOnly: true));
        return result;
    }

    public async Task<PendingHandoffRepairResult> ExecuteAsync(RetirementState? state)
    {
        var before = await ReviewAsync(state);
        if (!before.Passed || before.SafeNoOp)
        {
            return before;
        }

        if (!await _bootManager.SetDefaultBootAsync(BcdIdentifiers.Format(before.TargetDefault)))
        {
            throw new RetirementExecutionException(
                "The boot manager did not confirm the one permitted /default mutation. No other BCD command was attempted.");
        }

        var after = await ReviewAsync(state);
        if (!after.Passed || !after.SafeNoOp)
        {
            throw new RetirementExecutionException(
                "The persistent default mutation did not verify as Boot 2. No other BCD command was attempted." +
                Environment.NewLine + after.Describe(reviewOnly: false));
        }

        return after with { MutationPerformed = true };
    }

    private static void AddLoaderGuard(ValidationReport report, string prefix, Guid id, BcdSnapshot live)
    {
        var matches = live.WithObjectId(id);
        report.Add(
            $"{prefix}-loader-unique",
            matches.Count == 1,
            matches.Count == 1 ? $"{prefix} BCD object is unique." : $"{prefix} matched {matches.Count} BCD objects.");
        if (matches.Count == 1)
        {
            report.Add(
                $"{prefix}-is-windows-loader",
                matches[0].Kind == BcdObjectKind.WindowsLoader,
                $"{prefix} kind={matches[0].Kind}; required=WindowsLoader.");
        }
    }
}

public sealed record PendingHandoffRepairResult(
    bool Passed,
    bool SafeNoOp,
    bool MutationPerformed,
    Guid? CurrentDefault,
    Guid TargetDefault,
    string Command,
    ValidationReport Report)
{
    public static PendingHandoffRepairResult Failed(ValidationReport report) =>
        new(false, false, false, null, Guid.Empty, string.Empty, report);

    public string Describe(bool reviewOnly)
    {
        var text = new StringBuilder();
        text.AppendLine("======== PENDING HANDOFF DEFAULT REPAIR ========");
        text.AppendLine($"Mode: {(reviewOnly ? "REVIEW ONLY (read-only)" : "REPAIR")}");
        text.AppendLine($"Current default: {(CurrentDefault is Guid current ? BcdIdentifiers.Format(current) : "(unresolved)")}");
        text.AppendLine($"Target default: {(TargetDefault == Guid.Empty ? "(unresolved)" : BcdIdentifiers.Format(TargetDefault))}");
        text.AppendLine($"Only permitted command: {(string.IsNullOrWhiteSpace(Command) ? "(none; validation failed)" : Command)}");
        text.AppendLine($"Validation: {(Passed ? "PASS" : "FAIL")}");
        text.AppendLine($"Safe no-op: {SafeNoOp}");
        text.AppendLine($"BCD mutation performed: {MutationPerformed}");
        text.AppendLine("State modification permitted: False");
        text.AppendLine("Boot sequence / reboot / diskpart / BCD delete permitted: False");
        text.Append(Report.Describe());
        return text.ToString();
    }
}
