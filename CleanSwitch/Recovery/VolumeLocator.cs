using System.Runtime.InteropServices;
using System.Text;
using CleanSwitch.Models;
using Microsoft.Win32.SafeHandles;

namespace CleanSwitch.Recovery;

/// <summary>
/// Owns a <c>FindFirstVolumeW</c> search handle. Declared at namespace scope because the
/// interop marshaller constructs it by reflection.
/// </summary>
internal sealed class SafeFindVolumeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeFindVolumeHandle()
        : base(true)
    {
    }

    protected override bool ReleaseHandle() => FindVolumeClose(handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindVolumeClose(IntPtr hFindVolume);
}

/// <summary>Win32 <c>GetDriveType</c> classification.</summary>
public enum VolumeDriveType
{
    Unknown = 0,
    NoRootDirectory = 1,
    Removable = 2,
    Fixed = 3,
    Remote = 4,
    CdRom = 5,
    RamDisk = 6
}

/// <summary>How far <see cref="VolumeLocator"/> got in identifying one volume.</summary>
public enum VolumeIdentityOutcome
{
    /// <summary>Disk number, partition number and GPT partition id are all known.</summary>
    Identified,

    /// <summary>Disk and partition number are known, but the disk is not GPT so there is no GPT id.</summary>
    NotGpt,

    /// <summary>
    /// The volume spans more than one disk extent (mirror, stripe, spanned). Deliberately
    /// treated as non-identifiable rather than guessing which extent "is" the volume.
    /// </summary>
    Spanned,

    /// <summary>The volume or its physical disk could not be opened for querying.</summary>
    AccessDenied,

    /// <summary>The query failed for some other reason; see <see cref="LocatedVolume.Diagnostic"/>.</summary>
    Unavailable
}

/// <summary>
/// One volume as seen by the running OS, together with the reboot-stable identity of the
/// partition backing it.
/// </summary>
public sealed class LocatedVolume
{
    public required string VolumeGuidPath { get; init; }

    /// <summary>Current mount points, e.g. <c>D:\</c>. Empty when the volume has no letter.</summary>
    public IReadOnlyList<string> MountPoints { get; init; } = [];

    public VolumeDriveType DriveType { get; init; }

    public int? DiskNumber { get; init; }

    public int? PartitionNumber { get; init; }

    /// <summary>GPT unique partition GUID, brace-formatted. The only identifier stable across OS instances.</summary>
    public string? GptPartitionId { get; init; }

    public Guid? GptPartitionGuid { get; init; }

    /// <summary>GPT partition type GUID (Basic Data, EFI System, Microsoft Reserved, ...).</summary>
    public Guid? GptPartitionType { get; init; }

    /// <summary>GPT unique disk GUID from DRIVE_LAYOUT_INFORMATION_GPT.DiskId.</summary>
    public Guid? DiskGptUniqueId { get; init; }

    public long? PartitionStartingOffset { get; init; }

    /// <summary>Size of the backing partition, from the partition table.</summary>
    public long? PartitionSizeBytes { get; init; }

    /// <summary>Size reported by the mounted filesystem, when one is mounted.</summary>
    public long? FileSystemSizeBytes { get; init; }

    public string? FileSystem { get; init; }

    public string? VolumeLabel { get; init; }

    public bool IsRunningSystemVolume { get; init; }

    public VolumeIdentityOutcome Outcome { get; init; }

    /// <summary>Explains a non-<see cref="VolumeIdentityOutcome.Identified"/> outcome.</summary>
    public string? Diagnostic { get; init; }

    public bool IsFixed => DriveType == VolumeDriveType.Fixed;

    public bool HasGptIdentity => GptPartitionGuid is not null;

    public string? PrimaryMountPoint => MountPoints.Count > 0 ? MountPoints[0] : null;

    public long? SizeBytes => PartitionSizeBytes ?? FileSystemSizeBytes;

