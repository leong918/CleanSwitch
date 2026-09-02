using System.Text;
using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

/// <summary>
/// Read-only preview of resuming production execution from <see cref="RetirementStatus.Boot1Retired"/>.
/// Never constructs diskpart or bcdedit /delete, never persists state, never reboots.
/// </summary>
public sealed class RetirementResumePreview
{
    private readonly DiskValidator _diskValidator;
    private readonly IBcdStoreSource _bcdStore;
    private readonly IOperationLog _log;

    public RetirementResumePreview(
        DiskValidator diskValidator,
        IBcdStoreSource bcdStore,
        IOperationLog? log = null)
    {
        _diskValidator = diskValidator ?? throw new ArgumentNullException(nameof(diskValidator));
        _bcdStore = bcdStore ?? throw new ArgumentNullException(nameof(bcdStore));
        _log = log ?? NullOperationLog.Instance;
    }

    public async Task<RetirementResumePreviewResult> RunAsync(RetirementState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var result = new RetirementResumePreviewResult
        {
            LoadedStatus = RetirementStatusNames.ToWire(state.Status),
            DestructiveDeletionPerformed = state.DestructiveDeletionPerformed,
            BcdDeletionPerformed = state.BcdDeletionPerformed
        };

        if (state.Status != RetirementStatus.Boot1Retired)
        {
            return result.Fail(
                $"Resume preview requires status BOOT1_RETIRED; loaded {result.LoadedStatus}.");
        }

        if (!state.DestructiveDeletionPerformed)
        {
            return result.Fail(
                "Resume preview requires destructiveDeletionPerformed=true as evidence that Phase 2B already settled.");
        }

        result.Phase2BAction = "SKIP";
        result.Phase2BReason =
            "BOOT1_RETIRED with destructiveDeletionPerformed=true. diskpart will not run again.";

        if (state.Boot2Identity is null || !state.Boot2Identity.HasStableIdentifiers)
        {
            return result.Fail("Boot 2 identity is missing or incomplete; cannot preview resume.");
        }

        var observedBoot2 = _diskValidator.TryObserveByGptId(
            state.Boot2Identity.GptPartitionId,
            "Resume preview observation of Boot 2",
            out var boot2Error);
        if (observedBoot2 is null)
        {
            return result.Fail(boot2Error ?? "Boot 2 GPT GUID was not found.");
        }

        result.Boot2GptObserved = true;

        if (state.Boot1Identity?.GptPartitionId is not null)
        {
            var observedBoot1 = _diskValidator.TryObserveByGptId(
                state.Boot1Identity.GptPartitionId,
                "Resume preview observation of Boot 1",
                out _);
            result.Boot1GptAbsent = observedBoot1 is null;
            result.Boot1GptAcceptanceReason = observedBoot1 is null
                ? "Live Boot 1 GPT is absent and persisted BOOT1_RETIRED + destructiveDeletionPerformed=true accept this."
                : "Live Boot 1 GPT is still present; resume preview refuses because Phase 2B evidence conflicts with live layout.";
            if (observedBoot1 is not null)
            {
                return result.Fail(result.Boot1GptAcceptanceReason);
            }
        }

        BcdSnapshot afterBcd;
        try
        {
            afterBcd = await _bcdStore.CaptureAsync();
        }
        catch (Exception exception)
        {
            return result.Fail("Live BCD enumeration failed: " + exception.Message);
        }

        var boot1 = BcdIdentifiers.RequireConcreteObjectId(state.Boot1BcdObjectId, "Boot 1");
        var boot2 = BcdIdentifiers.RequireConcreteObjectId(state.Boot2BcdObjectId, "Boot 2");
        var exclusive = BcdBoot1DependencyGraph.ResolveExclusiveIds(state, afterBcd, before: null);
        result.Boot1ExclusiveBcdObjectIds = exclusive
            .Select(BcdIdentifiers.Format)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var survivorReport = BcdSurvivorReconciliation.VerifyAfterBoot1PartitionDelete(
            state,
            afterBcd,
            before: null);
        result.SurvivorReconciliationDetail = survivorReport.Describe();
        result.SurvivorReconciliationPassed = survivorReport.Passed;

        result.Boot1LoaderAbsent = afterBcd.WithObjectId(boot1).Count == 0;
        result.Boot1LoaderId = BcdIdentifiers.Format(boot1);

        if (result.Boot1LoaderAbsent)
        {
            result.Phase2CDeleteAction = "NO-OP / already absent";
            result.Phase2CDeleteReason =
                $"Boot 1 loader {result.Boot1LoaderId} is already absent. No bcdedit /delete would run.";
        }
        else
        {
            result.Phase2CDeleteAction = "WOULD DELETE";
            result.Phase2CDeleteReason =
                $"Boot 1 loader {result.Boot1LoaderId} is still present. Authorized execution would run bcdedit /delete.";
        }

        result.DefaultObjectId = afterBcd.DefaultObjectId is Guid defaultId
            ? BcdIdentifiers.Format(defaultId)
            : null;
        result.DefaultPointsToBoot2 = afterBcd.DefaultObjectId == boot2;

        if (!survivorReport.Passed)
        {
            return result.Fail("BCD survivor reconciliation failed.");
        }

        if (!result.Boot1LoaderAbsent && result.Phase2CDeleteAction.StartsWith("WOULD", StringComparison.Ordinal))
        {
            return result.Fail(result.Phase2CDeleteReason);
        }

        result.HypotheticalTransitions =
        [
            new HypotheticalTransition(
                "BOOT1_RETIRED",
                "BOOT1_RETIRED",
                "Survivor reconciliation passes. Status unchanged before handoff."),
            new HypotheticalTransition(
                "BOOT1_RETIRED",
                "BCD_UPDATED",
                $"Set one-time boot sequence to Boot 2 ({state.Boot2Id}). " +
                (result.Boot1LoaderAbsent
                    ? "bcdDeletionPerformed would remain false (loader already absent)."
                    : "bcdDeletionPerformed would become true after delete.")),
            new HypotheticalTransition(
                "BCD_UPDATED",
                "VERIFIED",
                $"Re-read Boot 2 entry {state.Boot2Id} after BCD update."),
            new HypotheticalTransition(
                "VERIFIED",
                "(restart scheduled)",
                "Schedule restart to hand off to Boot 2. Not performed in preview.")
        ];

        result.Readiness = "PASS";
        _log.Info("resume-preview", result.Describe());
        return result;
    }
}

