using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

/// <summary>Supplies a live BCD snapshot. Production reads the system store; tests inject a fake or a temp file store.</summary>
public interface IBcdStoreSource
{
    Task<BcdSnapshot> CaptureAsync();
}

/// <summary>Production source: <c>bcdedit /enum ... /v</c> via <see cref="IBootManager"/>.</summary>
public sealed class BootManagerBcdStoreSource : IBcdStoreSource
{
    private readonly IBootManager _bootManager;

    public BootManagerBcdStoreSource(IBootManager bootManager)
    {
        _bootManager = bootManager ?? throw new ArgumentNullException(nameof(bootManager));
    }

    public async Task<BcdSnapshot> CaptureAsync()
    {
        var warnings = new List<string>();
        IReadOnlyList<BcdEntry> all;
        try
        {
            all = await _bootManager.EnumerateAsync("all");
        }
        catch (Exception exception)
        {
            throw new RetirementExecutionException(
                "BCD enumeration could not be parsed safely. Refusing Phase 2C. " + exception.Message,
                exception);
        }

        if (all.Count == 0)
        {
            throw new RetirementExecutionException(
                "BCD enumeration returned no objects. Refusing to parse an empty store as success.");
        }

        var identities = all.Select(BcdEntryIdentity.FromEntry).ToList();
        if (identities.Any(entry => entry.IdentifierWasAlias || entry.ObjectId == Guid.Empty))
        {
            warnings.Add("One or more BCD identifiers remained aliases after /v enumeration.");
        }

        var current = await TryResolveAliasAsync("{current}", warnings);
        var defaultId = await TryResolveAliasAsync("{default}", warnings);
        var bootmgr = identities.Any(entry =>
            entry.Kind == BcdObjectKind.BootManager || entry.ObjectId == BcdIdentifiers.BootManagerId);

        if (!bootmgr)
        {
            var bootmgrEntries = await TryEnumerateAsync("{bootmgr}", warnings);
            bootmgr = bootmgrEntries.Any(entry =>
                entry.Kind == BcdObjectKind.BootManager ||
                BcdIdentifiers.IdsEqual(entry.FormattedId, BcdIdentifiers.Format(BcdIdentifiers.BootManagerId)));
        }

        return new BcdSnapshot(
            identities,
            current.Id,
            defaultId.Id,
            bootmgr,
            warnings,
            current.Resolution,
            defaultId.Resolution);
    }

    private sealed record AliasResolve(Guid? Id, BcdAliasResolution Resolution);

    private async Task<AliasResolve> TryResolveAliasAsync(string alias, List<string> warnings)
    {
        IReadOnlyList<BcdEntryIdentity> entries;
        try
        {
            var raw = await _bootManager.EnumerateAsync(alias);
            entries = raw.Select(BcdEntryIdentity.FromEntry).ToList();
        }
        catch (Exception exception)
        {
            warnings.Add($"{alias} could not be enumerated: {exception.Message}");
            return new AliasResolve(null, BcdAliasResolution.Unresolved);
        }

        var concrete = entries
            .Where(entry => !entry.IdentifierWasAlias && entry.ObjectId != Guid.Empty)
            .Select(entry => entry.ObjectId)
            .Distinct()
            .ToList();

        if (concrete.Count == 1)
        {
            return new AliasResolve(concrete[0], BcdAliasResolution.Resolved);
        }

        if (concrete.Count > 1)
        {
            warnings.Add($"{alias} resolved to {concrete.Count} concrete GUIDs. Treating as unresolved.");
            return new AliasResolve(null, BcdAliasResolution.Unresolved);
        }

        warnings.Add($"{alias} did not resolve to a concrete BCD object GUID.");
        return new AliasResolve(null, BcdAliasResolution.Unresolved);
    }

    private async Task<IReadOnlyList<BcdEntryIdentity>> TryEnumerateAsync(string scope, List<string> warnings)
    {
        try
        {
            var entries = await _bootManager.EnumerateAsync(scope);
            return entries.Select(BcdEntryIdentity.FromEntry).ToList();
        }
        catch (Exception exception)
        {
            warnings.Add($"{scope} could not be enumerated: {exception.Message}");
            return [];
        }
    }
}
