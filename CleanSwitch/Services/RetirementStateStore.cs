using System.Text;
using System.Text.Json;
using CleanSwitch.Models;
using CleanSwitch.Recovery;

namespace CleanSwitch.Services;

/// <summary>
/// Thrown when the retirement state file cannot be stored somewhere that survives
/// Boot 1 being retired. Always carries operator-actionable guidance.
/// </summary>
public sealed class RetirementStorageException : Exception
{
    public RetirementStorageException(string message)
        : base(message)
    {
    }

    public RetirementStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Reads and writes the retirement state file.
/// <para>
/// Location rule: the state file must NOT live only on the volume that is going to be
/// retired, otherwise the recovery-side run would lose its instructions the moment the
/// deletion succeeded. The path is configured by <c>CleanSwitch:RecoveryDataPath</c> and
/// is rejected outright when it resolves onto the running Windows volume, unless the
/// operator opts in with <c>CleanSwitch:AllowStateOnSystemVolume</c> (test-only).
/// </para>
/// </summary>
public sealed class RetirementStateStore
{
    private const string TempSuffix = ".tmp";
    private const string BackupSuffix = ".bak";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IOperationLog _log;

    public RetirementStateStore(CleanSwitchOptions options, IOperationLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _log = log ?? NullOperationLog.Instance;
        StateFilePath = ResolveStateFilePath(options);
    }

    public string StateFilePath { get; }

    public string StateDirectory => Path.GetDirectoryName(StateFilePath) ?? StateFilePath;

    /// <summary>
    /// Resolves the log directory for a given configuration without needing a store
    /// instance, so logging can start before path validation runs (and can therefore
    /// record why path validation failed).
    /// </summary>
    public static string? ResolveLogDirectory(CleanSwitchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.LogDirectory))
        {
            return TryGetFullPath(options.LogDirectory);
        }

        if (!string.IsNullOrWhiteSpace(options.RecoveryDataPath))
        {
            var root = TryGetFullPath(options.RecoveryDataPath);
            return root is null ? null : Path.Combine(root, "logs");
        }