public sealed class RetirementResumePreviewResult
{
    public string LoadedStatus { get; init; } = string.Empty;

    public bool DestructiveDeletionPerformed { get; init; }

    public bool BcdDeletionPerformed { get; init; }

    public string Phase2BAction { get; set; } = "SKIP";

    public string Phase2BReason { get; set; } = string.Empty;

    public bool Boot1GptAbsent { get; set; }

    public bool Boot2GptObserved { get; set; }

    public string Boot1GptAcceptanceReason { get; set; } = string.Empty;

    public bool SurvivorReconciliationPassed { get; set; }

    public string SurvivorReconciliationDetail { get; set; } = string.Empty;

    public IReadOnlyList<string> Boot1ExclusiveBcdObjectIds { get; set; } = [];

    public bool Boot1LoaderAbsent { get; set; }

    public string Boot1LoaderId { get; set; } = string.Empty;

    public string Phase2CDeleteAction { get; set; } = string.Empty;

    public string Phase2CDeleteReason { get; set; } = string.Empty;

    public string? DefaultObjectId { get; set; }

    public bool DefaultPointsToBoot2 { get; set; }

    public IReadOnlyList<HypotheticalTransition> HypotheticalTransitions { get; set; } = [];

    public string Readiness { get; set; } = "FAIL";

    public string? FailureReason { get; set; }

    public RetirementResumePreviewResult Fail(string reason)
    {
        Readiness = "FAIL";
        FailureReason = reason;
        return this;
    }

    public string Describe()
    {
        var text = new StringBuilder();
        text.AppendLine("======== RESUME PREVIEW (read-only) ========");
        text.AppendLine($"Loaded status: {LoadedStatus}");
        text.AppendLine($"destructiveDeletionPerformed: {DestructiveDeletionPerformed}");
        text.AppendLine($"bcdDeletionPerformed: {BcdDeletionPerformed}");
        text.AppendLine("Disk command executed: False");
        text.AppendLine("BCD delete command executed: False");
        text.AppendLine("State modified: False");
        text.AppendLine($"Phase 2B action: {Phase2BAction}");
        if (!string.IsNullOrWhiteSpace(Phase2BReason))
        {
            text.AppendLine($"Phase 2B detail            : {Phase2BReason}");
        }

        text.AppendLine($"Boot 1 GPT absent (live)   : {Boot1GptAbsent}");
        if (!string.IsNullOrWhiteSpace(Boot1GptAcceptanceReason))
        {
            text.AppendLine($"Boot 1 GPT acceptance      : {Boot1GptAcceptanceReason}");
        }

        text.AppendLine($"Boot 2 GPT observed (live) : {Boot2GptObserved}");
        text.AppendLine();
        text.AppendLine("----- Boot-1-exclusive BCD object GUIDs -----");
        if (Boot1ExclusiveBcdObjectIds.Count == 0)
        {
            text.AppendLine("(none resolved)");
        }
        else
        {
            foreach (var id in Boot1ExclusiveBcdObjectIds)
            {
                text.AppendLine("  " + id);
            }
        }

        text.AppendLine();
        text.AppendLine("----- BCD survivor reconciliation -----");
        text.AppendLine(SurvivorReconciliationDetail);
        text.AppendLine();
        text.AppendLine($"Boot 1 loader              : {Boot1LoaderId}");
        text.AppendLine($"Boot 1 loader absent       : {Boot1LoaderAbsent}");
        text.AppendLine($"Phase 2C delete action: {Phase2CDeleteAction}");
        if (!string.IsNullOrWhiteSpace(Phase2CDeleteReason))
        {
            text.AppendLine($"Phase 2C delete detail     : {Phase2CDeleteReason}");
        }

        text.AppendLine($"Default object GUID        : {DefaultObjectId ?? "(unresolved)"}");
        text.AppendLine($"Default points to Boot 2   : {DefaultPointsToBoot2}");
        text.AppendLine();

        if (HypotheticalTransitions.Count > 0)
        {
            text.AppendLine("----- Hypothetical transitions if execution were authorized -----");
            foreach (var transition in HypotheticalTransitions)
            {
                text.AppendLine($"  {transition.From} -> {transition.To}: {transition.Reason}");
            }

            text.AppendLine();
        }

        text.AppendLine($"Resume readiness: {Readiness}");
        if (!string.IsNullOrWhiteSpace(FailureReason))
        {
            text.AppendLine($"Failure reason             : {FailureReason}");
        }

        text.AppendLine("============================================");
        text.AppendLine("This preview never runs diskpart, bcdedit /delete, state persistence, or reboot.");
        return text.ToString().TrimEnd();
    }
}

public sealed record HypotheticalTransition(string From, string To, string Reason);
