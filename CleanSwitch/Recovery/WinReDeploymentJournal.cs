using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

public enum WinReDeploymentStage
{
    D0Prepared,
    D1Snapshotted,
    D2BackupVerified,
    FirstMutationAuthorized,
    D3DisableIntent,
    D3DisabledVerified,
    D4RemoveOriginalIntent,
    D4OriginalRemoved,
    D4CopyIncomingIntent,
    D4IncomingVerified,
    D4FinalRenameIntent,
    D4FinalInstalled,
    D5SetReImageIntent,
    D5SetReImageVerified,
    D5EnableIntent,
    D5EnabledVerified,
    D5ReviewVerified,
    AwaitingSmoke,
    SmokeVerified,
    CommitIntent,
    Committed,
    RollbackIntent,
    RolledBack,
    RecoveryRequired,
    // Appended to preserve the numeric values already persisted for all legacy stages.
    DeploymentVerified
}

public enum WinReJournalRecordKind
{
    Intent,
    Completion,
    Observation,
    Failure
}

public sealed record WinReDeploymentPlan
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string TransactionId { get; init; }
    public required string PreparedWimPath { get; init; }
    public required string PreparedWimSha256 { get; init; }
    public required string PreparedBundlePath { get; init; }
    public required string PreparedBundleSha256 { get; init; }
    public required string LiveWimPath { get; init; }
    public required string ExpectedOriginalWimSha256 { get; init; }
    public required string ObservedOriginalWimSha256 { get; init; }
    /// <summary>Compatibility copy of the observed hash; must equal ObservedOriginalWimSha256.</summary>
    public required string OriginalWimSha256 { get; init; }
    public required string BackupWimPath { get; init; }
    public required string IncomingWimPath { get; init; }
    public required string RecoveryDirectory { get; init; }
    public required string ExpectedRecoveryGuid { get; init; }
    public required string Boot2Guid { get; init; }
    public required string RetirementStateSha256 { get; init; }
    public required string ProtectedBcdFingerprint { get; init; }
    public required string GptLayoutFingerprint { get; init; }
    public required string RecoveryPartitionGptId { get; init; }
    public required string RecoveryDataVolumeGptId { get; init; }
    public required string ProductVersion { get; init; }
    public required string ExecutableSha256 { get; init; }
    public required string ConfigurationSha256 { get; init; }
}

public sealed record WinReDeploymentJournalRecord
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string TransactionId { get; init; }
    public required long Sequence { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public required WinReDeploymentStage Stage { get; init; }
    public required WinReJournalRecordKind Kind { get; init; }
    public required string Detail { get; init; }
    public required string PreviousRecordSha256 { get; init; }
    public required string RecordSha256 { get; init; }
    public WinReDeploymentPlan? Plan { get; init; }
}

public sealed record WinReDeploymentJournalSnapshot(
    string Path,
    IReadOnlyList<WinReDeploymentJournalRecord> Records)
{
    public WinReDeploymentJournalRecord Last => Records[^1];
    public WinReDeploymentPlan Plan => Records[0].Plan
        ?? throw new InvalidDataException("The deployment journal does not contain its immutable plan.");
    public bool IsTerminal => Last.Stage is WinReDeploymentStage.Committed or WinReDeploymentStage.RolledBack;
    public bool RequiresRecovery => !IsTerminal;
}

public interface IWinReDeploymentJournal
{
    string Path { get; }
    WinReDeploymentJournalSnapshot Create(WinReDeploymentPlan plan);
    WinReDeploymentJournalSnapshot Append(
        WinReDeploymentStage stage,
        WinReJournalRecordKind kind,
        string detail);
    WinReDeploymentJournalSnapshot Load();
}