    /// <summary>
    /// Projects this volume onto the identity model persisted in the retirement state file.
    /// Drive letters are carried only as informational text.
    /// </summary>
    public PartitionIdentity ToPartitionIdentity(string source) => new()
    {
        DiskNumber = DiskNumber,
        PartitionNumber = PartitionNumber,
        VolumeGuidPath = VolumeGuidPath,
        GptPartitionId = GptPartitionId,
        GptPartitionType = GptPartitionType is null ? null : VolumeLocator.FormatGptId(GptPartitionType.Value),
        DiskGptUniqueId = DiskGptUniqueId is null ? null : VolumeLocator.FormatGptId(DiskGptUniqueId.Value),
        PartitionStartingOffset = PartitionStartingOffset,
        PartitionSizeBytes = PartitionSizeBytes,
        ObservedDriveLetter = PrimaryMountPoint,
        Source = source
    };

    public string Describe()
    {
        var mounts = MountPoints.Count == 0 ? "(no mount point)" : string.Join(", ", MountPoints);
        return
            $"disk={DiskNumber?.ToString() ?? "?"} partition={PartitionNumber?.ToString() ?? "?"} " +
            $"gptId={GptPartitionId ?? "(none)"} size={FormatSize(SizeBytes)} fs={FileSystem ?? "(unknown)"} " +
            $"mounts={mounts} type={DriveType} volume={VolumeGuidPath}" +
            (IsRunningSystemVolume ? " [RUNNING SYSTEM VOLUME]" : string.Empty) +
            (Outcome == VolumeIdentityOutcome.Identified ? string.Empty : $" outcome={Outcome}: {Diagnostic}");
    }

    /// <summary>
    /// Decimal GB/MB, i.e. the same convention <c>Get-Partition</c> and the documented
    /// partition table for this machine use, so the numbers can be compared directly.
    /// </summary>
    public static string FormatSize(long? bytes) =>
        bytes is null or < 0
            ? "?"
            : bytes >= 1_000_000_000
                ? $"{bytes.Value / 1_000_000_000.0:0.##} GB"
                : $"{bytes.Value / 1_000_000.0:0.##} MB";
}

/// <summary>One GPT partition table row. Exists even when the partition has no volume.</summary>
public sealed class LocatedGptPartition
{
    public required int DiskNumber { get; init; }

    public Guid? DiskGptUniqueId { get; init; }

    public required int PartitionNumber { get; init; }

    public required Guid GptPartitionId { get; init; }

    public Guid? GptPartitionType { get; init; }

    public required long StartingOffset { get; init; }

    public required long SizeBytes { get; init; }
}

/// <summary>Outcome of one full enumeration pass. Never partial without saying so.</summary>
public sealed class VolumeLocatorResult
{
    public VolumeLocatorResult(IReadOnlyList<LocatedVolume> volumes, IReadOnlyList<string> warnings)
    {
        Volumes = volumes;
        Warnings = warnings;
    }

    public IReadOnlyList<LocatedVolume> Volumes { get; }

    /// <summary>Enumeration-level problems (as opposed to per-volume ones).</summary>
    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyList<LocatedVolume> WithGptPartitionId(Guid gptPartitionId) =>
        Volumes.Where(volume => volume.GptPartitionGuid == gptPartitionId).ToList();

    public IReadOnlyList<LocatedVolume> Identifiable =>
        Volumes.Where(volume => volume.HasGptIdentity).ToList();
}

/// <summary>
/// Read-only enumeration of every volume on this machine, mapped to the physical disk and
/// GPT partition that backs it.
/// <para>
/// Why this exists: drive letters and Win32 volume GUIDs are both assigned per Windows
/// instance. WinPE's Mount Manager mints its own volume GUIDs, so <c>\\?\Volume{...}</c>
/// is not stable across OS instances either. The GPT unique partition GUID lives in the
/// partition table on the disk itself, so it is identical from Boot 1, from WinRE and from
/// Boot 2. It is the only identifier the retirement flow can key on.
/// </para>
/// <para>
/// P/Invoke only, deliberately: no WMI and no <c>System.Management</c>, because the WMI
/// storage provider is frequently unavailable in WinPE, which is exactly where this has to
/// work.
/// </para>
/// <para>
/// SAFETY: every call here is a query. <c>CreateFile</c> is called with zero desired access
/// (query-only), and the only two IOCTLs issued —
/// <c>IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS</c> and <c>IOCTL_DISK_GET_DRIVE_LAYOUT_EX</c> —
/// read the partition table. Nothing is written, mounted, unmounted or formatted.
/// </para>
/// </summary>
public static class VolumeLocator
{
    /// <summary>Formats a GUID the way CleanSwitch configuration and logs expect it.</summary>
    public static string FormatGptId(Guid gptPartitionId) => $"{{{gptPartitionId:D}}}";

