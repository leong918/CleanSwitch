namespace CleanSwitch.Models;

/// <summary>
/// Stable identity of a partition or volume.
/// <para>
/// Drive letters are deliberately not part of any identity comparison: in WinRE the
/// letters are reassigned, so "D:" in recovery can be a completely different volume
/// than "D:" in Boot 1. <see cref="ObservedDriveLetter"/> exists only for log output.
/// </para>
/// </summary>
public sealed class PartitionIdentity
{
    public int? DiskNumber { get; set; }

    public int? PartitionNumber { get; set; }

    /// <summary>Win32 volume GUID path, e.g. <c>\\?\Volume{GUID}\</c>.</summary>
    public string? VolumeGuidPath { get; set; }

    /// <summary>GPT unique partition GUID from the partition table.</summary>
    public string? GptPartitionId { get; set; }

    /// <summary>
    /// GPT partition type GUID (EFI System, Basic Data, Microsoft Recovery, ...).
    /// Used to refuse ESP / Recovery / MSR targets. Not a unique identity.
    /// </summary>
    public string? GptPartitionType { get; set; }

    /// <summary>Raw BCD <c>device</c> / <c>osdevice</c> text, e.g. <c>partition=D:</c> or <c>unknown</c>.</summary>
    public string? BcdDevice { get; set; }

    /// <summary>Log/diagnostics only. Never used for identity decisions.</summary>
    public string? ObservedDriveLetter { get; set; }

    /// <summary>Free-text note explaining where this identity came from.</summary>
    public string? Source { get; set; }

    /// <summary>
    /// True when at least two independent, reboot-stable identifiers are present.
    /// Win32 volume GUIDs are not counted: WinPE assigns new ones, so they are not
    /// stable across Boot 1 / WinRE / Boot 2. Drive letters are never counted.
    /// </summary>
    public bool HasStableIdentifiers => StableIdentifierCount >= 2;

    public int StableIdentifierCount
    {
        get
        {
            var count = 0;
            if (DiskNumber is not null && PartitionNumber is not null)
            {
                count++;
            }

            if (!string.IsNullOrWhiteSpace(GptPartitionId))
            {
                count++;
            }

            return count;
        }
    }

    public string Describe()
    {
        var parts = new List<string>();
        if (DiskNumber is not null || PartitionNumber is not null)
        {
            parts.Add($"disk={DiskNumber?.ToString() ?? "?"} partition={PartitionNumber?.ToString() ?? "?"}");
        }

        if (!string.IsNullOrWhiteSpace(VolumeGuidPath))
        {
            parts.Add($"volume={VolumeGuidPath}");
        }

        if (!string.IsNullOrWhiteSpace(GptPartitionId))
        {
            parts.Add($"gptId={GptPartitionId}");
        }

        if (!string.IsNullOrWhiteSpace(GptPartitionType))
        {
            parts.Add($"gptType={GptPartitionType}");
        }

        if (!string.IsNullOrWhiteSpace(BcdDevice))
        {
            parts.Add($"bcdDevice={BcdDevice}");
        }

        if (!string.IsNullOrWhiteSpace(ObservedDriveLetter))
        {
            parts.Add($"letter={ObservedDriveLetter} (informational)");
        }

        if (!string.IsNullOrWhiteSpace(Source))
        {
            parts.Add($"source={Source}");
        }

        return parts.Count == 0 ? "<no identifiers>" : string.Join(", ", parts);
    }
}
