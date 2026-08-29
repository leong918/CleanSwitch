using System.Runtime.InteropServices;
using System.Text;

namespace CleanSwitch.Recovery;

/// <summary>
/// Read-only Win32 volume lookups. These calls only query the mount-point table; they
/// never mount, unmount, format or otherwise modify a volume.
/// </summary>
internal static class VolumeIdentity
{
    private const int MaxPath = 260;

    /// <summary>
    /// Resolves an arbitrary path to the volume GUID path of the volume that hosts it,
    /// e.g. <c>C:\Users\x</c> -> <c>\\?\Volume{2eca078d-...}\</c>. Returns null when the
    /// path cannot be resolved (missing volume, network path, insufficient rights).
    /// </summary>
    public static string? TryGetVolumeGuidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var mountPoint = TryGetVolumeMountPoint(path);
        if (mountPoint is null)
        {
            return null;
        }

        var buffer = new StringBuilder(MaxPath);
        return GetVolumeNameForVolumeMountPointW(mountPoint, buffer, buffer.Capacity)
            ? buffer.ToString()
            : null;
    }

    /// <summary>
    /// Resolves the mount point (usually <c>X:\</c>) that a path lives under.
    /// </summary>
    public static string? TryGetVolumeMountPoint(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var buffer = new StringBuilder(MaxPath);
        if (!GetVolumePathNameW(path, buffer, buffer.Capacity))
        {
            return null;
        }

        var mountPoint = buffer.ToString();
        return mountPoint.EndsWith(Path.DirectorySeparatorChar) ? mountPoint : mountPoint + Path.DirectorySeparatorChar;
    }

    /// <summary>Volume GUID path of the volume that the currently running Windows boots from.</summary>
    public static string? TryGetRunningSystemVolumeGuidPath() =>
        TryGetVolumeGuidPath(Environment.SystemDirectory);

    public static bool AreSameVolume(string? leftVolumeGuidPath, string? rightVolumeGuidPath) =>
        !string.IsNullOrWhiteSpace(leftVolumeGuidPath) &&
        !string.IsNullOrWhiteSpace(rightVolumeGuidPath) &&
        string.Equals(
            leftVolumeGuidPath.TrimEnd('\\'),
            rightVolumeGuidPath.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(
        string lpszFileName,
        StringBuilder lpszVolumePathName,
        int cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(
        string lpszVolumeMountPoint,
        StringBuilder lpszVolumeName,
        int cchBufferLength);
}
