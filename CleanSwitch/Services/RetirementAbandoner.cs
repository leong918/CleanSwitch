using System.Text.Json;
using CleanSwitch.Models;

namespace CleanSwitch.Services;

/// <summary>
/// Operator-visible, non-destructive abandon of a PENDING retirement operation.
/// Archives the exact live state file, verifies the archive, then marks the live
/// operation ABORTED through <see cref="IRetirementCoordinator.MarkAborted"/>.
/// Never starts diskpart, bcdedit, partition edits, BCD edits, or a reboot.
/// Never starts a new retirement operation.
/// </summary>
public sealed class RetirementAbandoner
{
    public const string DefaultSupersedeReason =
        "Operator superseded stale schema-v1 PENDING operation so a fresh schema-v2 retirement state can be captured.";

    private readonly IRetirementCoordinator _coordinator;
    private readonly IRetirementStateArchiver _archiver;
    private readonly IOperationLog _log;

    public RetirementAbandoner(
        IRetirementCoordinator coordinator,
        IOperationLog? log = null,
        IRetirementStateArchiver? archiver = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _log = log ?? NullOperationLog.Instance;
        _archiver = archiver ?? new FileRetirementStateArchiver();
    }

    public RetirementAbandonResult Execute(Action<string>? report = null, string? reason = null)
    {
        void Line(string text)
        {
            _log.Info("abandon", text);
            report?.Invoke(text);
        }

        var reasonText = string.IsNullOrWhiteSpace(reason) ? DefaultSupersedeReason : reason.Trim();
        var statePath = _coordinator.StateFilePath;

        Line("Loading retirement state (read-only so far).");
        Line($"State path     : {statePath}");

        var state = _coordinator.TryLoad();
        if (state is null)
        {
            throw new RetirementStateException(
                $"No retirement state file was found at '{statePath}'. " +
                "Nothing was archived and the live location was not changed.");
        }

        Line($"Operation      : {state.Operation}");
        Line($"Schema version : {state.SchemaVersion}");
        Line($"Status         : {RetirementStatusNames.ToWire(state.Status)}");
        Line($"CreatedAtUtc   : {state.CreatedAtUtc:o}");
        Line($"UpdatedAtUtc   : {state.UpdatedAtUtc:o}");
        Line($"Phase          : {state.Phase}");

        if (state.Status == RetirementStatus.Complete)
        {
            throw new RetirementStateException(
                "Refusing to abandon a COMPLETE retirement operation. " +
                "The live state file was not archived or changed.");
        }

        if (state.Status == RetirementStatus.Aborted)
        {
            throw new RetirementStateException(
                "Refusing to abandon a retirement operation that is already ABORTED. " +
                "The live state file was not archived or changed.");
        }

        if (state.Status != RetirementStatus.Pending)
        {
            throw new RetirementStateException(
                $"Refusing to abandon a retirement operation with status " +
                $"{RetirementStatusNames.ToWire(state.Status)}. " +
                "Only PENDING operations can be abandoned with this command. " +
                "The live state file was not archived or changed.");
        }

        Line("Archiving the exact live state file before any write...");
        var archive = _archiver.ArchiveVerifiedCopy(statePath, state);
        Line($"Archive path   : {archive.Path}");
        Line($"Archive SHA-256: {archive.Sha256Hex}");
        Line("Archive verified against the live file. Marking the live operation ABORTED via the coordinator.");

        // MarkAborted mutates the loaded object. Keep the archived bytes as the original.
        var original = ReadSnapshot(archive.Path);
        var aborted = _coordinator.MarkAborted(state, reasonText);
        var reloaded = _coordinator.TryLoad()
            ?? throw new RetirementStateException(
                "The live retirement state could not be reloaded after MarkAborted. " +
                $"Inspect '{statePath}' and the archive at '{archive.Path}'.");

        if (reloaded.Status != RetirementStatus.Aborted || !reloaded.IsTerminal)
        {
            throw new RetirementStateException(
                $"Reload after abandon did not observe terminal ABORTED " +
                $"(status={RetirementStatusNames.ToWire(reloaded.Status)}). " +
                $"Inspect '{statePath}' and the archive at '{archive.Path}'.");
        }

        var abortedAtUtc = reloaded.Transitions.Count > 0
            ? reloaded.Transitions[^1].AtUtc
            : reloaded.UpdatedAtUtc;

        var beginAllowed = AllowsNewRetirement(reloaded);
        Line($"Live status    : {RetirementStatusNames.ToWire(reloaded.Status)}");
        Line($"CreatedAtUtc   : {reloaded.CreatedAtUtc:o} (preserved)");
        Line($"AbortedAtUtc   : {abortedAtUtc:o}");
        Line($"UpdatedAtUtc   : {reloaded.UpdatedAtUtc:o}");
        Line($"Reason         : {reasonText}");
        Line(beginAllowed
            ? "BeginRetirement is now allowed to create a new operation."
            : "BeginRetirement is still blocked; inspect the live state file.");
        Line("A new retirement operation was not started.");

        return new RetirementAbandonResult
        {
            StateFilePath = statePath,
            ArchivePath = archive.Path,
            ArchiveSha256Hex = archive.Sha256Hex,
            Original = original,
            Reloaded = reloaded,
            Aborted = aborted,
            Reason = reasonText,
            OriginalCreatedAtUtc = state.CreatedAtUtc,
            AbortedAtUtc = abortedAtUtc,
            BeginRetirementAllowed = beginAllowed
        };
    }

    /// <summary>
    /// Same gate the coordinator uses before creating a new operation.
    /// Does not create or modify any state.
    /// </summary>
    public static bool AllowsNewRetirement(RetirementState? existing) =>
        existing is null || existing.IsTerminal || existing.Status == RetirementStatus.Failed;

    private static RetirementState ReadSnapshot(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RetirementState>(json, SnapshotOptions)
            ?? throw new RetirementStateException(
                $"The verified archive '{path}' could not be re-read as retirement state.");
    }

    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}

public sealed class RetirementAbandonResult
{
    public required string StateFilePath { get; init; }

    public required string ArchivePath { get; init; }

    public required string ArchiveSha256Hex { get; init; }

    public required RetirementState Original { get; init; }

    public required RetirementState Reloaded { get; init; }

    public required RetirementState Aborted { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset OriginalCreatedAtUtc { get; init; }

    public required DateTimeOffset AbortedAtUtc { get; init; }

    public bool BeginRetirementAllowed { get; init; }
}
