using CleanSwitch.Models;

namespace CleanSwitch.Recovery;

public enum BcdAliasResolution
{
    Resolved,
    Absent,
    Unresolved
}

public enum BcdObjectKind
{
    Unknown,
    WindowsLoader,
    ResumeLoader,
    RecoveryLoader,
    BootManager,
    FirmwareBootManager,
    FirmwareObject,
    MemoryDiagnostic,
    Other
}

/// <summary>One BCD object identified by its concrete GUID. Description is audit-only.</summary>
public sealed class BcdEntryIdentity
{
    public required Guid ObjectId { get; init; }

    public required string FormattedId { get; init; }

    public string Description { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string Device { get; init; } = string.Empty;

    public string OsDevice { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public BcdObjectKind Kind { get; init; }

    public bool IdentifierWasAlias { get; init; }

    public string Describe() =>
        $"{FormattedId} kind={Kind} path={(string.IsNullOrWhiteSpace(Path) ? "<none>" : Path)} " +
        $"description={(string.IsNullOrWhiteSpace(Description) ? "<none>" : Description)} (audit only)";

    public static BcdEntryIdentity FromEntry(BcdEntry entry)
    {
        var wasAlias = BcdIdentifiers.IsAlias(entry.Identifier);
        BcdIdentifiers.TryParseObjectId(entry.Identifier, out var objectId);
        return new BcdEntryIdentity
        {
            ObjectId = objectId,
            FormattedId = wasAlias || objectId == Guid.Empty
                ? entry.Identifier.Trim()
                : BcdIdentifiers.Format(objectId),
            Description = entry.Description,
            Path = entry.Path,
            Device = entry.Device,
            OsDevice = entry.OsDevice,
            Type = entry.Type,
            Kind = Classify(entry, objectId),
            IdentifierWasAlias = wasAlias
        };
    }

    public static BcdObjectKind Classify(BcdEntry entry, Guid objectId)
    {
        if (objectId == BcdIdentifiers.BootManagerId ||
            string.Equals(entry.Identifier, "{bootmgr}", StringComparison.OrdinalIgnoreCase))
        {
            return BcdObjectKind.BootManager;
        }

        if (objectId == BcdIdentifiers.FirmwareBootManagerId)
        {
            return BcdObjectKind.FirmwareBootManager;
        }

        if (objectId == BcdIdentifiers.MemoryDiagnosticId)
        {
            return BcdObjectKind.MemoryDiagnostic;
        }

        if (entry.IsResumeLoader)
        {
            return BcdObjectKind.ResumeLoader;
        }

        if (entry.LooksLikeRecoveryEnvironment)
        {
            return BcdObjectKind.RecoveryLoader;
        }

        if (entry.Type.Contains("firmware", StringComparison.OrdinalIgnoreCase))
        {
            return BcdObjectKind.FirmwareObject;
        }

        if (entry.IsWindowsLoader)
        {
            return BcdObjectKind.WindowsLoader;
        }

        return string.IsNullOrWhiteSpace(entry.Type) ? BcdObjectKind.Unknown : BcdObjectKind.Other;
    }
}

public sealed class BcdSnapshot
{
    public BcdSnapshot(
        IReadOnlyList<BcdEntryIdentity> entries,
        Guid? currentObjectId,
        Guid? defaultObjectId,
        bool bootManagerPresent,
        IReadOnlyList<string> warnings,
        BcdAliasResolution? currentResolution = null,
        BcdAliasResolution? defaultResolution = null)
    {
        Entries = entries;
        CurrentObjectId = currentObjectId;
        DefaultObjectId = defaultObjectId;
        BootManagerPresent = bootManagerPresent;
        Warnings = warnings;
        CurrentResolution = currentResolution ??
            (currentObjectId is not null ? BcdAliasResolution.Resolved : BcdAliasResolution.Unresolved);
        DefaultResolution = defaultResolution ??
            (defaultObjectId is not null ? BcdAliasResolution.Resolved : BcdAliasResolution.Unresolved);
    }

    public IReadOnlyList<BcdEntryIdentity> Entries { get; }

    public Guid? CurrentObjectId { get; }

    public Guid? DefaultObjectId { get; }

    public bool BootManagerPresent { get; }

    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// How <c>{current}</c> was resolved. Isolated <c>/createstore</c> files have no
    /// running OS, so they may report <see cref="BcdAliasResolution.Absent"/>.
    /// Production treats unresolved as a hard refusal.
    /// </summary>
    public BcdAliasResolution CurrentResolution { get; }

    public BcdAliasResolution DefaultResolution { get; }

    public IReadOnlyList<BcdEntryIdentity> WithObjectId(Guid objectId) =>
        Entries.Where(entry => !entry.IdentifierWasAlias && entry.ObjectId == objectId).ToList();

    public IReadOnlySet<Guid> ConcreteObjectIds() =>
        Entries.Where(entry => !entry.IdentifierWasAlias && entry.ObjectId != Guid.Empty)
            .Select(entry => entry.ObjectId)
            .ToHashSet();
}

public sealed record ResolvedBcdDeletionTarget(Guid ObjectId)
{
    public string FormattedId => BcdIdentifiers.Format(ObjectId);

    public string Describe() => $"bcdObject={FormattedId}";
}

public sealed class BcdResolveResult
{
    public required bool Passed { get; init; }

    public ResolvedBcdDeletionTarget? Target { get; init; }

    public required ValidationReport Report { get; init; }

    public IReadOnlySet<Guid> ApprovedSurvivorIds { get; init; } = new HashSet<Guid>();
}
