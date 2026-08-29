namespace CleanSwitch.Models;

public sealed class CleanSwitchOptions
{
    public const string DefaultStateFileName = "retirement-state.json";

    /// <summary>Optional. The other Windows loader to switch to when more than two exist.</summary>
    public string Boot2Guid { get; set; } = string.Empty;

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

    public string StateFileName { get; set; } = DefaultStateFileName;

    /// <summary>Optional override. Defaults to <c>{RecoveryDataPath}\logs</c>.</summary>
    public string LogDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Test-only escape hatch that permits the state file to sit on the running Windows
    /// volume. Only ever safe for the non-destructive Phase 2A handoff test.
    /// </summary>
    public bool AllowStateOnSystemVolume { get; set; }

    /// <summary>
    /// Reserved for Phase 2B. Even when true, nothing destructive runs: the executor is a
    /// stub that throws. Kept here so the authorisation shape is reviewable now.
    /// </summary>
    public bool EnableDestructiveRetirement { get; set; }
}