/// <summary>
/// Append-only, SHA-256 chained journal. Every append is flushed through the filesystem to
/// stable storage before control returns to the deployment engine.
/// </summary>
public sealed class FileWinReDeploymentJournal : IWinReDeploymentJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FileWinReDeploymentJournal(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public WinReDeploymentJournalSnapshot Create(WinReDeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        WinReDeploymentHashPolicy.RequireSealedPlan(plan);
        if (File.Exists(Path))
        {
            throw new InvalidOperationException($"Deployment journal already exists: '{Path}'.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var first = BuildRecord(
            plan.TransactionId, 1, WinReDeploymentStage.D0Prepared,
            WinReJournalRecordKind.Completion, "Prepared deployment plan recorded.", string.Empty, plan);
        AppendDurable(first, createNew: true);
        return Load();
    }

    public WinReDeploymentJournalSnapshot Append(
        WinReDeploymentStage stage,
        WinReJournalRecordKind kind,
        string detail)
    {
        var current = Load();
        if (current.IsTerminal)
        {
            throw new InvalidOperationException("A terminal deployment journal cannot be appended.");
        }

        var next = BuildRecord(
            current.Last.TransactionId,
            checked(current.Last.Sequence + 1),
            stage,
            kind,
            detail,
            current.Last.RecordSha256,
            null);
        AppendDurable(next, createNew: false);
        return Load();
    }

    public WinReDeploymentJournalSnapshot Load()
    {
        if (!File.Exists(Path))
        {
            throw new FileNotFoundException("Deployment journal was not found.", Path);
        }

        var lines = File.ReadAllLines(Path, Encoding.UTF8);
        if (lines.Length == 0 || lines.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("Deployment journal is empty or contains a truncated record.");
        }

        var records = new List<WinReDeploymentJournalRecord>(lines.Length);
        string previous = string.Empty;
        for (var index = 0; index < lines.Length; index++)
        {
            WinReDeploymentJournalRecord record;
            try
            {
                record = JsonSerializer.Deserialize<WinReDeploymentJournalRecord>(lines[index], JsonOptions)
                    ?? throw new InvalidDataException("Deployment journal record is null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Deployment journal record {index + 1} is malformed.", exception);
            }

            if (record.SchemaVersion != WinReDeploymentJournalRecord.CurrentSchemaVersion ||
                record.Sequence != index + 1 ||
                !string.Equals(record.PreviousRecordSha256, previous, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(record.RecordSha256, ComputeRecordHash(record), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Deployment journal integrity validation failed at record {index + 1}.");
            }

            if (index == 0 && (record.Plan is null ||
                               record.Plan.SchemaVersion != WinReDeploymentPlan.CurrentSchemaVersion ||
                               !string.Equals(record.TransactionId, record.Plan.TransactionId, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Deployment journal first record has no valid immutable plan.");
            }

            if (index > 0 && (record.Plan is not null ||
                              !string.Equals(record.TransactionId, records[0].TransactionId, StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"Deployment journal transaction identity changed at record {index + 1}.");
            }

            records.Add(record);
            previous = record.RecordSha256;
        }

        var snapshot = new WinReDeploymentJournalSnapshot(Path, records);
        try
        {
            WinReDeploymentHashPolicy.RequireSealedPlan(snapshot.Plan);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("Deployment journal plan hash contract is invalid: " + exception.Message, exception);
        }
        return snapshot;
    }

    private void AppendDurable(WinReDeploymentJournalRecord record, bool createNew)
    {
        var json = JsonSerializer.Serialize(record, JsonOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        using var stream = new FileStream(
            Path,
            createNew ? FileMode.CreateNew : FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static WinReDeploymentJournalRecord BuildRecord(
        string transactionId,
        long sequence,
        WinReDeploymentStage stage,
        WinReJournalRecordKind kind,
        string detail,
        string previous,
        WinReDeploymentPlan? plan)
    {
        var unsigned = new WinReDeploymentJournalRecord
        {
            TransactionId = transactionId,
            Sequence = sequence,
            TimestampUtc = DateTimeOffset.UtcNow,
            Stage = stage,
            Kind = kind,
            Detail = detail,
            PreviousRecordSha256 = previous,
            RecordSha256 = string.Empty,
            Plan = plan
        };
        return unsigned with { RecordSha256 = ComputeRecordHash(unsigned) };
    }

    private static string ComputeRecordHash(WinReDeploymentJournalRecord record)
    {
        var unsigned = record with { RecordSha256 = string.Empty };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(unsigned, JsonOptions)));
    }
}

public sealed record WinReDeploymentJournalInventory(
    IReadOnlyList<WinReDeploymentJournalSnapshot> Active,
    IReadOnlyList<string> Invalid);

public static class WinReDeploymentCommitSelection
{
    public static WinReDeploymentJournalSnapshot RequireExactAwaitingSmoke(
        WinReDeploymentJournalInventory inventory,
        string transactionId)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (!Guid.TryParseExact(transactionId, "N", out _))
            throw new InvalidOperationException("--deployment-transaction must be one exact N-format transaction id.");
        if (inventory.Invalid.Count != 0)
            throw new InvalidOperationException(
                "Deployment commit refuses corrupt, inaccessible, or unreconciled journal state: " +
                string.Join(" | ", inventory.Invalid));
        if (inventory.Active.Count != 1)
            throw new InvalidOperationException(
                $"Deployment commit requires exactly one authoritative unresolved transaction; found {inventory.Active.Count}.");

        var active = inventory.Active[0];
        if (active.Last.Stage != WinReDeploymentStage.AwaitingSmoke ||
            !string.Equals(active.Plan.TransactionId, transactionId, StringComparison.Ordinal) ||
            !string.Equals(active.Last.TransactionId, transactionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Deployment commit requires the explicitly named authoritative transaction at exactly AwaitingSmoke.");
        }

        return active;
    }
}

public static class WinReDeploymentJournalDiscovery
{
    public static string LegacyMachineRoot => System.IO.Path.Combine(
        WindowsWinReWorkspaceFactory.MachineRoot, "deployments");

    public static string ResolveAuthoritativeRoot(CleanSwitchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!VolumeLocator.TryParseGptId(options.RecoveryDataVolumeGptId, out _))
            throw new RetirementStorageException(
                "CleanSwitch:RecoveryDataVolumeGptId must be a concrete GPT id before WinRE journal discovery.");

        var statePath = RetirementStateStore.ResolveStateFilePath(options);
        var stateDirectory = System.IO.Path.GetDirectoryName(statePath)
            ?? throw new RetirementStorageException("The RecoveryData state directory could not be resolved.");
        return System.IO.Path.Combine(stateDirectory, "winre-deployments");
    }

    public static WinReDeploymentJournalInventory Inspect(CleanSwitchOptions options)
    {
        var authoritativeRoot = ResolveAuthoritativeRoot(options);
        var authoritative = Inspect(authoritativeRoot);
        if (authoritative.Active.Count > 0 || authoritative.Invalid.Count > 0) return authoritative;
        try
        {
            ValidateLegacyReconciliationMarker(authoritativeRoot, options);
            return authoritative;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            return new WinReDeploymentJournalInventory(authoritative.Active,
                [.. authoritative.Invalid, "Legacy cross-boot journal reconciliation is not proven: " + exception.Message]);
        }
    }

    public static string ReconcileLegacyJournals(CleanSwitchOptions options)
    {
        var authoritativeRoot = ResolveAuthoritativeRoot(options);
        var roots = ResolveProtectedLegacyRoots(options);
        var invalid = new List<string>();
        var active = new List<WinReDeploymentJournalSnapshot>();
        foreach (var (role, root) in roots)
        {
            var inventory = Inspect(root);
            invalid.AddRange(inventory.Invalid.Select(item => $"{role}: {item}"));
            active.AddRange(inventory.Active);
        }
        if (invalid.Count > 0 || active.Count > 0)
            throw new InvalidOperationException(
                "Legacy reconciliation found unresolved/corrupt journals: " +
                string.Join(" | ", invalid.Concat(active.Select(item => $"{item.Path} stage={item.Last.Stage}"))));

        Directory.CreateDirectory(authoritativeRoot);
        var marker = BuildMarker(options);
        var path = MarkerPath(authoritativeRoot);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(marker) + Environment.NewLine);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        ValidateLegacyReconciliationMarker(authoritativeRoot, options);
        return path;
    }

    private static IReadOnlyDictionary<string, string> ResolveProtectedLegacyRoots(CleanSwitchOptions options)
    {
        var requested = new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            ["Boot1"] = RequireConfiguredGpt(options.Boot1PartitionGptId, "Boot1PartitionGptId"),
            ["Boot2"] = RequireConfiguredGpt(options.Boot2PartitionGptId, "Boot2PartitionGptId")
        };
        var inventory = VolumeLocator.Enumerate();
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (role, gpt) in requested)
        {
            var matches = inventory.Volumes.Where(volume => volume.GptPartitionGuid == gpt).ToList();
            if (matches.Count != 1)
                throw new InvalidOperationException($"{role} GPT {VolumeLocator.FormatGptId(gpt)} resolved {matches.Count} times.");
            var volumeRoot = matches[0].VolumeGuidPath;
            try
            {
                _ = Directory.EnumerateFileSystemEntries(volumeRoot).Take(1).ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"{role} legacy ProgramData location is inaccessible.", exception);
            }
            roots[role] = System.IO.Path.Combine(volumeRoot, "ProgramData", "CleanSwitch", "WinRE", "deployments");
        }
        return roots;
    }

    private static Guid RequireConfiguredGpt(string value, string name) =>
        VolumeLocator.TryParseGptId(value, out var id)
            ? id
            : throw new InvalidOperationException($"{name} must be configured before legacy reconciliation.");

    private static string MarkerPath(string root) => System.IO.Path.Combine(root, "legacy-journal-reconciliation-v1.json");

    private static LegacyJournalReconciliationMarker BuildMarker(CleanSwitchOptions options)
    {
        var unsigned = new LegacyJournalReconciliationMarker
        {
            SchemaVersion = 1,
            Boot1PartitionGptId = options.Boot1PartitionGptId,
            Boot2PartitionGptId = options.Boot2PartitionGptId,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            RecordSha256 = string.Empty
        };
        return unsigned with { RecordSha256 = MarkerHash(unsigned) };
    }

    private static void ValidateLegacyReconciliationMarker(string root, CleanSwitchOptions options)
    {
        var path = MarkerPath(root);
        if (!File.Exists(path))
            throw new InvalidDataException("explicit --reconcile-legacy-winre-journals has not completed.");
        var marker = JsonSerializer.Deserialize<LegacyJournalReconciliationMarker>(File.ReadAllText(path))
            ?? throw new InvalidDataException("reconciliation marker is empty.");
        if (marker.SchemaVersion != 1 ||
            !string.Equals(marker.Boot1PartitionGptId, options.Boot1PartitionGptId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(marker.Boot2PartitionGptId, options.Boot2PartitionGptId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(marker.RecordSha256, MarkerHash(marker), StringComparison.Ordinal))
            throw new InvalidDataException("reconciliation marker is corrupt, stale, or bound to different protected GPT identities.");
    }

    private static string MarkerHash(LegacyJournalReconciliationMarker marker)
    {
        var unsigned = marker with { RecordSha256 = string.Empty };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(unsigned)));
    }

    public static WinReDeploymentJournalInventory InspectAuthoritativeRoots(
        string authoritativeRoot,
        string legacyRoot)
    {
        var authoritative = Inspect(authoritativeRoot);
        if (string.Equals(
                System.IO.Path.GetFullPath(authoritativeRoot),
                System.IO.Path.GetFullPath(legacyRoot),
                StringComparison.OrdinalIgnoreCase))
            return authoritative;

        var legacy = Inspect(legacyRoot);
        var invalid = authoritative.Invalid.ToList();
        invalid.AddRange(legacy.Invalid.Select(item => "Legacy ProgramData journal is invalid and requires operator review: " + item));
        invalid.AddRange(legacy.Active.Select(item =>
            $"Legacy ProgramData journal is unresolved and cannot be migrated automatically: {item.Path} " +
            $"stage={item.Last.Stage} sequence={item.Last.Sequence}."));
        return new WinReDeploymentJournalInventory(authoritative.Active, invalid);
    }

    public static WinReDeploymentJournalInventory Inspect(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        try
        {
            if (!Directory.Exists(root))
            {
                return new WinReDeploymentJournalInventory([], []);
            }
            if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            {
                return new WinReDeploymentJournalInventory([], [$"{root}: deployment journal root is a reparse point."]);
            }

            var active = new List<WinReDeploymentJournalSnapshot>();
            var invalid = new List<string>();
            foreach (var path in Directory.EnumerateFiles(root, "deployment-journal.ndjson", SearchOption.AllDirectories))
            {
                try
                {
                    var snapshot = new FileWinReDeploymentJournal(path).Load();
                    if (snapshot.RequiresRecovery) active.Add(snapshot);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    invalid.Add($"{path}: {exception.Message}");
                }
            }

            return new WinReDeploymentJournalInventory(active, invalid);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new WinReDeploymentJournalInventory([], [$"{root}: journal discovery failed closed: {exception.Message}"]);
        }
    }
}

public sealed record LegacyJournalReconciliationMarker
{
    public int SchemaVersion { get; init; }
    public required string Boot1PartitionGptId { get; init; }
    public required string Boot2PartitionGptId { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public required string RecordSha256 { get; init; }
}
