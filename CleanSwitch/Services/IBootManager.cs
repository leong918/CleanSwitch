using CleanSwitch.Models;

namespace CleanSwitch.Services;

public interface IBootManager
{
    Task<BootLayout> DetectAsync(string? preferredOtherGuid);

    Task<bool> SetNextBootAsync(string bootGuid);

    Task RestartAsync(int delaySeconds);

    /// <summary>
    /// Enumerates BCD entries for a scope such as <c>OSLOADER</c>, <c>all</c> or
    /// <c>{current}</c>. Used by the recovery-side validators, which need entry types
    /// (WinRE loaders) that the switch flow deliberately filters out.
    /// </summary>
    Task<IReadOnlyList<BcdEntry>> EnumerateAsync(string scope);

    /// <summary>Returns the entry for a GUID, or null when BCDEdit does not know it.</summary>
    Task<BcdEntry?> TryGetEntryAsync(string bootGuid);
}