        return null;
    }

    public static string ResolveStateFilePath(CleanSwitchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.RecoveryDataPath))
        {
            throw new RetirementStorageException(
                "CleanSwitch:RecoveryDataPath is not set. The retirement state file must live on a volume " +
                "that survives Boot 1 being retired, so CleanSwitch will not guess a location." +
                Environment.NewLine +
                "Set it in appsettings.json to a folder on a non-Boot-1 volume, for example \"D:\\\\CleanSwitchData\".");
        }

        var root = TryGetFullPath(options.RecoveryDataPath)
            ?? throw new RetirementStorageException(
                $"CleanSwitch:RecoveryDataPath '{options.RecoveryDataPath}' is not a usable filesystem path.");

        var fileName = string.IsNullOrWhiteSpace(options.StateFileName)
            ? CleanSwitchOptions.DefaultStateFileName
            : options.StateFileName.Trim();

        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new RetirementStorageException(
                $"CleanSwitch:StateFileName '{fileName}' contains characters that are not valid in a file name.");
        }

        var stateFilePath = Path.Combine(root, fileName);

        var mountPoint = VolumeIdentity.TryGetVolumeMountPoint(root);
        if (mountPoint is null || !Directory.Exists(mountPoint))
        {
            throw new RetirementStorageException(
                $"The volume hosting CleanSwitch:RecoveryDataPath ('{root}') is not available." +
                Environment.NewLine +
                "Attach or mount that volume, or point CleanSwitch:RecoveryDataPath at a folder on a volume " +
                "that is present both in Boot 1 and in the recovery environment. CleanSwitch will not fall back " +
                "to a Boot 1 only path, because that copy would disappear together with Boot 1.");
        }

        var configuredVolume = VolumeIdentity.TryGetVolumeGuidPath(root);
        var systemVolume = VolumeIdentity.TryGetRunningSystemVolumeGuidPath();
        var systemMountPoint = VolumeIdentity.TryGetVolumeMountPoint(Environment.SystemDirectory);

        // Prefer volume GUID comparison. If either GUID lookup failed, fall back to mount
        // point comparison so an unresolvable volume is treated as "possibly Boot 1"
        // instead of silently passing the check.
        var onSystemVolume = configuredVolume is not null && systemVolume is not null
            ? VolumeIdentity.AreSameVolume(configuredVolume, systemVolume)
            : string.Equals(mountPoint, systemMountPoint, StringComparison.OrdinalIgnoreCase);

        if (onSystemVolume && !options.AllowStateOnSystemVolume)
        {
            throw new RetirementStorageException(
                $"CleanSwitch:RecoveryDataPath ('{root}') resolves to the running Windows volume " +
                $"({systemVolume ?? systemMountPoint ?? "unknown"}), which is the volume being retired." +
                Environment.NewLine +
                "The only copy of the retirement state would be destroyed by the operation it is supposed " +
                "to be driving." +
                Environment.NewLine +
                "Fix this by pointing CleanSwitch:RecoveryDataPath at a folder on a different volume " +
                "(a second internal disk, a data partition, or a USB stick that is also visible from WinRE)." +
                Environment.NewLine +
                "For non-destructive Phase 2A handoff testing only, set CleanSwitch:AllowStateOnSystemVolume " +
                "to true to accept this unsafe location.");
        }

        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new RetirementStorageException(
                $"CleanSwitch could not create the retirement data folder '{root}': {exception.Message}" +
                Environment.NewLine +
                "Create the folder manually, grant Administrators write access to it, or choose another " +
                "CleanSwitch:RecoveryDataPath.",
                exception);
        }

        return stateFilePath;
    }

    /// <summary>
    /// Confirms the configured location is actually writable right now. Called before the
    /// UI writes a PENDING record, so a bad path fails before anything reboots.
    /// </summary>
    public void EnsureWritable()
    {
        var probe = Path.Combine(StateDirectory, $".cleanswitch-write-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "probe", Encoding.UTF8);
            File.Delete(probe);
            _log.Info("state-store", $"Write probe succeeded in '{StateDirectory}'.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new RetirementStorageException(
                $"CleanSwitch cannot write to the retirement data folder '{StateDirectory}': {exception.Message}" +
                Environment.NewLine +
                "The retirement flow will not start, because the state file is the only record that survives " +
                "the reboot into recovery. Grant write access or choose another CleanSwitch:RecoveryDataPath.",
                exception);
        }
    }

    public bool Exists() => File.Exists(StateFilePath);

    public RetirementState? TryLoad()
    {
        if (!File.Exists(StateFilePath))
        {
            _log.Info("state-store", $"No retirement state file at '{StateFilePath}'.");
            return null;
        }

        string json;
        try
        {
            json = File.ReadAllText(StateFilePath, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RetirementStorageException(
                $"CleanSwitch could not read the retirement state file '{StateFilePath}': {exception.Message}",
                exception);
        }

        RetirementState state;
        try
        {
            state = JsonSerializer.Deserialize<RetirementState>(json, JsonOptions)
                ?? throw new RetirementStorageException(
                    $"The retirement state file '{StateFilePath}' is empty or not a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new RetirementStorageException(
                $"The retirement state file '{StateFilePath}' is not valid JSON: {exception.Message}" +
                Environment.NewLine +
                "Inspect it by hand. Do not delete it while a retirement is in progress.",
                exception);
        }

        if (state.SchemaVersion != RetirementState.CurrentSchemaVersion)
        {
            throw new RetirementStorageException(
                $"The retirement state file '{StateFilePath}' has schemaVersion {state.SchemaVersion}, " +
                $"but this build understands version {RetirementState.CurrentSchemaVersion}. " +
                "Refusing to act on a state file it may misinterpret.");
        }

        if (!string.Equals(state.Operation, RetirementState.RetireBoot1Operation, StringComparison.Ordinal))
        {
            throw new RetirementStorageException(
                $"The retirement state file '{StateFilePath}' describes operation '{state.Operation}', " +
                $"but only '{RetirementState.RetireBoot1Operation}' is supported.");
        }

        _log.Info(
            "state-store",
            $"Loaded retirement state from '{StateFilePath}': status={RetirementStatusNames.ToWire(state.Status)}, " +
            $"boot1={state.Boot1Id}, boot2={state.Boot2Id}, transitions={state.Transitions.Count}.");

        return state;
    }

    /// <summary>
    /// Writes the state file atomically: serialise to a temp file in the same directory,
    /// flush it to disk, then replace the target in one filesystem operation. A crash can
    /// therefore leave the old file or the new file, never a half-written one.
    /// </summary>
    public void Save(RetirementState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(state, JsonOptions);
        var tempPath = StateFilePath + TempSuffix;
        var backupPath = StateFilePath + BackupSuffix;

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(StateFilePath))
            {
                File.Replace(tempPath, StateFilePath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, StateFilePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new RetirementStorageException(
                $"CleanSwitch could not write the retirement state file '{StateFilePath}': {exception.Message}" +
                Environment.NewLine +
                "No boot change should be attempted while the state file cannot be persisted.",
                exception);
        }

        _log.Info(
            "state-store",
            $"Persisted retirement state '{RetirementStatusNames.ToWire(state.Status)}' to '{StateFilePath}'.");
    }

    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