    /// <summary>Accepts a GPT partition GUID with or without braces.</summary>
    public static bool TryParseGptId(string? raw, out Guid gptPartitionId)
    {
        gptPartitionId = Guid.Empty;
        return !string.IsNullOrWhiteSpace(raw) && Guid.TryParse(raw.Trim(), out gptPartitionId);
    }

    /// <summary>
    /// Finds exactly one volume whose GPT unique partition GUID matches.
    /// Zero matches or two-or-more matches both fail: ambiguity must not pick a target.
    /// </summary>
    public static LocatedVolume? TryFindUniqueByGptId(Guid gptPartitionId, out string? error)
    {
        var located = Enumerate();
        var matches = located.WithGptPartitionId(gptPartitionId);
        if (matches.Count == 0)
        {
            error =
                $"No volume on this machine has GPT partition GUID {FormatGptId(gptPartitionId)}. " +
                "The partition may be missing, or this environment cannot read the partition table.";
            return null;
        }

        if (matches.Count > 1)
        {
            var listed = string.Join("; ", matches.Select(volume => volume.Describe()));
            error =
                $"GPT partition GUID {FormatGptId(gptPartitionId)} matched {matches.Count} volumes. " +
                "Refusing to choose. Matches: " + listed;
            return null;
        }

        error = null;
        return matches[0];
    }

    /// <summary>
    /// Reads every GPT partition on one disk from the partition table, including
    /// partitions that have no volume (MSR). Query-only.
    /// </summary>
    public static IReadOnlyList<LocatedGptPartition> ReadGptTable(int diskNumber)
    {
        var layout = ReadDriveLayout(diskNumber);
        if (layout.Error is not null)
        {
            return [];
        }

        return layout.Partitions
            .Where(partition => partition.GptPartitionId is not null)
            .Select(partition => new LocatedGptPartition
            {
                DiskNumber = diskNumber,
                DiskGptUniqueId = layout.DiskGptUniqueId,
                PartitionNumber = partition.PartitionNumber,
                GptPartitionId = partition.GptPartitionId!.Value,
                GptPartitionType = partition.GptPartitionType,
                StartingOffset = partition.StartingOffset,
                SizeBytes = partition.PartitionLength
            })
            .ToList();
    }

