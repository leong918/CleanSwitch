using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

public sealed record WinReLauncherManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string RecoveryGuid { get; init; } = string.Empty;
    public string ExecutableRelativePath { get; init; } = WinReLauncherContract.ExecutableRelativePath;
    public string ConfigurationRelativePath { get; init; } = WinReLauncherContract.ConfigurationRelativePath;
    public string[] Arguments { get; init; } = WinReLauncherContract.RecoveryArguments.ToArray();
    public string ExecutableSha256 { get; init; } = string.Empty;
    public string ConfigurationSha256 { get; init; } = string.Empty;
    public string ProductVersion { get; init; } = string.Empty;
    public string RecoveryDataVolumeGptId { get; init; } = string.Empty;
    public string RecoveryDataFolderName { get; init; } = string.Empty;
}

public sealed record WinReLauncherExpectation(
    WinReLauncherManifest Manifest,
    string SourceExecutablePath,
    string SourceConfigurationPath);

public sealed record WinReLauncherValidationResult(ValidationReport Report, string? ImagePath = null)
{
    public bool Passed => Report.Passed;
}

public interface IWinReLauncherValidator
{
    Task<WinReLauncherValidationResult> ValidateAsync(
        RecoveryEntryResolution recovery,
        CancellationToken cancellationToken = default);
}

public static class WinReLauncherProvisioningGuard
{
    public static void Validate(
        RetirementState? existingState,
        CleanSwitchOptions options,
        bool destructiveOperationsImplemented,
        bool bcdOperationsImplemented)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!destructiveOperationsImplemented || !bcdOperationsImplemented ||
            !options.EnableDestructiveRetirement)
        {
            throw new InvalidOperationException(
                "WinRE launcher provisioning requires DestructiveOperationsImplemented=true, " +
                "BcdOperationsImplemented=true, and EnableDestructiveRetirement=true because the launcher " +
                "explicitly supplies --execute-deletion.");
        }

        if (existingState is not null && !existingState.IsTerminal)
        {
            throw new InvalidOperationException(
                $"WinRE launcher provisioning is forbidden while retirement state is " +
                $"{RetirementStatusNames.ToWire(existingState.Status)}. Officially abandon or complete it first.");
        }
    }
}

/// <summary>
/// Immutable contract for the code that WinPE launches. The embedded executable is the same
/// approved single-file build that starts Phase 2A. Its appsettings locates retirement state
/// by RecoveryDataVolumeGptId; no installed-Windows drive letter is part of the contract.
/// </summary>
public static class WinReLauncherContract
{
    public const string PayloadDirectory = "CleanSwitchRecovery";
    public const string ExecutableRelativePath = PayloadDirectory + @"\CleanSwitch.exe";
    public const string ConfigurationRelativePath = PayloadDirectory + @"\appsettings.json";
    public const string ManifestRelativePath = PayloadDirectory + @"\winre-launcher-manifest.json";
    public const string WinpeshlRelativePath = @"Windows\System32\winpeshl.ini";

    public static readonly IReadOnlyList<string> RecoveryArguments =
        ["--recovery-run", "--execute-deletion"];

