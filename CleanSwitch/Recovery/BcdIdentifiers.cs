using System.Text.RegularExpressions;

namespace CleanSwitch.Recovery;

/// <summary>
/// BCD object identifiers. Destructive work accepts only a concrete GUID, never an alias
/// and never a display name.
/// </summary>
public static class BcdIdentifiers
{
    public static readonly Guid BootManagerId = Guid.Parse("9dea862c-5cdd-4e70-acc1-f32b344d4795");
    public static readonly Guid FirmwareBootManagerId = Guid.Parse("a5a30fa2-3d06-4e9f-b5f4-a01df9d1fcba");
    public static readonly Guid MemoryDiagnosticId = Guid.Parse("b2721d73-1db4-4c62-bf78-c548a880142d");
    public static readonly Guid NtldrId = Guid.Parse("466f5a88-0af2-4f76-9038-095b170dc21c");

    private static readonly HashSet<string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "{bootmgr}",
        "{fwbootmgr}",
        "{memdiag}",
        "{ntldr}",
        "{current}",
        "{default}",
        "{recoverysequence}",
        "{resumeloadersettings}",
        "{dbgsettings}",
        "{emssettings}",
        "{globalsettings}",
        "{bootloadersettings}",
        "{hypervisorsettings}"
    };

    public static string Format(Guid objectId) => $"{{{objectId:D}}}";

    public static bool TryParseObjectId(string? raw, out Guid objectId)
    {
        objectId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(raw) || IsAlias(raw))
        {
            return false;
        }

        return Guid.TryParse(raw.Trim(), out objectId);
    }

    public static Guid RequireConcreteObjectId(string? raw, string role)
    {
        if (!TryParseObjectId(raw, out var objectId))
        {
            throw new RetirementExecutionException(
                $"{role} BCD identity '{raw}' is not a concrete BCD object GUID. " +
                "Aliases such as {{current}}, {{default}} and {{bootmgr}} are refused. " +
                "Display names are never used. State must be regenerated on Boot 1.");
        }

        if (IsProtectedObject(objectId))
        {
            throw new RetirementExecutionException(
                $"{role} BCD identity {Format(objectId)} is a protected BCD object and cannot be a deletion target.");
        }

        return objectId;
    }

    public static bool IsAlias(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return Aliases.Contains(raw.Trim());
    }

    public static bool IsProtectedObject(Guid objectId) =>
        objectId == BootManagerId ||
        objectId == FirmwareBootManagerId ||
        objectId == MemoryDiagnosticId ||
        objectId == NtldrId;

    public static bool IdsEqual(string? left, string? right) =>
        TryParseObjectId(left, out var leftId) &&
        TryParseObjectId(right, out var rightId) &&
        leftId == rightId;

    /// <summary>
    /// Extracts a concrete GUID embedded in BCD <c>device</c>/<c>osdevice</c> text
    /// such as <c>partition={guid}</c>. Drive letters, aliases, and display names
    /// never produce a value.
    /// </summary>
    public static bool TryParseEmbeddedGuid(string? text, out Guid objectId)
    {
        objectId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(text) || IsAlias(text.Trim()))
        {
            return false;
        }

        if (TryParseObjectId(text, out objectId))
        {
            return true;
        }

        var match = Regex.Match(
            text,
            @"\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}");
        if (!match.Success || IsAlias(match.Value))
        {
            objectId = Guid.Empty;
            return false;
        }

        return TryParseObjectId(match.Value, out objectId);
    }
}