    /// <summary>
    /// Enumerates all volumes. This never throws: a volume that cannot be identified is
    /// returned with a non-<see cref="VolumeIdentityOutcome.Identified"/> outcome and a
    /// diagnostic, so callers can report precisely what failed.
    /// </summary>
    public static VolumeLocatorResult Enumerate()
    {
        var volumes = new List<LocatedVolume>();
        var warnings = new List<string>();

        // Stops Windows popping "There is no disk in the drive" for empty card readers
        // while we probe removable volumes.
        var errorModeChanged = SetThreadErrorMode(SemFailCriticalErrors, out var previousErrorMode);

        try
        {
            var systemVolume = VolumeIdentity.TryGetRunningSystemVolumeGuidPath();
            var layouts = new Dictionary<int, DiskLayout>();

            foreach (var volumeGuidPath in EnumerateVolumeGuidPaths(warnings))
            {
                try
                {
                    var driveType = (VolumeDriveType)GetDriveTypeW(volumeGuidPath);
                    if (driveType is VolumeDriveType.CdRom or VolumeDriveType.Remote)
                    {
                        continue;
                    }

                    volumes.Add(DescribeVolume(volumeGuidPath, driveType, systemVolume, layouts));
                }
                catch (Exception exception)
                {
                    warnings.Add($"Volume '{volumeGuidPath}' could not be described: {exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            warnings.Add($"Volume enumeration stopped early: {exception.Message}");
        }
        finally
        {
            if (errorModeChanged)
            {
                SetThreadErrorMode(previousErrorMode, out _);
            }
        }

        return new VolumeLocatorResult(volumes, warnings);
    }

    private static LocatedVolume DescribeVolume(
        string volumeGuidPath,
        VolumeDriveType driveType,
        string? systemVolumeGuidPath,
        Dictionary<int, DiskLayout> layouts)
    {
        var mountPoints = GetMountPoints(volumeGuidPath);
        var (fileSystem, label, fileSystemSize) = GetFileSystemInformation(volumeGuidPath);
        var isSystemVolume = VolumeIdentity.AreSameVolume(volumeGuidPath, systemVolumeGuidPath);

        var extents = GetVolumeDiskExtents(volumeGuidPath);

        if (extents.Error is not null)
        {
            return new LocatedVolume
            {
                VolumeGuidPath = volumeGuidPath,
                MountPoints = mountPoints,
                DriveType = driveType,
                FileSystem = fileSystem,
                VolumeLabel = label,
                FileSystemSizeBytes = fileSystemSize,
                IsRunningSystemVolume = isSystemVolume,
                Outcome = extents.AccessDenied ? VolumeIdentityOutcome.AccessDenied : VolumeIdentityOutcome.Unavailable,
                Diagnostic = extents.Error
            };
        }

        if (extents.Extents.Count != 1)
        {
            return new LocatedVolume
            {
                VolumeGuidPath = volumeGuidPath,
                MountPoints = mountPoints,
                DriveType = driveType,
                FileSystem = fileSystem,
                VolumeLabel = label,
                FileSystemSizeBytes = fileSystemSize,
                IsRunningSystemVolume = isSystemVolume,
                Outcome = VolumeIdentityOutcome.Spanned,
                Diagnostic =
                    $"The volume reports {extents.Extents.Count} disk extents, so it is not backed by exactly one " +
                    "partition. CleanSwitch will not guess which extent identifies it."
            };
        }

        var extent = extents.Extents[0];
        var diskNumber = (int)extent.DiskNumber;

        if (!layouts.TryGetValue(diskNumber, out var layout))
        {
            layout = ReadDriveLayout(diskNumber);
            layouts[diskNumber] = layout;
        }

        if (layout.Error is not null)
        {
            return new LocatedVolume
            {
                VolumeGuidPath = volumeGuidPath,
                MountPoints = mountPoints,
                DriveType = driveType,
                DiskNumber = diskNumber,
                PartitionStartingOffset = extent.StartingOffset,
                PartitionSizeBytes = extent.ExtentLength,
                FileSystem = fileSystem,
                VolumeLabel = label,
                FileSystemSizeBytes = fileSystemSize,
                IsRunningSystemVolume = isSystemVolume,
                Outcome = layout.AccessDenied ? VolumeIdentityOutcome.AccessDenied : VolumeIdentityOutcome.Unavailable,
                Diagnostic = layout.Error
            };
        }

        var partition = layout.Partitions
            .FirstOrDefault(candidate => candidate.StartingOffset == extent.StartingOffset);

        if (partition is null)
        {
            return new LocatedVolume
            {
                VolumeGuidPath = volumeGuidPath,
                MountPoints = mountPoints,
                DriveType = driveType,
                DiskNumber = diskNumber,
                PartitionStartingOffset = extent.StartingOffset,
                PartitionSizeBytes = extent.ExtentLength,
                FileSystem = fileSystem,
                VolumeLabel = label,
                FileSystemSizeBytes = fileSystemSize,
                IsRunningSystemVolume = isSystemVolume,
                Outcome = VolumeIdentityOutcome.Unavailable,
                Diagnostic =
                    $"No partition on disk {diskNumber} starts at byte offset {extent.StartingOffset}, so the " +
                    "volume could not be matched to a partition table entry."
            };
        }

        var isGpt = layout.PartitionStyle == PartitionStyleGpt && partition.GptPartitionId is not null;

        return new LocatedVolume
        {
            VolumeGuidPath = volumeGuidPath,
            MountPoints = mountPoints,
            DriveType = driveType,
            DiskNumber = diskNumber,
            PartitionNumber = partition.PartitionNumber,
            GptPartitionGuid = isGpt ? partition.GptPartitionId : null,
            GptPartitionId = isGpt ? FormatGptId(partition.GptPartitionId!.Value) : null,
            GptPartitionType = isGpt ? partition.GptPartitionType : null,
            DiskGptUniqueId = layout.DiskGptUniqueId,
            PartitionStartingOffset = partition.StartingOffset,
            PartitionSizeBytes = partition.PartitionLength,
            FileSystem = fileSystem,
            VolumeLabel = label,
            FileSystemSizeBytes = fileSystemSize,
            IsRunningSystemVolume = isSystemVolume,
            Outcome = isGpt ? VolumeIdentityOutcome.Identified : VolumeIdentityOutcome.NotGpt,
            Diagnostic = isGpt
                ? null
                : $"Disk {diskNumber} uses the {DescribePartitionStyle(layout.PartitionStyle)} partition style, " +
                  "which has no GPT unique partition GUID. Disk and partition number are still reported."
        };
    }

    private static List<string> EnumerateVolumeGuidPaths(List<string> warnings)
    {
        var found = new List<string>();
        var buffer = new StringBuilder(MaxVolumeNameLength);

        using var search = FindFirstVolumeW(buffer, buffer.Capacity);
        if (search.IsInvalid)
        {
            warnings.Add($"FindFirstVolumeW failed with Win32 error {Marshal.GetLastWin32Error()}. No volume could be enumerated.");
            return found;
        }

        found.Add(buffer.ToString());

        while (true)
        {
            buffer.Clear();
            buffer.EnsureCapacity(MaxVolumeNameLength);

            if (FindNextVolumeW(search, buffer, buffer.Capacity))
            {
                found.Add(buffer.ToString());
                continue;
            }

            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNoMoreFiles)
            {
                warnings.Add($"FindNextVolumeW failed with Win32 error {error}; the volume list may be incomplete.");
            }

            return found;
        }
    }

    private static IReadOnlyList<string> GetMountPoints(string volumeGuidPath)
    {
        var capacity = 512;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new char[capacity];
            if (GetVolumePathNamesForVolumeNameW(volumeGuidPath, buffer, buffer.Length, out var required))
            {
                return SplitMultiString(buffer);
            }

            if (Marshal.GetLastWin32Error() != ErrorMoreData)
            {
                return [];
            }

            capacity = Math.Max(required + 2, capacity * 2);
        }

        return [];
    }

    private static List<string> SplitMultiString(char[] buffer)
    {
        var values = new List<string>();
        var start = 0;

        for (var index = 0; index < buffer.Length; index++)
        {
            if (buffer[index] != '\0')
            {
                continue;
            }

            if (index == start)
            {
                break;
            }

            values.Add(new string(buffer, start, index - start));
            start = index + 1;
        }

        return values;
    }

    private static (string? FileSystem, string? Label, long? SizeBytes) GetFileSystemInformation(string volumeGuidPath)
    {
        string? fileSystem = null;
        string? label = null;
        long? size = null;

        var labelBuffer = new StringBuilder(MaxPath + 1);
        var fileSystemBuffer = new StringBuilder(MaxPath + 1);

        if (GetVolumeInformationW(
                volumeGuidPath,
                labelBuffer,
                labelBuffer.Capacity,
                out _,
                out _,
                out _,
                fileSystemBuffer,
                fileSystemBuffer.Capacity))
        {
            fileSystem = NullIfEmpty(fileSystemBuffer.ToString());
            label = NullIfEmpty(labelBuffer.ToString());
        }

        if (GetDiskFreeSpaceExW(volumeGuidPath, out _, out var totalBytes, out _))
        {
            size = (long)Math.Min(totalBytes, long.MaxValue);
        }

        return (fileSystem, label, size);
    }

    private static ExtentLookup GetVolumeDiskExtents(string volumeGuidPath)
    {
        // CreateFile on a volume requires the path without the trailing backslash.
        var devicePath = volumeGuidPath.TrimEnd('\\');

        using var handle = OpenForQuery(devicePath, out var openError);
        if (handle is null)
        {
            return ExtentLookup.Failed(
                $"Could not open volume '{devicePath}' to read its disk extents (Win32 error {openError}).",
                IsAccessError(openError));
        }

        // 8-byte header, then one 24-byte DISK_EXTENT per extent.
        var extentCapacity = 8;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = new byte[VolumeDiskExtentsHeaderSize + (extentCapacity * DiskExtentSize)];

            if (DeviceIoControl(
                    handle,
                    IoctlVolumeGetVolumeDiskExtents,
                    IntPtr.Zero,
                    0,
                    buffer,
                    (uint)buffer.Length,
                    out var returned,
                    IntPtr.Zero))
            {
                return ExtentLookup.Success(ParseExtents(buffer, (int)returned));
            }

            var error = Marshal.GetLastWin32Error();
            if (error is ErrorInsufficientBuffer or ErrorMoreData)
            {
                extentCapacity *= 4;
                continue;
            }

            return ExtentLookup.Failed(
                $"IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS failed for '{devicePath}' with Win32 error {error}.",
                IsAccessError(error));
        }

        return ExtentLookup.Failed(
            $"IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS for '{devicePath}' kept reporting an insufficient buffer.",
            false);
    }