    public static readonly string WinpeshlContents =
        "[LaunchApps]\r\n" +
        "%SYSTEMDRIVE%\\CleanSwitchRecovery\\CleanSwitch.exe, --recovery-run --execute-deletion\r\n" +
        "%SYSTEMROOT%\\System32\\recenv.exe\r\n";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static WinReLauncherExpectation CreateExpectation(
        CleanSwitchOptions options,
        string recoveryGuid,
        string executablePath,
        string configurationPath)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!BcdIdentifiers.TryParseObjectId(recoveryGuid, out var parsedRecovery))
        {
            throw new InvalidOperationException("A concrete RecoveryGuid is required for the WinRE launcher manifest.");
        }

        if (!VolumeLocator.TryParseGptId(options.RecoveryDataVolumeGptId, out var dataVolume))
        {
            throw new InvalidOperationException(
                "CleanSwitch:RecoveryDataVolumeGptId must be a concrete GPT partition GUID before WinRE can be provisioned.");
        }

        if (!File.Exists(executablePath) || !File.Exists(configurationPath))
        {
            throw new InvalidOperationException("The approved CleanSwitch executable and appsettings.json must both exist.");
        }

        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(executablePath).ProductVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException("The approved CleanSwitch executable has no ProductVersion.");
        }

        var manifest = new WinReLauncherManifest
        {
            RecoveryGuid = BcdIdentifiers.Format(parsedRecovery),
            ExecutableSha256 = HashFile(executablePath),
            ConfigurationSha256 = HashFile(configurationPath),
            ProductVersion = version,
            RecoveryDataVolumeGptId = VolumeLocator.FormatGptId(dataVolume),
            RecoveryDataFolderName = options.ResolveRecoveryDataFolderName()
        };

        if (string.IsNullOrWhiteSpace(manifest.RecoveryDataFolderName))
        {
            throw new InvalidOperationException("CleanSwitch:RecoveryDataFolderName must resolve to a non-empty folder name.");
        }

        return new WinReLauncherExpectation(manifest, executablePath, configurationPath);
    }

    public static ValidationReport ValidateOfflineRoot(string offlineRoot, WinReLauncherExpectation expected)
    {
        var report = new ValidationReport("WinRE CleanSwitch launcher");
        if (string.IsNullOrWhiteSpace(offlineRoot) || !Directory.Exists(offlineRoot))
        {
            report.Fail("winre-image-mounted", $"WinRE offline root '{offlineRoot}' is unavailable.");
            return report;
        }

        report.Pass("winre-image-mounted", $"Inspecting mounted WinRE root '{offlineRoot}'.");

        var winpeshlPath = UnderRoot(offlineRoot, WinpeshlRelativePath);
        var exePath = UnderRoot(offlineRoot, ExecutableRelativePath);
        var configPath = UnderRoot(offlineRoot, ConfigurationRelativePath);
        var manifestPath = UnderRoot(offlineRoot, ManifestRelativePath);

        ValidateExactText(report, "launcher-startup", winpeshlPath, WinpeshlContents);
        report.Add(
            "launcher-executable-present",
            File.Exists(exePath),
            File.Exists(exePath) ? $"Found '{ExecutableRelativePath}'." : $"Missing '{ExecutableRelativePath}'.");
        report.Add(
            "launcher-configuration-present",
            File.Exists(configPath),
            File.Exists(configPath) ? $"Found '{ConfigurationRelativePath}'." : $"Missing '{ConfigurationRelativePath}'.");
        report.Add(
            "launcher-manifest-present",
            File.Exists(manifestPath),
            File.Exists(manifestPath) ? $"Found '{ManifestRelativePath}'." : $"Missing '{ManifestRelativePath}'.");

        WinReLauncherManifest? actual = null;
        if (File.Exists(manifestPath))
        {
            try
            {
                actual = JsonSerializer.Deserialize<WinReLauncherManifest>(File.ReadAllText(manifestPath), JsonOptions);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                report.Fail("launcher-manifest-readable", $"Manifest could not be read exactly: {exception.Message}");
            }
        }

        if (actual is null)
        {
            report.Fail("launcher-manifest-valid", "Launcher manifest is absent, unreadable, or empty.");
        }
        else
        {
            ValidateManifest(report, actual, expected.Manifest);
        }

        ValidateHash(report, "launcher-executable-hash", exePath, expected.Manifest.ExecutableSha256);
        ValidateHash(report, "launcher-configuration-hash", configPath, expected.Manifest.ConfigurationSha256);

        if (File.Exists(exePath))
        {
            var actualVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath).ProductVersion ?? string.Empty;
            report.Add(
                "launcher-product-version",
                string.Equals(actualVersion, expected.Manifest.ProductVersion, StringComparison.Ordinal),
                $"actual='{actualVersion}', expected='{expected.Manifest.ProductVersion}'.");
        }

        return report;
    }

    public static void WritePayload(string offlineRoot, WinReLauncherExpectation expected)
    {
        var payload = UnderRoot(offlineRoot, PayloadDirectory);
        Directory.CreateDirectory(payload);
        Directory.CreateDirectory(Path.GetDirectoryName(UnderRoot(offlineRoot, WinpeshlRelativePath))!);

        File.Copy(expected.SourceExecutablePath, UnderRoot(offlineRoot, ExecutableRelativePath), overwrite: true);
        File.Copy(expected.SourceConfigurationPath, UnderRoot(offlineRoot, ConfigurationRelativePath), overwrite: true);
        File.WriteAllText(
            UnderRoot(offlineRoot, ManifestRelativePath),
            JsonSerializer.Serialize(expected.Manifest, JsonOptions) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            UnderRoot(offlineRoot, WinpeshlRelativePath),
            WinpeshlContents,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void ValidateManifest(
        ValidationReport report,
        WinReLauncherManifest actual,
        WinReLauncherManifest expected)
    {
        report.Add("launcher-schema", actual.SchemaVersion == WinReLauncherManifest.CurrentSchemaVersion,
            $"actual={actual.SchemaVersion}, required={WinReLauncherManifest.CurrentSchemaVersion}.");
        AddExact(report, "launcher-recovery-guid", actual.RecoveryGuid, expected.RecoveryGuid);
        AddExact(report, "launcher-executable-path", actual.ExecutableRelativePath, expected.ExecutableRelativePath);
        AddExact(report, "launcher-configuration-path", actual.ConfigurationRelativePath, expected.ConfigurationRelativePath);
        AddExact(report, "launcher-manifest-executable-hash", actual.ExecutableSha256, expected.ExecutableSha256);
        AddExact(report, "launcher-manifest-configuration-hash", actual.ConfigurationSha256, expected.ConfigurationSha256);
        AddExact(report, "launcher-manifest-product-version", actual.ProductVersion, expected.ProductVersion);
        AddExact(report, "launcher-data-volume-gpt", actual.RecoveryDataVolumeGptId, expected.RecoveryDataVolumeGptId);
        AddExact(report, "launcher-data-folder", actual.RecoveryDataFolderName, expected.RecoveryDataFolderName);
        report.Add(
            "launcher-official-entrypoint",
            actual.Arguments is not null && actual.Arguments.SequenceEqual(RecoveryArguments, StringComparer.Ordinal),
            $"actual arguments=[{string.Join(' ', actual.Arguments ?? [])}], required=[{string.Join(' ', RecoveryArguments)}]. " +
            "The GUI/default Program path is not a valid recovery continuation.");
    }

    private static void ValidateExactText(ValidationReport report, string name, string path, string expected)
    {
        if (!File.Exists(path))
        {
            report.Fail(name, $"Missing '{WinpeshlRelativePath}'; stock WinRE would open its normal UI.");
            return;
        }

        var actual = File.ReadAllText(path);
        report.Add(
            name,
            string.Equals(actual, expected, StringComparison.Ordinal),
            string.Equals(actual, expected, StringComparison.Ordinal)
                ? "winpeshl.ini invokes only the verified recovery runner payload, then recenv.exe as a fail-closed fallback."
                : "winpeshl.ini is stale, ambiguous, or does not exactly invoke '--recovery-run --execute-deletion'.");
    }

    private static void ValidateHash(ValidationReport report, string name, string path, string expected)
    {
        if (!File.Exists(path))
        {
            report.Fail(name, $"Cannot hash missing file '{path}'.");
            return;
        }

        var actual = HashFile(path);
        report.Add(name, string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            $"actual={actual}, expected={expected}.");
    }

    private static void AddExact(ValidationReport report, string name, string? actual, string expected) =>
        report.Add(name, string.Equals(actual, expected, StringComparison.Ordinal),
            $"actual='{actual ?? "<null>"}', expected='{expected}'.");

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string UnderRoot(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("WinRE payload path escaped the mounted image root.");
        }

        return result;
    }
}
