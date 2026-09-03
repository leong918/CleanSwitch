using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CleanSwitch.Models;

namespace CleanSwitch.Recovery;

public sealed record RecoverySmokeEvidence(
    string RecoveryGuid,
    string ProductVersion,
    string ExecutableSha256,
    string ConfigurationSha256,
    string RecoveryDataVolumeGptId,
    string RecoveryDataRoot,
    string WinReSystemRoot,
    string LauncherRoot);

public sealed record RecoverySmokeReceipt
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string ReceiptId { get; init; }
    public required string DeploymentTransactionId { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required string RecoveryGuid { get; init; }
    public required string ProductVersion { get; init; }
    public required string ExecutableSha256 { get; init; }
    public required string ConfigurationSha256 { get; init; }
    public required string RecoveryDataVolumeGptId { get; init; }
    public required string Result { get; init; }
    public required string WinReSystemRoot { get; init; }
    public required string LauncherRoot { get; init; }
    public bool WinReDetected { get; init; }
    public bool DestructiveExecutorInstantiated { get; init; }
    public bool RetirementStateCreated { get; init; }
}

public sealed record RecoverySmokeResult(bool Passed, string Message, string? ReceiptPath, string? ReceiptSha256);

public interface IRecoverySmokeEnvironment
{
    RecoverySmokeEvidence Verify(CleanSwitchOptions options);
    string WriteReceiptDurably(string recoveryDataRoot, RecoverySmokeReceipt receipt);
}

/// <summary>
/// Dedicated non-retirement WinRE smoke path. This type has no RecoveryRunner,
/// RetirementExecutor, disk command, BCD command, or state-store dependency.
/// </summary>
public sealed class RecoverySmokeRunner
{
    private readonly CleanSwitchOptions _options;
    private readonly IRecoverySmokeEnvironment _environment;
    private readonly string _deploymentTransactionId;

