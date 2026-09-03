using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CleanSwitch.Recovery;

public enum WinReDeploymentStage
{
    D0Prepared,
    D1Snapshotted,
    D2BackupVerified,
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
    RecoveryRequired
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
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string TransactionId { get; init; }
    public required string PreparedWimPath { get; init; }
    public required string PreparedWimSha256 { get; init; }
    public required string PreparedBundlePath { get; init; }
    public required string PreparedBundleSha256 { get; init; }
    public required string LiveWimPath { get; init; }
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

        return new WinReDeploymentJournalSnapshot(Path, records);
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

public static class WinReDeploymentJournalDiscovery
{
    public static string MachineRoot => System.IO.Path.Combine(
        WindowsWinReWorkspaceFactory.MachineRoot, "deployments");

    public static WinReDeploymentJournalInventory Inspect(string? root = null)
    {
        root ??= MachineRoot;
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