    private static List<DiskExtent> ParseExtents(byte[] buffer, int bytesReturned)
    {
        var extents = new List<DiskExtent>();
        if (bytesReturned < VolumeDiskExtentsHeaderSize)
        {
            return extents;
        }

        var count = BitConverter.ToInt32(buffer, 0);
        var available = (bytesReturned - VolumeDiskExtentsHeaderSize) / DiskExtentSize;
        count = Math.Clamp(count, 0, available);

        for (var index = 0; index < count; index++)
        {
            var offset = VolumeDiskExtentsHeaderSize + (index * DiskExtentSize);
            extents.Add(new DiskExtent(
                BitConverter.ToUInt32(buffer, offset + DiskExtentDiskNumberOffset),
                BitConverter.ToInt64(buffer, offset + DiskExtentStartingOffsetOffset),
                BitConverter.ToInt64(buffer, offset + DiskExtentLengthOffset)));
        }

        return extents;
    }

    private static DiskLayout ReadDriveLayout(int diskNumber)
    {
        var devicePath = $@"\\.\PHYSICALDRIVE{diskNumber}";

        using var handle = OpenForQuery(devicePath, out var openError);
        if (handle is null)
        {
            return DiskLayout.Failed(
                $"Could not open '{devicePath}' to read its partition table (Win32 error {openError}). " +
                "Reading the partition table needs an elevated process.",
                IsAccessError(openError));
        }

        var partitionCapacity = 32;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = new byte[DriveLayoutHeaderSize + (partitionCapacity * PartitionInformationExSize)];

            if (DeviceIoControl(
                    handle,
                    IoctlDiskGetDriveLayoutEx,
                    IntPtr.Zero,
                    0,
                    buffer,
                    (uint)buffer.Length,
                    out var returned,
                    IntPtr.Zero))
            {
                return ParseDriveLayout(buffer, (int)returned);
            }

            var error = Marshal.GetLastWin32Error();
            if (error is ErrorInsufficientBuffer or ErrorMoreData)
            {
                partitionCapacity *= 4;
                continue;
            }

            return DiskLayout.Failed(
                $"IOCTL_DISK_GET_DRIVE_LAYOUT_EX failed for '{devicePath}' with Win32 error {error}.",
                IsAccessError(error));
        }

