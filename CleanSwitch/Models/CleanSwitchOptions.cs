namespace CleanSwitch.Models;

public sealed class CleanSwitchOptions
{
    public const string DefaultStateFileName = "retirement-state.json";

    /// <summary>Optional. The other Windows loader to switch to when more than two exist.</summary>
    public string Boot2Guid { get; set; } = string.Empty;

    public string Boot1PartitionGptId { get; set; } = string.Empty;

    public string Boot2PartitionGptId { get; set; } = string.Empty;

    /// <summary>
    /// BCD identifier of the Windows Recovery Environment entry the RETIRE SYSTEM flow boots
    /// into. Find it with <c>bcdedit /enum all /v</c> from an elevated prompt. When empty,
    /// CleanSwitch tries the running entry's <c>recoverysequence</c> and fails loudly if that
    /// is also unavailable.
    /// </summary>
    public string RecoveryGuid { get; set; } = string.Empty;

    public int RestartDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Folder holding the retirement state file and logs. Must be on a volume that is NOT
    /// Boot 1, because Boot 1 is what the operation retires. No default is assumed: an empty
    /// value is an error rather than a silent fallback to <c>C:\</c>.
    /// </summary>
    public string RecoveryDataPath { get; set; } = string.Empty;

    /// <summary>
    /// GPT unique partition GUID of the volume that holds the retirement state file, e.g.
    /// <c>{7c1e2f3a-...}</c>. Optional but strongly recommended: drive letters and Win32
    /// volume GUIDs are assigned per Windows instance, so <c>D:</c> in Boot 1, in WinRE and
    /// in Boot 2 are three different volumes. The GPT partition GUID lives in the partition
    /// table on the disk, so it is identical in all three.
    /// <para>
    /// Find it with <c>CleanSwitch.exe --list-volumes</c>. When set, it wins over
    /// <see cref="RecoveryDataPath"/> and a missing volume is a hard error rather than a
    /// silent fallback to the letter path.
    /// </para>
    /// </summary>
    public string RecoveryDataVolumeGptId { get; set; } = string.Empty;

    /// <summary>
    /// Folder name on the volume identified by <see cref="RecoveryDataVolumeGptId"/>. Must be
    /// a plain name, not a rooted path. Defaults to the leaf of <see cref="RecoveryDataPath"/>
    /// (so <c>D:\CleanSwitchData</c> yields <c>CleanSwitchData</c>).
    /// </summary>
    public string RecoveryDataFolderName { get; set; } = string.Empty;

    public string StateFileName { get; set; } = DefaultStateFileName;

    /// <summary>Optional override. Defaults to <c>{RecoveryDataPath}\logs</c>.</summary>
    public string LogDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Test-only escape hatch that permits the state file to sit on the running Windows
    /// volume while constructing a new-operation store. Production configuration must leave
    /// this false; existing schema-v2 operations use persisted GPT identity validation instead.
    /// </summary>
    public bool AllowStateOnSystemVolume { get; set; }

    /// <summary>
    /// Reserved for Phase 2B. Even when true, nothing destructive runs: the executor is a
    /// stub that throws. Kept here so the authorisation shape is reviewable now.
    /// </summary>
    public bool EnableDestructiveRetirement { get; set; }

    /// <summary>
    /// Effective retirement data folder name: the explicit
    /// <see cref="RecoveryDataFolderName"/>, otherwise the leaf of
    /// <see cref="RecoveryDataPath"/>. Empty when neither yields one.
    /// </summary>
    public string ResolveRecoveryDataFolderName()
    {
        if (!string.IsNullOrWhiteSpace(RecoveryDataFolderName))
        {
            return RecoveryDataFolderName.Trim();
        }

        if (string.IsNullOrWhiteSpace(RecoveryDataPath))
        {
            return string.Empty;
        }

        var trimmed = RecoveryDataPath.Trim().TrimEnd('\\', '/');

        try
        {
            return Path.GetFileName(trimmed);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }
}
