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
/// Where the current configuration says the retirement state lives, resolved without
/// creating or writing anything. <see cref="Error"/> is set instead of the paths when the
/// configuration cannot be resolved at all.
/// </summary>
public sealed record RetirementLocationPreview(
    string? Root,
    string? StateFilePath,
    string? Source,
    PartitionIdentity? VolumeIdentity,
    string? Error);

/// <summary>
/// Reads and writes the retirement state file.
/// <para>
/// Location rule: the state file must NOT live only on the volume that is going to be
/// retired, otherwise the recovery-side run would lose its instructions the moment deletion
/// succeeded. Creation retains the conservative running-Windows boundary and then proves the
/// state host distinct from freshly captured Boot 1. Existing schema-v2 operations instead
/// prove the host against the persisted Boot 1 identity; running Windows and drive letters are
/// not identities. Legacy operations retain the conservative running-volume policy.
/// </para>
/// <para>
/// Identity rule: a drive letter designates a different volume in each environment the
/// retirement flow crosses (Boot 1, WinRE, Boot 2), and WinPE mints its own Win32 volume
/// GUIDs, so neither is a usable anchor. When
/// <c>CleanSwitch:RecoveryDataVolumeGptId</c> is set, the folder is resolved by GPT unique
/// partition GUID — read from the partition table on the disk — and the current mount point
/// is looked up at runtime. <c>CleanSwitch:RecoveryDataPath</c> keeps working as before when
/// no GPT id is configured.
/// </para>
/// <para>
/// Write side (Boot 1 creating the operation) resolves the configured location only. Read
/// side (WinRE / Boot 2) additionally scans fixed volumes for a matching state file, because
/// the configured letter may point somewhere else entirely in that environment. Ambiguity
/// during a scan is a hard failure, never a guess.
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
    private readonly string _stateFileName;
    private readonly string _folderName;
    private readonly RetirementStateAccessContext _accessContext;
    private readonly bool _allowStateOnSystemVolume;
    private readonly bool _allowFixedVolumeScan;
    private readonly IGptLayoutSource _layout;
    private bool _stateVolumeIsRunningWindows;
    private bool _scanned;

    public RetirementStateStore(
        CleanSwitchOptions options,
        IOperationLog? log = null,
        RetirementStateAccessContext accessContext = RetirementStateAccessContext.CreateNewOperation,
        IGptLayoutSource? layout = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _log = log ?? NullOperationLog.Instance;
        _accessContext = accessContext;
        _allowStateOnSystemVolume = options.AllowStateOnSystemVolume;
        _allowFixedVolumeScan = string.IsNullOrWhiteSpace(options.RecoveryDataVolumeGptId);
        _layout = layout ?? new VolumeLocatorGptLayoutSource();

        var resolution = ResolveStateLocation(
            options,
            rejectRunningWindowsVolume: accessContext == RetirementStateAccessContext.CreateNewOperation);
        ConfiguredStateFilePath = resolution.StateFilePath;
        StateFilePath = resolution.StateFilePath;
        StateVolumeIdentity = resolution.VolumeIdentity;
        _stateVolumeIsRunningWindows = resolution.IsRunningWindowsVolume;
        _stateFileName = resolution.StateFileName;
        _folderName = resolution.FolderName;

        _log.Info(
            "state-store",
            $"Retirement state location resolved by {resolution.Source}: '{resolution.StateFilePath}'" +
            (resolution.VolumeIdentity is null
                ? " (volume identity unavailable)"
                : $" on {resolution.VolumeIdentity.Describe()}"));
    }

    /// <summary>
    /// Where configuration says the state file is. <see cref="StateFilePath"/> can differ
    /// from this after a successful read-side scan.
    /// </summary>
    public string ConfiguredStateFilePath { get; }

    public string StateFilePath { get; private set; }

    /// <summary>True when <see cref="StateFilePath"/> came from a volume scan, not configuration.</summary>
    public bool StateFileFoundByScan { get; private set; }

    /// <summary>Stable identity of the volume <see cref="StateFilePath"/> lives on, when it could be read.</summary>
    public PartitionIdentity? StateVolumeIdentity { get; private set; }

    public string StateDirectory => Path.GetDirectoryName(StateFilePath) ?? StateFilePath;

    /// <summary>
    /// Resolves the log directory for a given configuration without needing a store
    /// instance, so logging can start before path validation runs (and can therefore
    /// record why path validation failed). Never throws: when the location cannot be
    /// resolved, logging degrades to the %ProgramData% destination alone.
    /// </summary>
    public static string? ResolveLogDirectory(CleanSwitchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.LogDirectory))
        {
            return TryGetFullPath(options.LogDirectory);
        }

        try
        {
            var root = ResolveRootDirectory(options).Root;
            return Path.Combine(root, "logs");
        }
        catch (RetirementStorageException)
        {
            return null;
        }
    }

    /// <summary>
    /// Kept for callers that only need the path. Applies the same resolution and the same
    /// safety checks as the constructor.
    /// </summary>
    public static string ResolveStateFilePath(CleanSwitchOptions options) =>
        ResolveStateLocation(options, rejectRunningWindowsVolume: true).StateFilePath;

    /// <summary>
    /// What the current configuration points at, with nothing created and nothing written.
    /// For the <c>--list-volumes</c> diagnostic, so the operator can confirm a configured
    /// GPT partition GUID resolves before starting a flow that reboots.
    /// </summary>
    public static RetirementLocationPreview PreviewLocation(CleanSwitchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var fileName = string.IsNullOrWhiteSpace(options.StateFileName)
            ? CleanSwitchOptions.DefaultStateFileName
            : options.StateFileName.Trim();

        try
        {
            var resolved = ResolveRootDirectory(options);
            return new RetirementLocationPreview(
                resolved.Root,
                Path.Combine(resolved.Root, fileName),
                resolved.Source,
                resolved.VolumeIdentity,
                null);
        }
        catch (RetirementStorageException exception)
        {
            return new RetirementLocationPreview(null, null, null, null, exception.Message);
        }
    }

    private static StateLocation ResolveStateLocation(
        CleanSwitchOptions options,
        bool rejectRunningWindowsVolume)
    {
        ArgumentNullException.ThrowIfNull(options);

        var fileName = string.IsNullOrWhiteSpace(options.StateFileName)
            ? CleanSwitchOptions.DefaultStateFileName
            : options.StateFileName.Trim();

        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new RetirementStorageException(
                $"CleanSwitch:StateFileName '{fileName}' contains characters that are not valid in a file name.");
        }

        var resolvedRoot = ResolveRootDirectory(options);
        var root = resolvedRoot.Root;
        var stateFilePath = Path.Combine(root, fileName);

        var mountPoint = VolumeIdentity.TryGetVolumeMountPoint(root);
        if (mountPoint is null || !Directory.Exists(mountPoint))
        {
            throw new RetirementStorageException(
                $"The volume hosting the retirement data folder ('{root}', resolved by {resolvedRoot.Source}) " +
                "is not available." +
                Environment.NewLine +
                "Attach or mount that volume, or point the retirement data folder at a volume that is present " +
                "both in Boot 1 and in the recovery environment. CleanSwitch will not fall back to a Boot 1 " +
                "only path, because that copy would disappear together with Boot 1.");
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

        if (rejectRunningWindowsVolume && onSystemVolume && !options.AllowStateOnSystemVolume)
        {
            throw new RetirementStorageException(
                $"The retirement data folder ('{root}', resolved by {resolvedRoot.Source}) resolves to the " +
                $"running Windows volume ({systemVolume ?? systemMountPoint ?? "unknown"}), which is the volume " +
                "being retired." +
                Environment.NewLine +
                "The only copy of the retirement state would be destroyed by the operation it is supposed " +
                "to be driving." +
                Environment.NewLine +
                "Fix this by pointing CleanSwitch:RecoveryDataVolumeGptId (preferred) or " +
                "CleanSwitch:RecoveryDataPath at a different volume: a second internal disk, a data " +
                "partition, or a USB stick that is also visible from WinRE. Run " +
                "'CleanSwitch.exe --list-volumes' to see the GPT partition GUIDs to choose from." +
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
                "Create the folder manually, grant Administrators write access to it, or point " +
                "CleanSwitch:RecoveryDataVolumeGptId / CleanSwitch:RecoveryDataPath at another volume.",
                exception);
        }

        return new StateLocation(
            stateFilePath,
            fileName,
            resolvedRoot.FolderName,
            resolvedRoot.Source,
            resolvedRoot.VolumeIdentity ?? TryDescribeVolumeForPath(root, "Volume hosting the retirement data folder"),
            onSystemVolume);
    }

    /// <summary>
    /// Turns configuration into the retirement data folder.
    /// <para>
    /// Order: (1) <c>CleanSwitch:RecoveryDataVolumeGptId</c>, resolved through the partition
    /// table to whatever mount point that volume currently has; (2) the literal
    /// <c>CleanSwitch:RecoveryDataPath</c>. A configured GPT id that cannot be found is a
    /// hard failure. Falling back to the letter path there would be actively dangerous: the
    /// same letter designates a different volume in each environment, so the fallback could
    /// write the state file onto the volume slated for deletion.
    /// </para>
    /// </summary>
    private static ResolvedRoot ResolveRootDirectory(CleanSwitchOptions options)
    {
        var folderName = options.ResolveRecoveryDataFolderName();

        if (!string.IsNullOrWhiteSpace(options.RecoveryDataVolumeGptId))
        {
            return ResolveRootByGptId(options, folderName);
        }

        if (string.IsNullOrWhiteSpace(options.RecoveryDataPath))
        {
            throw new RetirementStorageException(
                "Neither CleanSwitch:RecoveryDataVolumeGptId nor CleanSwitch:RecoveryDataPath is set. The " +
                "retirement state file must live on a volume that survives Boot 1 being retired, so CleanSwitch " +
                "will not guess a location." +
                Environment.NewLine +
                "Preferred: run 'CleanSwitch.exe --list-volumes', copy the GPT partition GUID of the volume that " +
                "should hold the state file into CleanSwitch:RecoveryDataVolumeGptId, and set " +
                "CleanSwitch:RecoveryDataFolderName (for example \"CleanSwitchData\")." +
                Environment.NewLine +
                "Or set CleanSwitch:RecoveryDataPath to a folder on a non-Boot-1 volume, for example " +
                "\"D:\\\\CleanSwitchData\". Note that a drive letter means a different volume in Boot 1, in " +
                "WinRE and in Boot 2.");
        }

        var root = TryGetFullPath(options.RecoveryDataPath)
            ?? throw new RetirementStorageException(
                $"CleanSwitch:RecoveryDataPath '{options.RecoveryDataPath}' is not a usable filesystem path.");

        return new ResolvedRoot(root, folderName, "CleanSwitch:RecoveryDataPath (literal drive letter path)", null);
    }

    private static ResolvedRoot ResolveRootByGptId(CleanSwitchOptions options, string folderName)
    {
        var configured = options.RecoveryDataVolumeGptId.Trim();

        if (!VolumeLocator.TryParseGptId(configured, out var gptPartitionId))
        {
            throw new RetirementStorageException(
                $"CleanSwitch:RecoveryDataVolumeGptId '{configured}' is not a GUID. Expected a value like " +
                "{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}, which you can read off " +
                "'CleanSwitch.exe --list-volumes'.");
        }

        if (string.IsNullOrWhiteSpace(folderName))
        {
            throw new RetirementStorageException(
                "CleanSwitch:RecoveryDataVolumeGptId is set but no folder name could be determined." +
                Environment.NewLine +
                "Set CleanSwitch:RecoveryDataFolderName (for example \"CleanSwitchData\"), or leave " +
                "CleanSwitch:RecoveryDataPath populated so its last path segment can be used.");
        }

        if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.IsPathRooted(folderName))
        {
            throw new RetirementStorageException(
                $"CleanSwitch:RecoveryDataFolderName '{folderName}' must be a plain folder name, not a rooted " +
                "path, because it is combined with whatever mount point the identified volume currently has.");
        }

        var located = VolumeLocator.Enumerate();
        var matches = located.WithGptPartitionId(gptPartitionId);

        if (matches.Count == 0)
        {
            throw new RetirementStorageException(
                $"No volume on this machine has GPT partition GUID {VolumeLocator.FormatGptId(gptPartitionId)}, " +
                "which CleanSwitch:RecoveryDataVolumeGptId names as the home of the retirement state file." +
                Environment.NewLine +
                "CleanSwitch will NOT fall back to CleanSwitch:RecoveryDataPath, because that is a drive letter " +
                "and a drive letter designates a different volume in every environment - including, possibly, " +
                "the volume this operation is meant to retire." +
                Environment.NewLine +
                "Volumes seen right now:" +
                Environment.NewLine +
                DescribeVolumes(located) +
                Environment.NewLine +
                "Run 'CleanSwitch.exe --list-volumes' and correct CleanSwitch:RecoveryDataVolumeGptId.");
        }

        if (matches.Count > 1)
        {
            throw new RetirementStorageException(
                $"{matches.Count} volumes report GPT partition GUID " +
                $"{VolumeLocator.FormatGptId(gptPartitionId)}, which cannot be true of a healthy partition " +
                "table. Refusing to choose one." +
                Environment.NewLine +
                string.Join(Environment.NewLine, matches.Select(volume => "  " + volume.Describe())));
        }

        var match = matches[0];
        var mountPoint = match.PrimaryMountPoint;

        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            throw new RetirementStorageException(
                $"The volume with GPT partition GUID {VolumeLocator.FormatGptId(gptPartitionId)} " +
                $"(disk {match.DiskNumber?.ToString() ?? "?"} partition " +
                $"{match.PartitionNumber?.ToString() ?? "?"}, {LocatedVolume.FormatSize(match.SizeBytes)}) has no " +
                "mount point in this environment, so CleanSwitch cannot build a path to it." +
                Environment.NewLine +
                "Assign it a drive letter in this environment (for example with diskpart 'assign letter=') and " +
                "run CleanSwitch again. CleanSwitch never mounts or unmounts volumes itself.");
        }

        var root = Path.Combine(mountPoint, folderName);

        return new ResolvedRoot(
            root,
            folderName,
            $"CleanSwitch:RecoveryDataVolumeGptId {VolumeLocator.FormatGptId(gptPartitionId)} " +
            $"-> current mount point '{mountPoint}'",
            match.ToPartitionIdentity(
                $"Volume located by GPT partition GUID {VolumeLocator.FormatGptId(gptPartitionId)}"));
    }

    private static string DescribeVolumes(VolumeLocatorResult located)
    {
        var lines = located.Volumes.Select(volume => "  " + volume.Describe()).ToList();
        lines.AddRange(located.Warnings.Select(warning => "  ! " + warning));
        return lines.Count == 0 ? "  (none)" : string.Join(Environment.NewLine, lines);
    }

    private static PartitionIdentity? TryDescribeVolumeForPath(string path, string source)
    {
        var volumeGuidPath = VolumeIdentity.TryGetVolumeGuidPath(path);
        if (volumeGuidPath is null)
        {
            return null;
        }

        var match = VolumeLocator.Enumerate().Volumes
            .FirstOrDefault(volume => VolumeIdentity.AreSameVolume(volume.VolumeGuidPath, volumeGuidPath));

        return match?.ToPartitionIdentity(source)
            ?? new PartitionIdentity
            {
                VolumeGuidPath = volumeGuidPath,
                ObservedDriveLetter = VolumeIdentity.TryGetVolumeMountPoint(path),
                Source = source + " (partition table lookup unavailable)"
            };
    }

    private sealed record ResolvedRoot(
        string Root,
        string FolderName,
        string Source,
        PartitionIdentity? VolumeIdentity);

    private sealed record StateLocation(
        string StateFilePath,
        string StateFileName,
        string FolderName,
        string Source,
        PartitionIdentity? VolumeIdentity,
        bool IsRunningWindowsVolume);

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
                "the reboot into recovery. Grant write access, or point " +
                "CleanSwitch:RecoveryDataVolumeGptId / CleanSwitch:RecoveryDataPath at another volume.",
                exception);
        }
    }

    public bool Exists() => File.Exists(StateFilePath);

    /// <summary>
    /// Capture-time state-location boundary. Called by <see cref="RetirementCoordinator"/>
    /// after Boot 1 identity capture and before it reads or writes an operation.
    /// </summary>
    public void ValidateForNewOperation(PartitionIdentity retiringBoot1)
    {
        ArgumentNullException.ThrowIfNull(retiringBoot1);

        if (_accessContext != RetirementStateAccessContext.CreateNewOperation)
        {
            throw new RetirementStorageException(
                "A new retirement operation cannot be created through an existing-state or operator-abandon store.");
        }

        if (_allowStateOnSystemVolume)
        {
            _log.Warn(
                "state-store",
                "TEST-ONLY AllowStateOnSystemVolume bypassed the complete GPT state-location proof for a new operation.");
            return;
        }

        StateVolumeSafetyValidator.ValidateForNewOperation(
            StateVolumeIdentity,
            retiringBoot1,
            _layout.Capture());
        _log.Info("state-store", "New-operation state volume is uniquely proven distinct from retiring Boot 1.");
    }

    /// <summary>
    /// Loads the state file. When nothing is at the configured location, falls back to
    /// scanning every fixed volume for <c>&lt;volume&gt;\&lt;folder&gt;\&lt;state file&gt;</c>,
    /// because in WinRE and on Boot 2 the configured drive letter points at a different
    /// volume than it did on Boot 1. A scan that finds more than one distinct valid state
    /// file fails loudly instead of choosing.
    /// </summary>
    public RetirementState? TryLoad()
    {
        if (!File.Exists(StateFilePath))
        {
            _log.Info("state-store", $"No retirement state file at '{StateFilePath}'.");

            if (!_allowFixedVolumeScan)
            {
                _log.Warn(
                    "state-store",
                    "RecoveryDataVolumeGptId is configured and authoritative. The configured volume contains no " +
                    "state file, so fixed-volume fallback scanning is disabled; an old state on another volume " +
                    "will not be adopted.");
                return null;
            }

            var scanned = TryAdoptScannedStateFile();
            if (scanned is null)
            {
                return null;
            }

            return Validate(scanned.State, scanned.Path);
        }

        var state = ReadAndParse(StateFilePath);
        return Validate(state, StateFilePath);
    }

    private RetirementState ReadAndParse(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RetirementStorageException(
                $"CleanSwitch could not read the retirement state file '{path}': {exception.Message}",
                exception);
        }

        try
        {
            return JsonSerializer.Deserialize<RetirementState>(json, JsonOptions)
                ?? throw new RetirementStorageException(
                    $"The retirement state file '{path}' is empty or not a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new RetirementStorageException(
                $"The retirement state file '{path}' is not valid JSON: {exception.Message}" +
                Environment.NewLine +
                "Inspect it by hand. Do not delete it while a retirement is in progress.",
                exception);
        }
    }

    private RetirementState Validate(RetirementState state, string path)
    {
        if (state.SchemaVersion < RetirementState.MinimumReadableSchemaVersion ||
            state.SchemaVersion > RetirementState.CurrentSchemaVersion)
        {
            throw new RetirementStorageException(
                $"The retirement state file '{path}' has schemaVersion {state.SchemaVersion}, " +
                $"but this build reads versions {RetirementState.MinimumReadableSchemaVersion}-" +
                $"{RetirementState.CurrentSchemaVersion}. " +
                "Refusing to act on a state file it may misinterpret.");
        }

        if (!string.Equals(state.Operation, RetirementState.RetireBoot1Operation, StringComparison.Ordinal))
        {
            throw new RetirementStorageException(
                $"The retirement state file '{path}' describes operation '{state.Operation}', " +
                $"but only '{RetirementState.RetireBoot1Operation}' is supported.");
        }

        ValidateExistingStateLocation(state, path);

        _log.Info(
            "state-store",
            $"Loaded retirement state from '{path}': status={RetirementStatusNames.ToWire(state.Status)}, " +
            $"boot1={state.Boot1Id}, boot2={state.Boot2Id}, transitions={state.Transitions.Count}.");

        return state;
    }

    private void ValidateExistingStateLocation(RetirementState state, string path)
    {
        if (_accessContext == RetirementStateAccessContext.OperatorAbandon)
        {
            _log.Info(
                "state-store",
                "Operator-abandon context accepted the state location for archive-and-abort only. " +
                "This context cannot create or execute a retirement operation.");
            return;
        }

        if (_accessContext == RetirementStateAccessContext.CreateNewOperation)
        {
            // The constructor already applied the conservative running-volume boundary;
            // BeginRetirement performs the complete GPT proof against freshly captured Boot 1.
            return;
        }

        if (state.SchemaVersion < RetirementState.CurrentSchemaVersion)
        {
            StateVolumeSafetyValidator.ValidateLegacy(
                _stateVolumeIsRunningWindows,
                _allowStateOnSystemVolume);
            _log.Info("state-store", "Legacy state-location policy passed conservatively.");
            return;
        }

        try
        {
            StateVolumeSafetyValidator.ValidateExistingSchema2(
                state,
                StateVolumeIdentity,
                _layout.Capture());
            _log.Info(
                "state-store",
                "Schema-v2 state volume is uniquely proven distinct from persisted retiring Boot 1. " +
                "The running Windows volume and drive letters were not used as rejection identities.");
        }
        catch (RetirementStorageException exception)
        {
            throw new RetirementStorageException(
                $"The existing retirement state at '{path}' failed state-volume safety validation: " +
                exception.Message,
                exception);
        }
    }

    /// <summary>
    /// Scans every fixed volume for a valid state file and, if exactly one distinct volume
    /// holds one, redirects this store at it so later transitions are saved back to the same
    /// file. Runs at most once per store instance.
    /// </summary>
    private ScannedStateFile? TryAdoptScannedStateFile()
    {
        if (_scanned)
        {
            return null;
        }

        _scanned = true;

        if (string.IsNullOrWhiteSpace(_folderName))
        {
            _log.Info(
                "state-store",
                "No retirement data folder name is configured, so there is nothing to scan volumes for.");
            return null;
        }

        var located = VolumeLocator.Enumerate();
        foreach (var warning in located.Warnings)
        {
            _log.Warn("state-store", $"Volume scan warning: {warning}");
        }

        var candidates = new List<ScannedStateFile>();
        var skippedRemovable = 0;

        foreach (var volume in located.Volumes)
        {
            if (!volume.IsFixed)
            {
                skippedRemovable++;
                continue;
            }

            foreach (var mountPoint in volume.MountPoints)
            {
                var candidatePath = Path.Combine(mountPoint, _folderName, _stateFileName);
                if (!File.Exists(candidatePath))
                {
                    continue;
                }

                RetirementState parsed;
                try
                {
                    parsed = ReadAndParse(candidatePath);
                }
                catch (RetirementStorageException exception)
                {
                    _log.Warn(
                        "state-store",
                        $"Volume scan ignored '{candidatePath}': {exception.Message}");
                    continue;
                }

                if (parsed.SchemaVersion < RetirementState.MinimumReadableSchemaVersion ||
                    parsed.SchemaVersion > RetirementState.CurrentSchemaVersion ||
                    !string.Equals(parsed.Operation, RetirementState.RetireBoot1Operation, StringComparison.Ordinal))
                {
                    _log.Warn(
                        "state-store",
                        $"Volume scan ignored '{candidatePath}': operation='{parsed.Operation}' " +
                        $"schemaVersion={parsed.SchemaVersion}; expected " +
                        $"'{RetirementState.RetireBoot1Operation}' and schema " +
                        $"{RetirementState.MinimumReadableSchemaVersion}-" +
                        $"{RetirementState.CurrentSchemaVersion}.");
                    continue;
                }

                // One volume can be reachable through several mount points; that is the same
                // file, not an ambiguity.
                if (candidates.Any(existing =>
                        VolumeIdentity.AreSameVolume(existing.Volume.VolumeGuidPath, volume.VolumeGuidPath)))
                {
                    continue;
                }

                candidates.Add(new ScannedStateFile(candidatePath, parsed, volume));
                break;
            }
        }

        if (candidates.Count == 0)
        {
            _log.Info(
                "state-store",
                $"Volume scan found no '{_folderName}\\{_stateFileName}' on any of the " +
                $"{located.Volumes.Count(volume => volume.IsFixed)} fixed volume(s)" +
                (skippedRemovable == 0
                    ? "."
                    : $" ({skippedRemovable} removable/other volume(s) were not scanned)."));
            return null;
        }

        if (candidates.Count > 1)
        {
            var ambiguity =
                $"{candidates.Count} different volumes hold a valid '{RetirementState.RetireBoot1Operation}' " +
                "state file, so CleanSwitch cannot tell which operation it is supposed to be driving." +
                Environment.NewLine +
                string.Join(
                    Environment.NewLine,
                    candidates.Select(candidate =>
                        $"  {candidate.Path} - status=" +
                        $"{RetirementStatusNames.ToWire(candidate.State.Status)} " +
                        $"created={candidate.State.CreatedAtUtc:u} on {candidate.Volume.Describe()}")) +
                Environment.NewLine +
                "Ambiguity must stop the flow rather than pick one. Delete the stale file(s), or set " +
                "CleanSwitch:RecoveryDataVolumeGptId to the GPT partition GUID of the volume that holds the " +
                "real one (see 'CleanSwitch.exe --list-volumes'), then run again.";

            // Logged here as well as thrown, because some callers only surface a short message.
            _log.Warn("state-store", ambiguity);
            throw new RetirementStorageException(ambiguity);
        }

        var found = candidates[0];

        _log.Warn(
            "state-store",
            "RETIREMENT STATE FILE FOUND BY VOLUME SCAN, NOT BY CONFIGURATION." +
            Environment.NewLine +
            $"  Configured location : {ConfiguredStateFilePath} (nothing there)" +
            Environment.NewLine +
            $"  Found instead       : {found.Path}" +
            Environment.NewLine +
            $"  On volume           : {found.Volume.Describe()}" +
            Environment.NewLine +
            "  Drive letters differ per Windows instance, which is the expected reason the configured path " +
            "missed. Set CleanSwitch:RecoveryDataVolumeGptId to " +
            $"{found.Volume.GptPartitionId ?? "the GPT partition GUID of that volume"} to make this " +
            "deterministic.");

        StateFilePath = found.Path;
        StateFileFoundByScan = true;
        StateVolumeIdentity = found.Volume.ToPartitionIdentity(
            "Volume holding the retirement state file, found by fixed-volume scan");
        _stateVolumeIsRunningWindows = found.Volume.IsRunningSystemVolume;

        return found;
    }

    private sealed record ScannedStateFile(string Path, RetirementState State, LocatedVolume Volume);

    /// <summary>
    /// Writes the state file atomically: serialise to a temp file in the same directory,
    /// flush it to disk, then replace the target in one filesystem operation. A crash can
    /// therefore leave the old file or the new file, never a half-written one.
    /// </summary>
    public void Save(RetirementState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (_accessContext == RetirementStateAccessContext.OperatorAbandon &&
            state.Status != RetirementStatus.Aborted)
        {
            throw new RetirementStorageException(
                "The operator-abandon state store can persist only the terminal ABORTED transition. " +
                "It cannot create, resume, or advance a retirement operation.");
        }

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