    public RecoverySmokeRunner(
        CleanSwitchOptions options,
        IRecoverySmokeEnvironment environment,
        string deploymentTransactionId)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        if (!Guid.TryParseExact(deploymentTransactionId, "N", out _))
            throw new ArgumentException("A concrete N-format deployment transaction id is required.", nameof(deploymentTransactionId));
        _deploymentTransactionId = deploymentTransactionId;
    }

    public RecoverySmokeResult Run()
    {
        var evidence = _environment.Verify(_options);
        var receipt = new RecoverySmokeReceipt
        {
            ReceiptId = Guid.NewGuid().ToString("N"),
            DeploymentTransactionId = _deploymentTransactionId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            RecoveryGuid = evidence.RecoveryGuid,
            ProductVersion = evidence.ProductVersion,
            ExecutableSha256 = evidence.ExecutableSha256,
            ConfigurationSha256 = evidence.ConfigurationSha256,
            RecoveryDataVolumeGptId = evidence.RecoveryDataVolumeGptId,
            WinReSystemRoot = evidence.WinReSystemRoot,
            LauncherRoot = evidence.LauncherRoot,
            WinReDetected = true,
            Result = "PASS: launcher manifest, embedded payload and RecoveryData GPT identity verified; no retirement operation was opened.",
            DestructiveExecutorInstantiated = false,
            RetirementStateCreated = false
        };
        var path = _environment.WriteReceiptDurably(evidence.RecoveryDataRoot, receipt);
        return new RecoverySmokeResult(true, receipt.Result, path, HashFile(path));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public sealed class WindowsRecoverySmokeEnvironment : IRecoverySmokeEnvironment
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public RecoverySmokeEvidence Verify(CleanSwitchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Recovery smoke cannot resolve its executable path.");
        var payloadRoot = AppContext.BaseDirectory;
        var config = Path.Combine(payloadRoot, "appsettings.json");
        var manifestPath = Path.Combine(payloadRoot, "winre-launcher-manifest.json");
        if (!File.Exists(executable) || !File.Exists(config) || !File.Exists(manifestPath))
        {
            throw new InvalidOperationException("Recovery smoke requires the embedded executable, appsettings and launcher manifest.");
        }

        using var miniNt = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\MiniNT", writable: false);
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
            ?? throw new InvalidOperationException("Recovery smoke cannot resolve SystemRoot.");
        var systemDrive = Path.GetPathRoot(Path.GetFullPath(systemRoot))
            ?? throw new InvalidOperationException("Recovery smoke cannot resolve the WinRE system drive.");
        var expectedPayloadRoot = Path.GetFullPath(Path.Combine(systemDrive, WinReLauncherContract.PayloadDirectory))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var actualPayloadRoot = Path.GetFullPath(payloadRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fallback = Path.Combine(systemDrive, WinReLauncherContract.FallbackExecutableRelativePath);
        var winpeshl = Path.Combine(systemRoot, "System32", "winpeshl.ini");
        if (miniNt is null ||
            !string.Equals(actualPayloadRoot, expectedPayloadRoot, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fallback) || !File.Exists(winpeshl) ||
            !string.Equals(File.ReadAllText(winpeshl), WinReLauncherContract.WinpeshlContents, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "--recovery-smoke is accepted only from the exact customized WinRE launcher environment.");
        }

        var manifest = JsonSerializer.Deserialize<WinReLauncherManifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidDataException("Recovery launcher manifest is unreadable.");
        var exeHash = HashFile(executable);
        var configHash = HashFile(config);
        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? string.Empty;
        if (manifest.SchemaVersion != WinReLauncherManifest.CurrentSchemaVersion ||
            !BcdIdentifiers.IdsEqual(manifest.RecoveryGuid, options.RecoveryGuid) ||
            !string.Equals(manifest.ExecutableSha256, exeHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.ConfigurationSha256, configHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.ProductVersion, version, StringComparison.Ordinal) ||
            !string.Equals(manifest.RecoveryDataVolumeGptId, options.RecoveryDataVolumeGptId, StringComparison.OrdinalIgnoreCase) ||
            manifest.Arguments is null ||
            !manifest.Arguments.SequenceEqual(WinReLauncherContract.RecoveryArguments, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Recovery smoke launcher manifest does not exactly match this payload/configuration.");
        }

        if (!VolumeLocator.TryParseGptId(options.RecoveryDataVolumeGptId, out var dataGpt))
        {
            throw new InvalidOperationException("RecoveryDataVolumeGptId is not a concrete GPT GUID.");
        }

        var volume = VolumeLocator.TryFindUniqueByGptId(dataGpt, out var error)
            ?? throw new InvalidOperationException(error);
        if (volume.MountPoints.Count != 1 || string.IsNullOrWhiteSpace(volume.PrimaryMountPoint))
        {
            throw new InvalidOperationException("RecoveryData GPT volume must have exactly one usable mount point in WinRE.");
        }

        var dataRoot = Path.Combine(volume.PrimaryMountPoint, options.ResolveRecoveryDataFolderName());
        if (!Directory.Exists(dataRoot))
        {
            throw new InvalidOperationException($"RecoveryData root '{dataRoot}' is unavailable; smoke will not create it.");
        }

        var statePath = Path.Combine(dataRoot, string.IsNullOrWhiteSpace(options.StateFileName)
            ? CleanSwitchOptions.DefaultStateFileName
            : options.StateFileName);
        if (File.Exists(statePath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            var root = document.RootElement;
            var terminal = root.TryGetProperty("isTerminal", out var terminalValue) && terminalValue.ValueKind == JsonValueKind.True;
            var status = root.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
            if (!terminal || !string.Equals(status, "ABORTED", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Recovery smoke requires no active retirement operation; state is status='{status ?? "<unknown>"}', terminal={terminal}.");
            }
        }

        return new RecoverySmokeEvidence(
            manifest.RecoveryGuid, version, exeHash, configHash,
            VolumeLocator.FormatGptId(dataGpt), dataRoot,
            Path.GetFullPath(systemRoot), actualPayloadRoot.TrimEnd(Path.DirectorySeparatorChar));
    }

    public string WriteReceiptDurably(string recoveryDataRoot, RecoverySmokeReceipt receipt)
    {
        var directory = Path.Combine(recoveryDataRoot, "smoke-receipts");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"winre-smoke-{receipt.CreatedAtUtc:yyyyMMddTHHmmssZ}-{receipt.ReceiptId}.json");
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(receipt, JsonOptions) + Environment.NewLine);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return path;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public static class RecoverySmokeReceiptVerifier
{
    public static string Verify(string path, WinReDeploymentPlan plan)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("Recovery smoke receipt is unavailable.");
        var receipt = JsonSerializer.Deserialize<RecoverySmokeReceipt>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Recovery smoke receipt is unreadable.");
        if (receipt.SchemaVersion != RecoverySmokeReceipt.CurrentSchemaVersion ||
            !string.Equals(receipt.DeploymentTransactionId, plan.TransactionId, StringComparison.Ordinal) ||
            !BcdIdentifiers.IdsEqual(receipt.RecoveryGuid, plan.ExpectedRecoveryGuid) ||
            !string.Equals(receipt.RecoveryDataVolumeGptId,
                ExtractExpectedDataGpt(plan), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(receipt.ProductVersion, plan.ProductVersion, StringComparison.Ordinal) ||
            !string.Equals(receipt.ExecutableSha256, plan.ExecutableSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(receipt.ConfigurationSha256, plan.ConfigurationSha256, StringComparison.OrdinalIgnoreCase) ||
            receipt.DestructiveExecutorInstantiated || receipt.RetirementStateCreated ||
            !receipt.WinReDetected || string.IsNullOrWhiteSpace(receipt.WinReSystemRoot) ||
            string.IsNullOrWhiteSpace(receipt.LauncherRoot) ||
            !receipt.Result.StartsWith("PASS:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Recovery smoke receipt does not match the deployment transaction or records unsafe activity.");
        }

        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // The deployment plan separately binds the recovery partition and the independent
    // RecoveryData partition; the smoke receipt must match the latter.
    private static string ExtractExpectedDataGpt(WinReDeploymentPlan plan) => plan.RecoveryDataVolumeGptId;
}
