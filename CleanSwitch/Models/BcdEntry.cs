namespace CleanSwitch.Models;

/// <summary>
/// A single entry parsed out of <c>bcdedit /enum ... /v</c> output.
/// </summary>
public sealed record BcdEntry(
    string Identifier,
    string Description,
    string Path,
    string Device,
    string OsDevice,
    string RecoverySequence,
    string ResumeObject,
    string Type,
    string SystemRoot = "")
{
    public bool IsWindowsLoader =>
        Path.Contains("winload", StringComparison.OrdinalIgnoreCase);

    public bool IsResumeLoader =>
        Path.Contains("winresume", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A WinRE loader boots from a ramdisk-backed <c>winre.wim</c> and is normally
    /// described as "Windows Recovery Environment".
    /// </summary>
    public bool LooksLikeRecoveryEnvironment =>
        Device.Contains("ramdisk", StringComparison.OrdinalIgnoreCase) ||
        Device.Contains("winre", StringComparison.OrdinalIgnoreCase) ||
        OsDevice.Contains("ramdisk", StringComparison.OrdinalIgnoreCase) ||
        OsDevice.Contains("winre", StringComparison.OrdinalIgnoreCase) ||
        Description.Contains("Recovery", StringComparison.OrdinalIgnoreCase);

    public string Describe() =>
        $"{(string.IsNullOrWhiteSpace(Description) ? "<no description>" : Description)} ({Identifier})" +
        $" path={(string.IsNullOrWhiteSpace(Path) ? "<none>" : Path)}" +
        $" device={(string.IsNullOrWhiteSpace(Device) ? "<none>" : Device)}";
}