        return DiskLayout.Failed(
            $"IOCTL_DISK_GET_DRIVE_LAYOUT_EX for '{devicePath}' kept reporting an insufficient buffer.",
            false);
    }

    private static DiskLayout ParseDriveLayout(byte[] buffer, int bytesReturned)
    {
        if (bytesReturned < DriveLayoutHeaderSize)
        {
            return DiskLayout.Failed(
                $"IOCTL_DISK_GET_DRIVE_LAYOUT_EX returned {bytesReturned} bytes, which is smaller than the " +
                $"{DriveLayoutHeaderSize}-byte DRIVE_LAYOUT_INFORMATION_EX header.",
                false);
        }

        var style = BitConverter.ToInt32(buffer, 0);
        var declaredCount = BitConverter.ToInt32(buffer, 4);
        Guid? diskGptId = null;
        if (style == PartitionStyleGpt && bytesReturned >= 24)
        {
            var parsed = ReadGuid(buffer, 8);
            if (parsed != Guid.Empty)
            {
                diskGptId = parsed;
            }
        }

        var available = (bytesReturned - DriveLayoutHeaderSize) / PartitionInformationExSize;
        var count = Math.Clamp(declaredCount, 0, available);

        var partitions = new List<PartitionTableEntry>(count);

        for (var index = 0; index < count; index++)
        {
            var offset = DriveLayoutHeaderSize + (index * PartitionInformationExSize);
            var entryStyle = BitConverter.ToInt32(buffer, offset + PartitionStyleOffset);
            var startingOffset = BitConverter.ToInt64(buffer, offset + PartitionStartingOffsetOffset);
            var length = BitConverter.ToInt64(buffer, offset + PartitionLengthOffset);
            var number = BitConverter.ToInt32(buffer, offset + PartitionNumberOffset);

            Guid? gptId = null;
            Guid? gptType = null;

            if (entryStyle == PartitionStyleGpt)
            {
                gptType = ReadGuid(buffer, offset + GptPartitionTypeOffset);
                gptId = ReadGuid(buffer, offset + GptPartitionIdOffset);

                if (gptId == Guid.Empty)
                {
                    gptId = null;
                }
            }

            partitions.Add(new PartitionTableEntry(number, startingOffset, length, gptId, gptType));
        }

        return DiskLayout.Success(style, diskGptId, partitions);
    }

    private static Guid ReadGuid(byte[] buffer, int offset) =>
        new(buffer.AsSpan(offset, 16));

    private static SafeFileHandle? OpenForQuery(string devicePath, out int win32Error)
    {
        // Zero desired access is the query-only form: it is enough for the two read IOCTLs
        // used here and does not request read or write access to the device contents.
        var handle = CreateFileW(
            devicePath,
            0,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        win32Error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;

        if (!handle.IsInvalid)
        {
            return handle;
        }

        handle.Dispose();
        return null;
    }

    private static bool IsAccessError(int win32Error) =>
        win32Error is ErrorAccessDenied or ErrorSharingViolation;

    private static string DescribePartitionStyle(int style) => style switch
    {
        PartitionStyleMbr => "MBR",
        PartitionStyleGpt => "GPT",
        PartitionStyleRaw => "RAW",
        _ => $"unknown ({style})"
    };

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record DiskExtent(uint DiskNumber, long StartingOffset, long ExtentLength);

    private sealed record PartitionTableEntry(
        int PartitionNumber,
        long StartingOffset,
        long PartitionLength,
        Guid? GptPartitionId,
        Guid? GptPartitionType);

    private sealed class ExtentLookup
    {
        private ExtentLookup(IReadOnlyList<DiskExtent> extents, string? error, bool accessDenied)
        {
            Extents = extents;
            Error = error;
            AccessDenied = accessDenied;
        }

        public IReadOnlyList<DiskExtent> Extents { get; }

        public string? Error { get; }

        public bool AccessDenied { get; }

        public static ExtentLookup Success(IReadOnlyList<DiskExtent> extents) => new(extents, null, false);

        public static ExtentLookup Failed(string error, bool accessDenied) => new([], error, accessDenied);
    }

    private sealed class DiskLayout
    {
        private DiskLayout(
            int partitionStyle,
            Guid? diskGptUniqueId,
            IReadOnlyList<PartitionTableEntry> partitions,
            string? error,
            bool accessDenied)
        {
            PartitionStyle = partitionStyle;
            DiskGptUniqueId = diskGptUniqueId;
            Partitions = partitions;
            Error = error;
            AccessDenied = accessDenied;
        }

        public int PartitionStyle { get; }

        public Guid? DiskGptUniqueId { get; }

        public IReadOnlyList<PartitionTableEntry> Partitions { get; }

        public string? Error { get; }

        public bool AccessDenied { get; }

        public static DiskLayout Success(
            int partitionStyle,
            Guid? diskGptUniqueId,
            IReadOnlyList<PartitionTableEntry> partitions) =>
            new(partitionStyle, diskGptUniqueId, partitions, null, false);

        public static DiskLayout Failed(string error, bool accessDenied) =>
            new(PartitionStyleRaw, null, [], error, accessDenied);
    }

    // ---- Win32 constants -------------------------------------------------------------

    private const int MaxPath = 260;

    /// <summary>
    /// A volume GUID path is 49 characters (<c>\\?\Volume{GUID}\</c>) plus the terminator,
    /// but MSDN specifies MAX_PATH for these buffers, so use that.
    /// </summary>
    private const int MaxVolumeNameLength = MaxPath;

    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;

    private const uint SemFailCriticalErrors = 0x0001;

    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    // CTL_CODE(IOCTL_VOLUME_BASE=0x56, 0x0000, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

    // CTL_CODE(IOCTL_DISK_BASE=0x07, 0x0014, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint IoctlDiskGetDriveLayoutEx = 0x00070050;

    private const int PartitionStyleMbr = 0;
    private const int PartitionStyleGpt = 1;
    private const int PartitionStyleRaw = 2;

    // VOLUME_DISK_EXTENTS { DWORD NumberOfDiskExtents; DISK_EXTENT Extents[1]; }
    // DISK_EXTENT has 8-byte alignment, so the array starts at offset 8, not 4.
    private const int VolumeDiskExtentsHeaderSize = 8;

    // DISK_EXTENT { DWORD DiskNumber; LARGE_INTEGER StartingOffset; LARGE_INTEGER ExtentLength; }
    private const int DiskExtentSize = 24;
    private const int DiskExtentDiskNumberOffset = 0;
    private const int DiskExtentStartingOffsetOffset = 8;
    private const int DiskExtentLengthOffset = 16;

    // DRIVE_LAYOUT_INFORMATION_EX { DWORD PartitionStyle; DWORD PartitionCount;
    //   union { DRIVE_LAYOUT_INFORMATION_MBR (8); DRIVE_LAYOUT_INFORMATION_GPT (40); } (at 8);
    //   PARTITION_INFORMATION_EX PartitionEntry[1] (at 48); }
    private const int DriveLayoutHeaderSize = 48;

    // PARTITION_INFORMATION_EX { PARTITION_STYLE (0); LARGE_INTEGER StartingOffset (8);
    //   LARGE_INTEGER PartitionLength (16); DWORD PartitionNumber (24);
    //   BOOLEAN RewritePartition (28); BOOLEAN IsServicePartition (29);
    //   union { PARTITION_INFORMATION_MBR (24 bytes); PARTITION_INFORMATION_GPT (112 bytes); } (at 32) }
    private const int PartitionInformationExSize = 144;
    private const int PartitionStyleOffset = 0;
    private const int PartitionStartingOffsetOffset = 8;
    private const int PartitionLengthOffset = 16;
    private const int PartitionNumberOffset = 24;

    // PARTITION_INFORMATION_GPT { GUID PartitionType (0); GUID PartitionId (16);
    //   DWORD64 Attributes (32); WCHAR Name[36] (40); }  relative to the union at offset 32.
    private const int GptPartitionTypeOffset = 32;
    private const int GptPartitionIdOffset = 48;

    // ---- Win32 imports ---------------------------------------------------------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFindVolumeHandle FindFirstVolumeW(
        StringBuilder lpszVolumeName,
        int cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextVolumeW(
        SafeFindVolumeHandle hFindVolume,
        StringBuilder lpszVolumeName,
        int cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNamesForVolumeNameW(
        string lpszVolumeName,
        [Out] char[] lpszVolumePathNames,
        int cchBufferLength,
        out int lpcchReturnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetDriveTypeW(string lpRootPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName,
        StringBuilder lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        [Out] byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadErrorMode(uint dwNewMode, out uint lpOldMode);
}
