using CleanSwitch.Models;

namespace CleanSwitch.Services;

/// <summary>
/// Byte-for-byte archive of a retirement state file. Used by the operator abandon
/// path so the live file is never rewritten until a verified copy exists.
/// </summary>
public interface IRetirementStateArchiver
{
    /// <summary>
    /// Copies <paramref name="sourcePath"/> to the archive folder next to it,
    /// then verifies the copy exists and has the same SHA-256 as the source.
    /// Must not modify the source. Must not delete the archive on verify failure.
    /// </summary>
    RetirementStateArchive ArchiveVerifiedCopy(string sourcePath, RetirementState state);
}

/// <summary>A verified archive copy of a retirement state file.</summary>
public sealed record RetirementStateArchive(string Path, string Sha256Hex);
