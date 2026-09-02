using System.Globalization;
using System.Security.Cryptography;
using CleanSwitch.Models;

namespace CleanSwitch.Services;

/// <summary>
/// Copies a retirement state file into <c>&lt;state-directory&gt;\archive\</c>
/// and refuses to return until the copy's SHA-256 matches the original.
/// Never deletes the archive, even when verification fails.
/// </summary>
public sealed class FileRetirementStateArchiver : IRetirementStateArchiver
{
    public const string ArchiveFolderName = "archive";

    private readonly Func<string, string> _computeSha256Hex;

    public FileRetirementStateArchiver()
        : this(ComputeSha256Hex)
    {
    }

    /// <summary>
    /// Test seam for hash-mismatch cases. Production uses
    /// <see cref="ComputeSha256Hex"/>.
    /// </summary>
    public FileRetirementStateArchiver(Func<string, string> computeSha256Hex)
    {
        _computeSha256Hex = computeSha256Hex ?? throw new ArgumentNullException(nameof(computeSha256Hex));
    }

    public RetirementStateArchive ArchiveVerifiedCopy(string sourcePath, RetirementState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(state);

        if (!File.Exists(sourcePath))
        {
            throw new RetirementStateException(
                $"Cannot archive retirement state: the live file '{sourcePath}' is missing. " +
                "The live retirement state was not changed.");
        }

        var stateDirectory = Path.GetDirectoryName(sourcePath)
            ?? throw new RetirementStateException(
                $"Cannot archive retirement state: '{sourcePath}' has no parent directory.");
        var archiveDirectory = Path.Combine(stateDirectory, ArchiveFolderName);
        Directory.CreateDirectory(archiveDirectory);

        var archivePath = AllocateArchivePath(archiveDirectory, state);
        File.Copy(sourcePath, archivePath, overwrite: false);

        if (!File.Exists(archivePath))
        {
            throw new RetirementStateException(
                $"Archive copy was not created at '{archivePath}'. The live retirement state was not changed.");
        }

        VerifyIdentical(sourcePath, archivePath, _computeSha256Hex);
        return new RetirementStateArchive(archivePath, _computeSha256Hex(sourcePath));
    }

    public static string ComputeSha256Hex(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    public static void VerifyIdentical(string sourcePath, string archivePath) =>
        VerifyIdentical(sourcePath, archivePath, ComputeSha256Hex);

    public static void VerifyIdentical(string sourcePath, string archivePath, Func<string, string> computeSha256Hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(computeSha256Hex);

        if (!File.Exists(archivePath))
        {
            throw new RetirementStateException(
                $"Archive verification failed: '{archivePath}' does not exist. " +
                "The live retirement state was not changed.");
        }

        var sourceHash = computeSha256Hex(sourcePath);
        var archiveHash = computeSha256Hex(archivePath);
        if (!string.Equals(sourceHash, archiveHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new RetirementStateException(
                $"Archive verification failed: SHA-256 of '{archivePath}' ({archiveHash}) " +
                $"does not match the live state file '{sourcePath}' ({sourceHash}). " +
                "The live retirement state was not changed. The archive file was left in place.");
        }
    }

    private static string AllocateArchivePath(string archiveDirectory, RetirementState state)
    {
        var status = RetirementStatusNames.ToWire(state.Status);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var name = $"retirement-state.{status}.v{state.SchemaVersion}.{stamp}.json";
        var path = Path.Combine(archiveDirectory, name);
        if (!File.Exists(path))
        {
            return path;
        }

        return Path.Combine(
            archiveDirectory,
            $"retirement-state.{status}.v{state.SchemaVersion}.{stamp}.{Guid.NewGuid():N}.json");
    }
}
